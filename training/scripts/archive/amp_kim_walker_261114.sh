#!/usr/bin/env bash
# First motion-prior experiment (2026-11-14): does --amp change how Kim's walker moves, against a control fine-tune
# without it? Both warm-start from the shipped walker (runs/amp/kim_walker_warm.pt, rebuilt from
# Models/kim/walker_policy.onnx by scripts/onnx_to_ckpt.py), same seed, same budget, checkpoint-only: nothing is
# exported or shipped. Then eval.py on kim_r1b, 3 seeds, for the warm brain and both candidates.
#   bash scripts/archive/amp_kim_walker_261114.sh [minutes per run, default 25] [num envs, default 2048]
# Needs: the editor closed or >= 4 GB free RAM (AGENTS.md), and assets/mocap/cmu_style_kim.npz (scripts/fetch_mocap.py).
set -euo pipefail
cd "$(dirname "$0")/../.."
MIN=${1:-25}; ENVS=${2:-2048}; PY=.venv/Scripts/python.exe
OUT=../DOCS/20261114/amp-walker; mkdir -p "$OUT"
export KOTH_ROBOT=kim
WARM=runs/amp/kim_walker_warm.pt
[ -f "$WARM" ] || $PY scripts/onnx_to_ckpt.py --onnx ../Assets/PoKingHill/Models/kim/walker_policy.onnx --out "$WARM"
[ -f assets/mocap/cmu_style_kim.npz ] || $PY scripts/fetch_mocap.py --bodies kim
note() { echo "$(date '+%F %T') $*" | tee -a "$OUT/progress.txt"; }

note "baseline eval: shipped walker"
$PY scripts/eval.py --rung kim_r1b --ckpt "$WARM" --seeds 3 --num-envs 256 > "$OUT/eval_warm.txt" 2>&1
note "$(grep -E '^(SUMMARY|VERDICT)' "$OUT/eval_warm.txt" | tr '\n' ' ')"

for arm in amp control; do
  extra=(); [ "$arm" = amp ] && extra=(--amp --amp-weight 0.5)
  note "train $arm: $MIN min, $ENVS worlds"
  $PY scripts/train.py --rung kim_r1b --num-envs "$ENVS" --iters 100000 --warm "$WARM" --critic-warmup 15 --seed 1 \
      --max-minutes "$MIN" --save-interval 50 --checkpoint-only --name "walker_$arm" "${extra[@]}" > "$OUT/train_$arm.log" 2>&1
  note "train $arm done: $(grep -c 'Learning iteration' "$OUT/train_$arm.log") iterations"
  $PY scripts/eval.py --rung kim_r1b --ckpt "runs/kim_r1b/walker_$arm/model_final.pt" --seeds 3 --num-envs 256 > "$OUT/eval_$arm.txt" 2>&1
  note "$(grep -E '^(SUMMARY|VERDICT)' "$OUT/eval_$arm.txt" | tr '\n' ' ')"
done
note "all done"
