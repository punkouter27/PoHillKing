"""What a mirror stalemate looks like: pelvis separation and speeds in a brain's duel against a copy of itself (eval.py's
duel), and how much of each round a stall rule (closer than D m, both slower than V m/s, for S s; env stalemate_rule)
would cover, in the rounds that reach the bell and in the decided ones before their decision.

  KOTH_ROBOT=kim .venv/Scripts/python.exe scripts/archive/stall_probe.py --ckpt runs/shipped/kim/model_final.pt [--rung r4league]
"""
import argparse, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import torch
from koth.env import KothEnv
from koth.rules import OUT_RADIUS
from scripts.eval import eval_cfg, load_policy

DS, VS, S = [0.6, 0.8, 1.0, 1.2], [0.1, 0.2, 0.3, 0.5], 2.0


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--ckpt", required=True); ap.add_argument("--rung", default="r4league")
    ap.add_argument("--num-envs", type=int, default=256); ap.add_argument("--seconds", type=float, default=25.0); a = ap.parse_args()
    env = KothEnv(eval_cfg(a.rung, opponent=[a.ckpt], ckpt=a.ckpt), a.num_envs, seed=0); policy = load_policy(env, a.ckpt)
    N, dt = env.N, env.ctrl_dt; steps = int(a.seconds / dt); obs = env.reset(); torch.manual_seed(1000)
    open_ = torch.ones(N, dtype=torch.bool, device=env.device); decided = torch.zeros_like(open_)
    stale = torch.zeros(len(DS), len(VS), N, device=env.device); covered = torch.zeros_like(stale); open_t = torch.zeros(N, device=env.device)
    seps, speeds = [], []
    for t in range(steps):
        with torch.no_grad(): obs, _, done, extras = env.step(policy(obs))
        _, _, linv_w, _ = env._root()
        sep = extras["sep"].view(N, 2)[:, 0]; v = linv_w[:, :2].norm(dim=1).view(N, 2).max(1).values
        if t * dt > 5.0: seps.append(sep[open_]); speeds.append(v[open_])
        for i, d in enumerate(DS):
            for j, vv in enumerate(VS):
                stale[i, j] = torch.where((sep < d) & (v < vv) & open_, stale[i, j] + dt, torch.zeros_like(sep))
                covered[i, j] += ((stale[i, j] > S) & open_).float() * dt
        open_t += open_.float() * dt
        out = ((extras["radius"] > OUT_RADIUS) | extras["fallen"]).view(N, 2).any(1) & open_
        decided |= out; open_ &= ~(out | (done if done.shape[0] == N else done.view(N, 2)[:, 0]))
    sep, sp = torch.cat(seps), torch.cat(speeds); qs = torch.tensor([0.1, 0.25, 0.5, 0.75, 0.9], device=env.device)
    res = dict(ckpt=a.ckpt, rounds=N, decided=round(decided.float().mean().item(), 3),
               sep_quantiles_after_5s=[round(x, 3) for x in sep.quantile(qs).tolist()],
               max_speed_quantiles_after_5s=[round(x, 3) for x in sp.quantile(qs).tolist()], rules={})
    for i, d in enumerate(DS):
        for j, vv in enumerate(VS):
            share = covered[i, j] / open_t.clamp(min=dt)
            res["rules"][f"d{d}_v{vv}"] = dict(bell=round(share[~decided].mean().item(), 3) if (~decided).any() else None,
                                               decided=round(share[decided].mean().item(), 3) if decided.any() else None)
    print(json.dumps(res, indent=1))


if __name__ == "__main__":
    main()
