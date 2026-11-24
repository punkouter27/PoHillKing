"""Motion prior (adversarial motion priors, Peng et al. 2021) as an opt-in style reward: train.py --amp <dataset.npz>.

A discriminator learns to tell pairs of consecutive frames of human motion capture (koth/mocap.py) from pairs the
learner produces; the learner earns a style reward for the pairs the discriminator takes for human:

    r_style = clamp(1 - 0.25 (D(s, s') - 1)^2, 0, 1)        added as  weight * ctrl_dt * r_style  per step

It is added to the env's reward after its own clip, like the other bonus terms, and before rsl_rl stores the step, so
GAE sees it. The discriminator trains on the least-squares GAN loss with a gradient penalty on the reference data, a
few minibatches per PPO update, on a ring buffer of the learner's recent pairs.

What it does not change: the observations, the actions, the action path, the timing, the physics, the rules. A brain
trained with it is a drop-in candidate for the same Unity runner, and ships only through the existing gates.

Nothing here runs unless train.py is given --amp.
"""

from __future__ import annotations

import os

import numpy as np
import torch

from koth.env import terrain_height
from koth.mocap import FEAT, STYLE_IDX
from koth.obs import down_like, quat_rotate_inverse


def env_features(env) -> torch.Tensor:
    """(M, FEAT) frame features of every robot row, the layout of koth/mocap.py. No side effects on the env."""
    q = env._flat(env.qpos[:, env.rq7])
    v = env._flat(env.qvel[:, env.rd6])
    pos, quat, linv_w, angv = q[:, :3], q[:, 3:7], v[:, :3], v[:, 3:6]
    jpos, jvel = env._joints()
    stand = env.key_root_z.repeat(env.N).unsqueeze(1)
    h = (pos[:, 2:3] - terrain_height(pos[:, :2], env.arena).unsqueeze(1)) / stand
    idx = torch.as_tensor(STYLE_IDX, device=jpos.device)
    return torch.cat(
        [
            h,
            quat_rotate_inverse(quat, down_like(quat)),
            quat_rotate_inverse(quat, linv_w) / stand,
            angv,
            jpos[:, idx],
            jvel[:, idx],
        ],
        1,
    )


class Discriminator(torch.nn.Module):
    def __init__(self, mean: torch.Tensor, std: torch.Tensor, hidden=(512, 256)):
        super().__init__()
        self.register_buffer("mean", mean.clone())
        self.register_buffer("std", std.clone().clamp(min=0.05))
        layers, n = [], mean.numel()
        for h in hidden:
            layers += [torch.nn.Linear(n, h), torch.nn.ReLU()]
            n = h
        self.trunk = torch.nn.Sequential(*layers)
        self.head = torch.nn.Linear(n, 1)

    def forward(self, x):
        return self.head(self.trunk(((x - self.mean) / self.std).clamp(-8, 8))).squeeze(-1)


def style_reward(d: torch.Tensor) -> torch.Tensor:
    return (1.0 - 0.25 * (d - 1.0) ** 2).clamp(0.0, 1.0)


class MotionPrior:
    """Wraps a KothEnv's step (style reward on the learner rows) and an rsl_rl PPO's update (discriminator training)."""

    def __init__(
        self,
        env,
        dataset: str,
        weight: float = 1.0,
        batch: int = 4096,
        updates: int = 2,
        buffer: int = 200_000,
        lr: float = 1e-4,
        grad_penalty: float = 5.0,
        seed: int = 0,
    ):
        z = np.load(dataset)
        assert z["pairs"].shape[1] == 2 * FEAT, (
            f"{dataset}: built for another feature layout ({z['pairs'].shape[1]} != {2 * FEAT})"
        )
        assert abs(float(z["dt"]) - env.ctrl_dt) < 1e-6, (
            f"{dataset} is sampled at {float(z['dt'])} s, the env steps at {env.ctrl_dt} s"
        )
        from koth.env import ROBOT

        robot = str(z["robot"]) if "robot" in z.files else ""
        assert robot == ROBOT, (
            f"{dataset} was retargeted to '{robot}', this run trains '{ROBOT}' (scripts/fetch_mocap.py builds one per body)"
        )
        dev = env.device
        self.env, self.weight, self.batch, self.updates, self.gp = (
            env,
            float(weight),
            int(batch),
            int(updates),
            float(grad_penalty),
        )
        self.expert = torch.as_tensor(z["pairs"], dtype=torch.float32, device=dev)
        self.disc = Discriminator(
            torch.as_tensor(z["mean"], dtype=torch.float32, device=dev),
            torch.as_tensor(z["std"], dtype=torch.float32, device=dev),
        ).to(dev)
        self.opt = torch.optim.Adam(
            [
                dict(params=self.disc.trunk.parameters(), weight_decay=1e-4),
                dict(params=self.disc.head.parameters(), weight_decay=1e-2),
            ],
            lr=lr,
        )
        self.buf = torch.zeros(int(buffer), 2 * FEAT, device=dev)
        self.head = 0
        self.filled = 0
        self.gen = torch.Generator(device=dev)
        self.gen.manual_seed(seed)
        self.rows = env.lrows if env.frozen is not None else slice(None)  # the rows whose reward the env returns
        self.prev = None
        self.stats = dict(d_expert=0.0, d_policy=0.0, loss=0.0, reward=0.0, nan_updates=0, updates=0)

    # ---------------------------------------------------------------- reward
    def _store(self, x: torch.Tensor):
        n = len(x)
        if n == 0:
            return
        if n >= len(self.buf):
            x = x[-len(self.buf) :]
            n = len(x)
        end = self.head + n
        if end <= len(self.buf):
            self.buf[self.head : end] = x
        else:
            k = len(self.buf) - self.head
            self.buf[self.head :] = x[:k]
            self.buf[: n - k] = x[k:]
        self.head = end % len(self.buf)
        self.filled = min(len(self.buf), self.filled + n)

    def pairs(self, cur: torch.Tensor, done: torch.Tensor):
        """(prev, cur) on the learner rows and which of them are real transitions: a row that was just reset holds the
        first frame of a new episode, so its pair would join two unrelated states."""
        valid = ~done.bool() & torch.isfinite(cur).all(1) & torch.isfinite(self.prev).all(1)
        return torch.cat([self.prev, cur], 1), valid

    def attach(self, runner):
        env, step, update, save = self.env, self.env.step, runner.alg.update, runner.save
        self.prev = env_features(env)[self.rows].clone()

        def step_(actions):
            obs, rew, done, extras = step(actions)
            cur = env_features(env)[self.rows]
            x, valid = self.pairs(cur, done)
            with torch.no_grad():
                r = style_reward(self.disc(torch.nan_to_num(x))) * valid
            self._store(x[valid])
            self.prev = cur.clone()
            n = valid.sum().clamp(min=1)
            self.stats["reward"] = float(r.sum() / n)
            extras["log"]["rew/amp_style"] = (r.sum() / n) * self.weight * env.ctrl_dt
            extras["log"]["amp/style_reward"] = r.sum() / n
            for k in ("d_expert", "d_policy", "loss"):
                extras["log"]["amp/" + k] = torch.tensor(self.stats[k], device=env.device)
            return obs, rew + self.weight * env.ctrl_dt * r, done, extras

        def update_(*a, **k):
            out = update(*a, **k)
            self.train()
            return out

        def save_(path, infos=None):
            out = save(path, infos)
            torch.save(
                dict(disc=self.disc.state_dict(), opt=self.opt.state_dict(), stats=self.stats),
                os.path.join(os.path.dirname(path), "amp_disc.pt"),
            )
            return out

        env.step, runner.alg.update, runner.save = step_, update_, save_
        return self

    def load(self, path: str) -> bool:
        if not os.path.exists(path):
            return False
        ck = torch.load(path, map_location=self.env.device, weights_only=False)
        self.disc.load_state_dict(ck["disc"])
        self.opt.load_state_dict(ck["opt"])
        return True

    # ---------------------------------------------------------------- discriminator
    def train(self):
        if self.filled < self.batch:
            return
        dev = self.buf.device
        for _ in range(self.updates):
            e = (
                self.expert[torch.randint(len(self.expert), (self.batch,), device=dev, generator=self.gen)]
                .clone()
                .requires_grad_(True)
            )
            p = self.buf[
                torch.randint(self.filled, (self.batch,), device=dev, generator=self.gen)
            ].clone()  # a normal tensor: the buffer was filled in inference mode
            de, dp = self.disc(e), self.disc(p)
            grad = torch.autograd.grad(de.sum(), e, create_graph=True)[0]
            loss = ((de - 1) ** 2).mean() + ((dp + 1) ** 2).mean() + self.gp * grad.pow(2).sum(1).mean()
            self.opt.zero_grad(set_to_none=True)
            loss.backward()
            self.stats["updates"] += 1
            if not all(torch.isfinite(q.grad).all() for q in self.disc.parameters() if q.grad is not None):
                self.stats["nan_updates"] += 1
                self.opt.zero_grad(set_to_none=True)
                continue
            torch.nn.utils.clip_grad_norm_(self.disc.parameters(), 1.0)
            self.opt.step()
            self.stats.update(d_expert=float(de.mean()), d_policy=float(dp.mean()), loss=float(loss))
