# CI entry point for .github/workflows/webgl.yml: build WebGL headless, deploy it with deploy_webgl.ps1,
# then check the live page.
#
# SAME FILE IN EVERY PO UNITY GAME (reference copy: PoBox; copy it with
# PoPunkouterSoftware/scripts/sync-ci.ps1). What differs per game is Tools/webgl.json:
#   { "buildMethod": "...", "staticWebApp": "...", "resourceGroup": "...", "subscription": "..." }
#
#   pwsh Tools/ci_webgl.ps1
#
# Runs on the self-hosted `unity` runner, i.e. the dev PC, with its Unity install, license and az login.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cfg = Get-Content (Join-Path $PSScriptRoot 'webgl.json') -Raw | ConvertFrom-Json
$version = (Select-String -Path "$root/ProjectSettings/ProjectVersion.txt" -Pattern '^m_EditorVersion: (.+)$').Matches[0].Groups[1].Value.Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
if (-not (Test-Path $unity)) { throw "Unity $version is not installed on this runner ($unity)." }

# Keep the runner PC awake for the whole job: when it slept mid-build the runner lost GitHub and the run failed
# ("lost communication with the server"). Holds while this thread runs; Windows drops it when the process exits.
Add-Type -Namespace Po -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][Po.Power]::SetThreadExecutionState([uint32]'0x80000001')   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

# One Unity build at a time on this PC, across every game: two at once starve each other of CPU and RAM.
$lock = [System.Threading.Mutex]::new($false, 'Global\PoUnityBuild')
"Waiting for the Unity build lock..."
try { [void]$lock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }  # a killed build left it: it is ours now
try {
    $log = Join-Path $root 'Logs/ci-webgl.log'
    Remove-Item $log -ErrorAction SilentlyContinue
    "Building with Unity $version ($($cfg.buildMethod))..."
    $p = Start-Process $unity -Wait -PassThru -NoNewWindow -ArgumentList @(
        '-batchmode', '-quit', '-projectPath', "`"$root`"", '-buildTarget', 'WebGL',
        '-executeMethod', $cfg.buildMethod, '-logFile', "`"$log`"")
    $result = if (Test-Path $log) { Select-String -Path $log -Pattern 'WEBGL BUILD RESULT: (.*)' | Select-Object -Last 1 }
    "Unity exited $($p.ExitCode): $($result.Line)"
    if (-not $result -or $result.Matches[0].Groups[1].Value -notlike 'Succeeded*') {
        if (Test-Path $log) { Get-Content $log -Tail 80 }
        throw 'WebGL build failed.'
    }
} finally { $lock.ReleaseMutex() }

az account set --subscription $cfg.subscription
$out = & (Join-Path $PSScriptRoot 'deploy_webgl.ps1')
$out
$url = ($out | Select-String 'DEPLOYED: (\S+)' | Select-Object -Last 1).Matches[0].Groups[1].Value
if (-not $url) { throw 'deploy_webgl.ps1 did not report a URL.' }

# The page is live and is a Unity WebGL page, not an old or empty site.
foreach ($attempt in 1..20) {
    try {
        $page = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 30
        if ($page.StatusCode -eq 200 -and $page.Content -match 'loader.js') { "Live: $url"; exit 0 }
    } catch { }
    Start-Sleep 15
}
throw "$url did not serve the WebGL page."
