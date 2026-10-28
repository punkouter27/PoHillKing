# CI entry point for .github/workflows/webgl.yml: build WebGL headless, deploy it with deploy_webgl.ps1,
# then check the live page. Same file in every Po Unity game; only -Method differs (the game's builder).
#
#   pwsh Tools/ci_webgl.ps1 -Method PoSumo.EditorTools.BuildWebGL.Build
#
# Runs on the self-hosted `unity` runner, i.e. the dev PC, with its Unity install, license and az login.
param([Parameter(Mandatory)][string] $Method)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = (Select-String -Path "$root/ProjectSettings/ProjectVersion.txt" -Pattern '^m_EditorVersion: (.+)$').Matches[0].Groups[1].Value.Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
if (-not (Test-Path $unity)) { throw "Unity $version is not installed on this runner ($unity)." }

# One Unity build at a time on this PC, across every game: two at once starve each other of CPU and RAM.
$lock = [System.Threading.Mutex]::new($false, 'Global\PoUnityBuild')
"Waiting for the Unity build lock..."
try { [void]$lock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }  # a killed build left it: it is ours now
try {
    $log = Join-Path $root 'Logs/ci-webgl.log'
    Remove-Item $log -ErrorAction SilentlyContinue
    "Building with Unity $version ($Method)..."
    $p = Start-Process $unity -Wait -PassThru -NoNewWindow -ArgumentList @(
        '-batchmode', '-quit', '-projectPath', "`"$root`"", '-buildTarget', 'WebGL',
        '-executeMethod', $Method, '-logFile', "`"$log`"")
    $result = if (Test-Path $log) { Select-String -Path $log -Pattern 'WEBGL BUILD RESULT: (.*)' | Select-Object -Last 1 }
    "Unity exited $($p.ExitCode): $($result.Line)"
    if (-not $result -or $result.Matches[0].Groups[1].Value -notlike 'Succeeded*') {
        if (Test-Path $log) { Get-Content $log -Tail 80 }
        throw 'WebGL build failed.'
    }
} finally { $lock.ReleaseMutex() }

# deploy_webgl.ps1 reads the default az subscription; every Po game lives in this one.
az account set --subscription 14d963a2-badc-4a1f-9900-553bf01f8de2
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
