# Builds the WebGL player on THIS machine and publishes it as a GitHub Release. Publishing the
# release triggers .github/workflows/webgl.yml, which deploys the release's webgl.zip to the
# game's Azure Static Web App on a GitHub-hosted runner. No Unity licence is needed in CI.
#
# SAME FILE IN EVERY PO UNITY GAME (reference copy: PoBox; copy it with
# PoPunkouterSoftware/scripts/sync-ci.ps1).
#
#   pwsh -File Tools\publish_webgl.ps1               # build, zip, publish release, prune old ones
#   pwsh -File Tools\publish_webgl.ps1 -SkipBuild    # publish the existing Builds/WebGL
#   pwsh -File Tools\publish_webgl.ps1 -NoPublish    # build and zip only
#
# Close the Unity editor on this project first (a batch-mode Unity cannot open a project that is
# already open). The release is tagged at HEAD, so HEAD must already be pushed to origin.
# The build method comes from Tools/webgl.json; it writes Builds/WebGL.

param(
    [switch] $SkipBuild,
    [switch] $NoPublish,
    [int] $Keep = 3          # webgl-* releases to keep, the new one included
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$cfg = Get-Content (Join-Path $PSScriptRoot 'webgl.json') -Raw | ConvertFrom-Json
$buildDir = Join-Path $root 'Builds\WebGL'
$zip = Join-Path $root 'Builds\webgl.zip'

$sha = (git rev-parse HEAD).Trim()
if (-not $NoPublish) {
    git fetch --quiet origin
    if (-not (git branch -r --contains $sha)) {
        throw "HEAD $($sha.Substring(0,7)) is not on origin. Push it first: the release is tagged at HEAD."
    }
    if (git status --porcelain --untracked-files=no) {
        Write-Warning 'Uncommitted changes: the build will include them, but the release is tagged at HEAD.'
    }
}

function Find-Unity {
    $want = ((Get-Content 'ProjectSettings\ProjectVersion.txt' | Select-String 'm_EditorVersion:') -split ':\s*')[1].Trim()
    $hub = 'C:\Program Files\Unity\Hub\Editor'
    $exact = Join-Path $hub "$want\Editor\Unity.exe"
    if (Test-Path $exact) { return @{ Exe = $exact; Exact = $true; Version = $want } }
    # Same major.minor (e.g. 6000.6.x): patch releases build the same project, but the editor rewrites
    # ProjectVersion.txt and may bump package versions; those files are restored after the build.
    $line = ($want -split '\.')[0..1] -join '.'
    $alt = Get-ChildItem $hub -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$line.*" -and (Test-Path (Join-Path $_.FullName 'Editor\Data\PlaybackEngines\WebGLSupport')) } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $alt) { throw "Unity $want (or any $line.x with WebGL support) is not installed. Install it in Unity Hub with the WebGL module." }
    Write-Warning "Unity $want is not installed; building with $($alt.Name). Install $want in Unity Hub to build with the exact version."
    return @{ Exe = Join-Path $alt.FullName 'Editor\Unity.exe'; Exact = $false; Version = $alt.Name }
}

if (-not $SkipBuild) {
    $u = Find-Unity
    $guarded = 'ProjectSettings/ProjectVersion.txt', 'Packages/manifest.json', 'Packages/packages-lock.json'
    $saved = @{}
    if (-not $u.Exact) {
        foreach ($f in $guarded) { if (Test-Path $f) { $saved[$f] = [IO.File]::ReadAllBytes((Join-Path $root $f)) } }
    }
    New-Item -ItemType Directory -Force (Split-Path $buildDir) | Out-Null
    $log = Join-Path $root 'Builds\webgl-build.log'
    "Building WebGL with Unity $($u.Version) ($($cfg.buildMethod)); log: $log"
    try {
        $p = Start-Process -FilePath $u.Exe -PassThru -Wait -NoNewWindow -ArgumentList @(
            '-batchmode', '-quit', '-projectPath', "`"$root`"", '-buildTarget', 'WebGL',
            '-executeMethod', $cfg.buildMethod, '-logFile', "`"$log`"")
        $code = $p.ExitCode
    }
    finally {
        foreach ($f in $saved.Keys) { [IO.File]::WriteAllBytes((Join-Path $root $f), $saved[$f]) }
    }
    if ($code -ne 0) {
        Get-Content $log -Tail 40
        throw "Unity build failed (exit $code). Full log: $log"
    }
}

if (-not (Test-Path (Join-Path $buildDir 'index.html'))) { throw "No WebGL build at $buildDir." }

# Static Web Apps 404s .unityweb without this MIME config.
Copy-Item (Join-Path $PSScriptRoot 'webgl\staticwebapp.config.json') $buildDir -Force
$buildStamp = Get-Date -Format 'yyMMdd'
$diagHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Diagnostics</title>
<style>body{margin:0;background:#0a0f0a;color:#e8efe8;font:14px/1.5 system-ui,sans-serif}header{display:flex;justify-content:space-between;padding:12px 16px;border-bottom:1px solid #1c261c}header a{color:#e8efe8;text-decoration:none;font-weight:600}main{max-width:680px;margin:24px auto;padding:0 16px;display:grid;gap:12px}.card{background:#121a12;border:1px solid #1c261c;border-radius:8px;padding:12px 16px;display:flex;justify-content:space-between;align-items:center}.ok{color:#6fce8f;font-weight:600}.stamp{position:fixed;right:10px;bottom:6px;font-size:11px;color:#9aa69a}</style></head>
<body><header><a href="/">WebGL Player</a><a href="/">← Back</a></header><main><h1>System status</h1>
<div class="card"><span>WebGL build bundle (/index.html)</span><span class="ok">Ready</span></div>
<div class="card"><span>3D renderer (WebGL2)</span><span id="gl" class="ok">Checking…</span></div>
<div class="card"><span>Build version</span><span class="ok">v$buildStamp</span></div></main><span class="stamp">v$buildStamp</span>
<script>document.getElementById('gl').textContent=document.createElement('canvas').getContext('webgl2')?'Healthy':'Unavailable';</script></body></html>
"@
[IO.File]::WriteAllText((Join-Path $buildDir 'diag.html'), $diagHtml)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $buildDir '*') -DestinationPath $zip -CompressionLevel Optimal
'{0}: {1:N1} MB' -f $zip, ((Get-Item $zip).Length / 1MB)

if ($NoPublish) { 'Built and zipped; not published (-NoPublish).'; return }

$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner).Trim()
$stamp = Get-Date -Format 'yyMMdd-HHmm'
$tag = "webgl-$stamp-$($sha.Substring(0,7))"
gh release create $tag $zip -R $repo --target $sha --title "WebGL $stamp" --latest=false `
    --notes "Local WebGL build of $($sha.Substring(0,7)). Publishing this release deploys it to Azure (.github/workflows/webgl.yml)."
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
"PUBLISHED $tag  ->  deploy: https://github.com/$repo/actions/workflows/webgl.yml"

# Keep only the newest $Keep webgl-* releases (and their tags).
$old = gh release list -R $repo --limit 100 --json tagName,createdAt |
    ConvertFrom-Json | Where-Object { $_.tagName -like 'webgl-*' } |
    Sort-Object createdAt -Descending | Select-Object -Skip $Keep
foreach ($r in $old) { gh release delete $r.tagName -R $repo --yes --cleanup-tag; "pruned $($r.tagName)" }
