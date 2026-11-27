#!/bin/bash
# The VM's shutdown script: when Google takes a Spot VM back, the -Hours cap is reached or `cloud-train.ps1 stop` deletes
# it, there are about 30 s left. Copy the run folder and the log to the bucket and mark the job "interrupted", so the
# run can be continued from its last checkpoint (written every --save-interval updates). Does nothing when startup.sh
# already finished the job.
[ -f /var/po/done ] && exit 0
md() { curl -sf -H 'Metadata-Flavor: Google' "http://metadata.google.internal/computeMetadata/v1/instance/$1"; }
GS="gs://$(md attributes/po-bucket)/jobs/$(md attributes/po-job)"
RUN="/opt/po/src/Training/$(md attributes/po-run)"
[ -d "$RUN" ] && timeout 20 gcloud storage rsync -r -q "$RUN" "$GS/run"
timeout 5 gcloud storage cp -q /opt/po/train.log "$GS/train.log"
printf '{"state":"interrupted","exit":null,"vm":"%s","time":"%s"}\n' "$(md name)" "$(date -u +%FT%TZ)" >/tmp/status.json
timeout 5 gcloud storage cp -q /tmp/status.json "$GS/status.json"
