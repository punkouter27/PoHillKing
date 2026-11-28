#!/bin/bash
# Runs once, as root, when the cloud training VM boots (cloud-train.ps1 passes it as the VM's startup script).
# It fetches Training/ at the launcher's (pushed) commit, builds the training environment, runs Training/scripts/train.py
# with the launcher's arguments, copies the run folder and the log to the bucket every few minutes, and deletes the VM
# when training ends, however it ends. Everything it needs comes from the VM's metadata (po-* attributes).
set -u
export HOME=/root
md() { curl -sf -H 'Metadata-Flavor: Google' "http://metadata.google.internal/computeMetadata/v1/instance/$1"; }

JOB=$(md attributes/po-job)
BUCKET=$(md attributes/po-bucket)
RUN_REL=$(md attributes/po-run)          # runs/<rung>/<name>, relative to Training/
ROBOT=$(md attributes/po-robot || true)  # KOTH_ROBOT, empty = G1
REPO=$(md attributes/po-repo)
COMMIT=$(md attributes/po-commit)
SMOKE=$(md attributes/po-smoke || true) # cloud-train.ps1 -Smoke: CPU VM, everything but train.py
NAME=$(md name)
ZONE=$(md zone | awk -F/ '{print $NF}')
GS="gs://$BUCKET/jobs/$JOB"
WORK=/opt/po
LOG=$WORK/train.log
mkdir -p $WORK /var/po
cd $WORK

status() { # status <state> [exit code]: the launcher polls this file
    printf '{"state":"%s","exit":%s,"vm":"%s","zone":"%s","time":"%s"}\n' "$1" "${2:-null}" "$NAME" "$ZONE" \
        "$(date -u +%FT%TZ)" >$WORK/status.json
    gcloud storage cp -q $WORK/status.json "$GS/status.json" >/dev/null 2>&1
}
sync_out() {
    [ -d "$WORK/src/Training/$RUN_REL" ] &&
        gcloud storage rsync -r -q "$WORK/src/Training/$RUN_REL" "$GS/run" >/dev/null 2>&1
    gcloud storage cp -q "$LOG" "$GS/train.log" >/dev/null 2>&1
}
finish() { # finish <state> <exit code>: last copy, then the VM deletes itself (so it stops costing money)
    sync_out
    status "$1" "$2"
    touch /var/po/done
    gcloud compute instances delete "$NAME" --zone "$ZONE" --quiet
    shutdown -h now # only reached if the delete was refused
}

exec >>"$LOG" 2>&1
echo "== $(date -u +%FT%TZ) job $JOB on $NAME ($ZONE)"
status setup
( while sleep 120; do sync_out; done ) & # the setup log reaches the bucket too
SYNC_PID=$!

if [ -z "$SMOKE" ]; then
    echo "== waiting for the NVIDIA driver"
    for _ in $(seq 90); do nvidia-smi >/dev/null 2>&1 && break; sleep 10; done
    nvidia-smi || finish "failed: no GPU driver after 15 minutes" 1
fi

echo "== fetching Training/ and benchmark/ at commit $COMMIT"
# A sparse, shallow clone of the pushed commit (not a tarball), so train.py records the real commit in every
# checkpoint's provenance, exactly as on the laptop.
git init -q src && cd src &&
    git remote add origin "$REPO" &&
    git sparse-checkout set Training benchmark &&
    git fetch -q --depth 1 --filter=blob:none origin "$COMMIT" &&
    git checkout -q FETCH_HEAD || finish "failed: could not fetch commit $COMMIT from $REPO" 1
cd $WORK
# Inputs the launcher uploaded: git-ignored checkpoints named in the arguments (--warm, --resume, --opponent ...)
# with the config.json beside each, and the motion-capture dataset for --amp. Same paths as under Training/.
gcloud storage ls "$GS/inputs/" >/dev/null 2>&1 &&
    { gcloud storage rsync -r -q "$GS/inputs" "$WORK/src/Training" || finish "failed: inputs did not download" 1; }

echo "== building the training environment (uv sync)"
curl -LsSf https://astral.sh/uv/install.sh | sh
export PATH="/root/.local/bin:$PATH"
cd $WORK/src/Training
uv sync --frozen || finish "failed: uv sync" 1

mapfile -t ARGS < <(md attributes/po-args) # one train.py argument per line
echo "== train.py ${ARGS[*]}"
status training
kill $SYNC_PID 2>/dev/null
sync_out # the setup log now, not 5 min from now
( while sleep 300; do sync_out; done ) &
[ -n "$ROBOT" ] && export KOTH_ROBOT="$ROBOT"
if [ -n "$SMOKE" ]; then # the training stack imports and the run folder makes the round trip; no GPU, no train.py
    mkdir -p "$RUN_REL" && ls -R "$WORK/src/Training/runs" >"$RUN_REL/smoke_inputs.txt" 2>&1
    uv run --frozen python -c "import torch, mujoco, mujoco_warp, warp, rsl_rl, koth.env; print('smoke: imports ok, torch', torch.__version__, 'mujoco', mujoco.__version__)" 2>&1 |
        tee "$RUN_REL/smoke.txt"
    CODE=${PIPESTATUS[0]}
else
    PYTHONUNBUFFERED=1 uv run --frozen python scripts/train.py "${ARGS[@]}"
    CODE=$?
fi
echo "== $(date -u +%FT%TZ) train.py exited $CODE"
[ $CODE -eq 0 ] && finish finished 0
finish "failed: train.py exited $CODE" $CODE
