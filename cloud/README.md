# Cloud training (Google Cloud, Spot L4)

`cloud-train.ps1` (repo root) runs `Training/scripts/train.py` on a Google Cloud GPU instead of this laptop. Each
run gets its own Spot VM with one NVIDIA L4 (24 GB). The VM deletes itself when training ends, and the run folder
comes back to `Training/runs/<rung>/<name>` exactly as a local run would.

```powershell
.\cloud-train.ps1 start -Robot kim --rung kim_r0b --name kim_r0b_c1 --num-envs 4096 --iters 600
.\cloud-train.ps1 status              # jobs, state, hours, approximate cost
.\cloud-train.ps1 logs <job>          # end of train.log (refreshed every 5 min)
.\cloud-train.ps1 pull [<job>]        # copy ended runs home (a named running job: a snapshot)
.\cloud-train.ps1 stop <job>          # delete the VM now, save and pull its run
```

`start` waits and pulls the run home when it ends. Ctrl+C stops only the waiting: the VM keeps going, and
`pull` brings the run home later. Options: `-Robot kim|trump|grandpa` (KOTH_ROBOT), `-Hours 24` (a hard cap, after
which Google deletes the VM), `-NoWait`, `-Force` (write into an existing local run folder, used to continue a run),
and `-Smoke` (a CPU-only VM runs everything except train.py, which tests the pipeline without GPU quota).

## What happens

1. The launcher refuses to start unless `Training/` and `benchmark/` are committed and pushed. The VM clones that
   exact commit from GitHub (sparse: `Training/` and `benchmark/` only), so every checkpoint's provenance names a real
   commit.
2. Git-ignored files named in the arguments, such as `--warm`, `--resume` and `--opponent` checkpoints (with the
   `config.json` and `amp_disc.pt` beside them) and `--amp` datasets, are uploaded to the bucket and placed at the same
   paths on the VM.
3. `cloud/startup.sh` runs on the VM. It waits for the GPU driver, runs `uv sync --frozen` (the same `uv.lock`), then
   runs `train.py` with your arguments. It copies the run folder and the log to the bucket every 5 minutes, and once
   more at the end, and then deletes the VM.
4. Spot VMs can be taken back by Google at any time. When that happens, `cloud/shutdown.sh` saves the run folder in
   its last 30 seconds and the job reads **interrupted**. `pull` brings it home and prints the `start ... --resume`
   command that continues from the newest `model_N.pt` (written every `--save-interval` updates, default 100).

Two changes to your train.py arguments: `--tensorboard` is dropped, and `--graduate-reward` without `--export-dir`
exports into `runs/<rung>/<name>/export/`. Copy a graduated ONNX into Unity yourself after the pull. Run TensorBoard
on the pulled folder as usual.

## Cost

A g2-standard-8 Spot VM with one L4 costs about **$0.35 an hour**, with Spot prices varying. Setup takes about 10
minutes and is billed too. The real numbers are in Billing → Reports for project `po-rl-training-7106`, where credits
appear as negative amounts. The budget "Po RL training credit guard" emails at 50/80/95/100 % of $290.
The free-trial credit ends on **2027-01-29**. After that, or past $300, the card on the billing account is charged.

An L4 is not faster than the laptop's RTX 5070 Ti. The point is to free the laptop and to run several jobs at once
(quota permitting). Compare speeds with `benchmark.ps1` on the laptop.

## One-off setup (done 2026-11-27 for this laptop's account)

1. Install the Google Cloud CLI (`winget install Google.CloudSDK`), then run `gcloud auth login`.
2. Create project `po-rl-training-7106`, link it to the billing account, enable the Compute, Storage, Billing
   Budgets and Cloud Quotas APIs, create bucket `gs://po-rl-training-7106-jobs` (us-central1) and set the $290
   budget. Settings live in `cloud/cloud.json`.
3. Request GPU quota. New projects start at 0 GPUs. We requested `GPUS-ALL-REGIONS` = 2 and
   `PREEMPTIBLE-NVIDIA-L4-GPUS` (us-central1) = 2. Google reviews these, and its email goes to the account. Check them at
   console.cloud.google.com → IAM & Admin → Quotas. Until they are granted, `start` stops with "No GPU quota yet",
   but `-Smoke` works.
4. On another laptop, only step 1 is needed (and `gcloud auth login` with the same account).

## Leftovers to watch

- `status` warns about any training VM that has no job (for example, if a launch was interrupted). Delete it with
  the command it prints.
- `pull` empties a job from the bucket once it is home. A job that is never pulled keeps a few hundred MB there,
  which costs cents a month.
