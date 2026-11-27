# Trains on a Google Cloud GPU instead of this laptop: one Spot L4 VM per run, which deletes itself when train.py ends.
#   .\cloud-train.ps1 start [-Robot kim] [-Hours 24] [-NoWait] [-Force] <train.py arguments>
#       e.g. .\cloud-train.ps1 start -Robot kim --rung kim_r0b --name kim_r0b_c1 --num-envs 4096 --iters 600
#       Trains the PUSHED commit (Training/ and benchmark/ must be committed and pushed). Then waits, shows progress
#       and pulls the run home when it ends; Ctrl+C stops only the waiting, the VM carries on.
#   .\cloud-train.ps1 status            every cloud job and its state, hours and approximate cost
#   .\cloud-train.ps1 logs <job>        the end of the job's train.log (copied to the bucket every 5 minutes)
#   .\cloud-train.ps1 pull [<job>]      copy ended jobs into Training/runs/<rung>/<name>, then clear them from the bucket
#                                       (-Keep leaves them there); a running job is copied as it stands and kept
#   .\cloud-train.ps1 stop <job>        delete the job's VM now (its run folder is saved first), then pull it
# Arguments for train.py pass through unchanged, except: --tensorboard is dropped (nobody can open it on the VM), and
# --graduate-reward without --export-dir exports into runs/<rung>/<name>/export, so the ONNX comes home with the run.
# Git-ignored files named in the arguments (--warm/--resume/--opponent checkpoints with the config.json beside them,
# --amp datasets) are uploaded for the VM. Settings: cloud/cloud.json. How it works and the one-off setup: cloud/README.md.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0)][ValidateSet('start', 'status', 'logs', 'pull', 'stop')][string]$Command = 'status',
    [string]$Robot = '',
    [double]$Hours = 24,
    [switch]$NoWait,
    [switch]$Force,
    [switch]$Keep,
    [switch]$Smoke, # start: a CPU-only VM runs everything but train.py (setup, inputs, imports, round trip): no GPU quota
    [Parameter(ValueFromRemainingArguments)][string[]]$Rest = @()
)
$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Training = Join-Path $Root 'Training'
$Cfg = Get-Content (Join-Path $Root 'cloud/cloud.json') -Raw | ConvertFrom-Json
$Jobs = "gs://$($Cfg.bucket)/jobs"
$Gcloud = (Get-Command gcloud -ErrorAction SilentlyContinue)?.Source
if (-not $Gcloud) { $Gcloud = Join-Path $env:LOCALAPPDATA 'Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd' }
if (-not (Test-Path $Gcloud)) { throw 'Google Cloud CLI not found: winget install Google.CloudSDK, then gcloud auth login (cloud/README.md)' }

function Invoke-GCloud {
    # gcloud with the project; returns stdout lines, leaves stderr in $script:GErr and the exit code in $script:GExit
    $out = & $Gcloud @args "--project=$($Cfg.project)" 2>&1
    $script:GExit = $LASTEXITCODE
    $script:GErr = ($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }
}
function Assert-GCloud([string]$what) { if ($script:GExit) { throw "$what failed:`n$script:GErr" } }
function Read-Json([string]$url) {
    $t = Invoke-GCloud storage cat $url
    if ($script:GExit -or -not $t) { return $null }
    ($t -join "`n") | ConvertFrom-Json
}
function Get-Vms {
    $t = Invoke-GCloud compute instances list '--filter=labels.po-repo=pohillking' '--format=json'
    if ($script:GExit) { return @() }
    @(($t -join "`n") | ConvertFrom-Json)
}
function Get-JobState($job, $vms) {
    # job.json (what was started) + status.json (what the VM last reported) + whether its VM still exists
    $j = Read-Json "$Jobs/$job/job.json"
    if (-not $j) { return $null }
    $s = Read-Json "$Jobs/$job/status.json"
    $state = $s ? $s.state : 'starting'
    $alive = [bool]($vms | Where-Object { $_.name -eq $j.vm })
    $ended = $state -match '^(finished|failed|interrupted|stopped)'
    $start = [datetimeoffset]$j.started # offsets, not [datetime]: "...Z" parses to local time and skews the hours
    if (-not $ended -and -not $alive -and ([datetimeoffset]::UtcNow - $start).TotalMinutes -gt 10) {
        $state = 'lost (VM gone without a final report)'; $ended = $true
    }
    $endTime = ($ended -and $s) ? [datetimeoffset]$s.time : [datetimeoffset]::UtcNow
    $h = [math]::Max(0, ($endTime - $start).TotalHours)
    [pscustomobject]@{ Job = $job; State = $state; Ended = $ended; Run = "runs/$($j.rung)/$($j.name)"; Vm = $j.vm
        Zone = $j.zone; Hours = [math]::Round($h, 2); ApproxUsd = [math]::Round($h * $Cfg.approxUsdPerHour, 2); Info = $j }
}
function Get-JobNames {
    $t = Invoke-GCloud storage ls "$Jobs/"
    if ($script:GExit) { return @() }
    @($t | Where-Object { $_ -match '/jobs/([^/]+)/$' } | ForEach-Object { $Matches[1] })
}

function Invoke-Pull($st, [bool]$final) {
    $dest = Join-Path $Training $st.Run
    $has = Invoke-GCloud storage ls "$Jobs/$($st.Job)/run/"
    if ($has) {
        New-Item -ItemType Directory -Force $dest | Out-Null
        Invoke-GCloud storage rsync -r "$Jobs/$($st.Job)/run" $dest | Out-Null; Assert-GCloud "copying $($st.Job)"
        Invoke-GCloud storage cp "$Jobs/$($st.Job)/train.log" (Join-Path $dest 'cloud_train.log') | Out-Null
        Write-Host "pulled $($st.Job) -> Training/$($st.Run)"
    } else {
        $log = Join-Path $Training "runs/cloud_$($st.Job).log"
        Invoke-GCloud storage cp "$Jobs/$($st.Job)/train.log" $log | Out-Null
        Write-Host "$($st.Job) has no run folder (it ended before training started); its log: Training/runs/cloud_$($st.Job).log"
    }
    if ($final -and -not $Keep) {
        Invoke-GCloud storage rm -r "$Jobs/$($st.Job)" | Out-Null; Assert-GCloud "clearing $($st.Job) from the bucket"
    }
    $py = Join-Path $Training '.venv/Scripts/python.exe'
    if ($has -and (Test-Path $py)) { & $py (Join-Path $Root 'benchmark/runs.py') *> $null } # rebuild runs/runs.html
    if ($st.State -match '^(interrupted|lost)' -and $has) {
        $last = Get-ChildItem $dest -Filter 'model_*.pt' | Sort-Object { [int]($_.BaseName -replace '\D', '') } | Select-Object -Last 1
        if ($last) {
            $i = [Collections.Generic.List[string]]$st.Info.args
            foreach ($f in '--resume', '--warm') { $k = $i.IndexOf($f); if ($k -ge 0) { $i.RemoveRange($k, 2) } }
            $r = $st.Info.robot ? " -Robot $($st.Info.robot)" : ''
            Write-Host "Continue it from $($last.Name):`n  .\cloud-train.ps1 start$r -Force $($i -join ' ') --resume $($st.Run)/$($last.Name)"
        }
    }
}

switch ($Command) {
    'status' {
        $vms = Get-Vms
        $rows = Get-JobNames | ForEach-Object { Get-JobState $_ $vms } | Where-Object { $_ }
        if (-not $rows) { Write-Host 'No cloud jobs.' } else { $rows | Format-Table Job, State, Run, Zone, Hours, ApproxUsd -AutoSize }
        $orphans = $vms | Where-Object { $_.name -notin $rows.Vm }
        foreach ($v in $orphans) { Write-Warning "VM $($v.name) has no job in the bucket and still costs money: gcloud compute instances delete $($v.name) --zone $(($v.zone -split '/')[-1])" }
        Write-Host "Real spend: https://console.cloud.google.com/billing/reports?project=$($Cfg.project) (credits show as negative)"
    }
    'logs' {
        if (-not $Rest) { throw 'usage: .\cloud-train.ps1 logs <job>' }
        $t = Invoke-GCloud storage cat "$Jobs/$($Rest[0])/train.log"; Assert-GCloud 'reading the log'
        $t | Select-Object -Last 40
    }
    'pull' {
        $vms = Get-Vms
        $names = $Rest ? @($Rest[0]) : (Get-JobNames)
        $any = $false
        foreach ($n in $names) {
            $st = Get-JobState $n $vms
            if (-not $st) { Write-Warning "no job $n"; continue }
            if (-not $st.Ended -and -not $Rest) { continue } # pull without a name: ended jobs only
            Invoke-Pull $st $st.Ended; $any = $true
        }
        if (-not $any) { Write-Host 'Nothing to pull (no ended jobs).' }
    }
    'stop' {
        if (-not $Rest) { throw 'usage: .\cloud-train.ps1 stop <job>' }
        $st = Get-JobState $Rest[0] (Get-Vms)
        if (-not $st) { throw "no job $($Rest[0])" }
        if (-not $st.Ended) {
            Write-Host "deleting $($st.Vm) (its shutdown script saves the run folder first)..."
            Invoke-GCloud compute instances delete $st.Vm "--zone=$($st.Zone)" --quiet | Out-Null; Assert-GCloud 'deleting the VM'
            $st = Get-JobState $Rest[0] @()
        }
        Invoke-Pull $st $true
    }
    'start' {
        $a = [Collections.Generic.List[string]]::new([string[]]$Rest)
        function Get-Arg([string]$n) { $i = $a.IndexOf($n); if ($i -ge 0 -and $i + 1 -lt $a.Count) { $a[$i + 1] } }
        $rung = Get-Arg '--rung'
        if (-not $rung) { throw 'give train.py a --rung (and a --name for the run folder)' }
        $stamp = [datetime]::UtcNow.ToString('yyMMdd-HHmm')
        $name = Get-Arg '--name'
        if (-not $name) { $name = "cloud-$stamp"; $a.AddRange([string[]]@('--name', $name)) }
        while ($a.Remove('--tensorboard')) {}
        if ($a.Contains('--graduate-reward') -and -not $a.Contains('--export-dir')) {
            $a.AddRange([string[]]@('--export-dir', "runs/$rung/$name/export"))
        }
        if ((Test-Path (Join-Path $Training "runs/$rung/$name")) -and -not $Force) {
            throw "Training/runs/$rung/$name already exists here and the pull would write into it: use a new --name (or -Force)"
        }

        # The VM trains the pushed commit, so the checkpoints' provenance names a commit anyone can check out.
        git -C $Root fetch -q origin
        $dirty = git -C $Root status --porcelain -- Training benchmark
        if ($dirty) { throw "Commit and push Training/ and benchmark/ first; the VM trains the pushed commit:`n$($dirty -join "`n")" }
        if ([int](git -C $Root rev-list --count '@{u}..HEAD')) { throw 'Push first: the VM fetches this commit from GitHub.' }
        $commit = git -C $Root rev-parse HEAD
        $repo = git -C $Root remote get-url origin

        $job = ("$name-$stamp".ToLower() -replace '[^a-z0-9-]', '-' -replace '-+', '-').Trim('-')
        $vm = "po-hk-$job"; if ($vm.Length -gt 63) { $vm = $vm.Substring(0, 63).TrimEnd('-') }
        $gs = "$Jobs/$job"

        # Git-ignored inputs: every argument that names a file under Training/ git does not track, plus what train.py
        # reads beside a checkpoint (config.json = its action path, amp_disc.pt = its motion-prior discriminator).
        $tracked = [Collections.Generic.HashSet[string]]::new([string[]](git -C $Root ls-files Training), [StringComparer]::OrdinalIgnoreCase)
        $inputs = [Collections.Generic.SortedSet[string]]::new()
        for ($i = 0; $i -lt $a.Count; $i++) {
            if ($a[$i].StartsWith('--')) { continue }
            $p = ($a[$i] -split ':')[0] -replace '\\', '/'
            if ($p -notmatch '\.(pt|pkl|npz|json|onnx)$') { continue }
            if ([IO.Path]::IsPathRooted($p)) { throw "use a path relative to Training/, not $p" }
            if (-not (Test-Path (Join-Path $Training $p) -PathType Leaf)) { continue }
            $a[$i] = $a[$i] -replace '\\', '/'
            if ($tracked.Contains("Training/$p")) { continue }
            [void]$inputs.Add($p)
            $dir = Split-Path $p -Parent
            foreach ($side in 'config.json', 'amp_disc.pt') {
                $s = ($dir ? "$dir/$side" : $side) -replace '\\', '/'
                if (Test-Path (Join-Path $Training $s)) { [void]$inputs.Add($s) }
            }
        }
        $k = $a.IndexOf('--amp')
        if ($k -ge 0) {
            $amp = ($k + 1 -lt $a.Count -and -not $a[$k + 1].StartsWith('--')) ? $a[$k + 1] : "assets/mocap/cmu_style_$($Robot ? $Robot : 'g1').npz"
            if (-not (Test-Path (Join-Path $Training $amp))) { throw "--amp needs $amp here first (uv run python scripts/fetch_mocap.py)" }
            [void]$inputs.Add($amp -replace '\\', '/')
        }
        foreach ($p in $inputs) {
            Write-Host "uploading Training/$p"
            Invoke-GCloud storage cp (Join-Path $Training $p) "$gs/inputs/$p" | Out-Null; Assert-GCloud "uploading $p"
        }

        $argFile = Join-Path ([IO.Path]::GetTempPath()) "po-args-$job.txt"
        [IO.File]::WriteAllText($argFile, ($a -join "`n"))
        $info = [ordered]@{ job = $job; vm = $vm; zone = $null; rung = $rung; name = $name; robot = $Robot; args = @($a)
            commit = $commit; started = [datetime]::UtcNow.ToString('o'); hours = $Hours; machine = $Cfg.machineType; smoke = [bool]$Smoke }
        $machine = $Smoke ? 'e2-standard-4' : $Cfg.machineType
        if ($Smoke) { $Hours = [math]::Min($Hours, 1) }
        $meta = "po-job=$job,po-bucket=$($Cfg.bucket),po-run=runs/$rung/$name,po-repo=$repo,po-commit=$commit" +
            ($Robot ? ",po-robot=$Robot" : '') + ($Smoke ? ',po-smoke=1' : ',install-nvidia-driver=True')
        $files = "startup-script=$(Join-Path $Root 'cloud/startup.sh'),shutdown-script=$(Join-Path $Root 'cloud/shutdown.sh'),po-args=$argFile"
        $made = $null
        foreach ($z in $Cfg.zones) {
            Write-Host "creating Spot $machine$($Smoke ? ' (smoke test, no GPU)' : ' (1x L4)') $vm in $z ..."
            Invoke-GCloud compute instances create $vm "--zone=$z" "--machine-type=$machine" `
                --provisioning-model=SPOT --instance-termination-action=DELETE "--max-run-duration=$([int]($Hours * 3600))s" `
                --maintenance-policy=TERMINATE "--image-family=$($Cfg.imageFamily)" "--image-project=$($Cfg.imageProject)" `
                "--boot-disk-size=$($Cfg.diskGb)GB" --boot-disk-type=pd-balanced --scopes=cloud-platform `
                "--labels=po-repo=pohillking,po-job=$job" "--metadata=$meta" "--metadata-from-file=$files" | Out-Null
            if (-not $script:GExit) { $made = $z; break }
            if ($script:GErr -match 'QUOTA|[Qq]uota') {
                Remove-Item $argFile -ErrorAction SilentlyContinue
                throw "No GPU quota yet (cloud/README.md, step 3). Google said:`n$script:GErr"
            }
            if ($script:GErr -notmatch 'RESOURCE_POOL_EXHAUSTED|STOCKOUT|does not have enough resources|unavailable') {
                Remove-Item $argFile -ErrorAction SilentlyContinue; throw "creating the VM failed:`n$script:GErr"
            }
            Write-Host "  no Spot L4 free in $z right now"
        }
        Remove-Item $argFile -ErrorAction SilentlyContinue
        if (-not $made) { throw 'No Spot L4 free in any zone of cloud/cloud.json right now; try again later.' }
        $info.zone = $made
        $jf = Join-Path ([IO.Path]::GetTempPath()) "po-job-$job.json"
        $info | ConvertTo-Json -Depth 4 | Set-Content $jf
        Invoke-GCloud storage cp $jf "$gs/job.json" | Out-Null; Assert-GCloud 'writing job.json'
        Remove-Item $jf
        Write-Host "job $job started on $vm ($made); it deletes itself when train.py ends, after $Hours h at the latest."
        if ($NoWait) { Write-Host "Later: .\cloud-train.ps1 status / logs $job / pull"; break }

        Write-Host 'Waiting (Ctrl+C stops only the waiting; the VM carries on). Setup takes ~10 min before training starts.'
        $last = ''
        while ($true) {
            Start-Sleep 60
            $st = Get-JobState $job (Get-Vms)
            $tail = (Invoke-GCloud storage cat "$gs/train.log" | Select-Object -Last 1)
            $line = "{0}  {1}  {2:N2} h  ~`${3}  {4}" -f (Get-Date -Format 'HH:mm'), $st.State, $st.Hours, $st.ApproxUsd, $tail
            if ($line.Substring(6) -ne $last) { Write-Host $line; $last = $line.Substring(6) }
            if ($st.Ended) { break }
        }
        Invoke-Pull $st $true
    }
}
