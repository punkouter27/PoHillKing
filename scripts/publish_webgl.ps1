# Builds the WebGL player on THIS machine and publishes it as a GitHub Release. Publishing the release triggers
# .github/workflows/webgl.yml, which deploys the release's webgl.zip to the game's Azure Static Web App on a
# GitHub-hosted runner and checks the live page and /diag. No Unity licence is needed in CI.
#
#   pwsh -File scripts\publish_webgl.ps1               # EditMode tests, build, zip, publish release (older releases are kept)
#   pwsh -File scripts\publish_webgl.ps1 -NoPublish    # tests, build and zip only (then scripts\deploy_webgl.ps1 if wanted)
#   pwsh -File scripts\publish_webgl.ps1 -SkipBuild    # publish the existing Builds/WebGL (no tests, no build)
#
# Close the Unity editor on this project first (a batch-mode Unity cannot open a project that is already open). The
# release is tagged at HEAD, so HEAD must already be pushed to origin. Settings: scripts/webgl.json.
#
# One version stamp: UTC yyMMdd, computed here once and passed to Unity (-poBuildVersion), which puts it in
# PlayerSettings.bundleVersion for the build (Application.version, the page's PRODUCT_VERSION) and in the HUD's
# Resources/build_version.txt; diag.html gets the same string.

param(
    [switch] $SkipBuild,
    [switch] $NoPublish,
    [int] $Keep = 0          # > 0: prune to this many webgl-* releases, the new one included (0 = delete nothing)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$cfg = Get-Content (Join-Path $PSScriptRoot 'webgl.json') -Raw | ConvertFrom-Json
$buildDir = Join-Path $root 'Builds\WebGL'
$zip = Join-Path $root 'Builds\webgl.zip'
$version = (Get-Date).ToUniversalTime().ToString('yyMMdd')

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
    $exe = Join-Path 'C:\Program Files\Unity\Hub\Editor' "$want\Editor\Unity.exe"
    if (-not (Test-Path $exe)) { throw "Unity $want is not installed. Install exactly $want in Unity Hub with the WebGL module." }
    if (-not (Test-Path (Join-Path (Split-Path $exe) 'Data\PlaybackEngines\WebGLSupport'))) { throw "Unity $want has no WebGL module." }
    return $exe
}

function Invoke-Unity([string[]] $arguments, [string] $log) {
    $p = Start-Process -FilePath $script:unity -PassThru -Wait -NoNewWindow -ArgumentList (@(
        '-batchmode', '-projectPath', "`"$root`"", '-logFile', "`"$log`"") + $arguments)
    return $p.ExitCode
}

if (-not $SkipBuild) {
    $script:unity = Find-Unity
    New-Item -ItemType Directory -Force (Join-Path $root 'Builds') | Out-Null

    # 1. EditMode tests: a red test stops the publish.
    $results = Join-Path $root 'Builds\editmode-results.xml'
    $testLog = Join-Path $root 'Builds\editmode.log'
    if (Test-Path $results) { Remove-Item $results -Force }
    "EditMode tests; log: $testLog"
    $code = Invoke-Unity @('-nographics', '-runTests', '-testPlatform', 'EditMode', '-testResults', "`"$results`"") $testLog
    if (-not (Test-Path $results)) { Get-Content $testLog -Tail 40; throw "EditMode tests did not run (exit $code). Log: $testLog" }
    [xml] $xml = Get-Content $results
    $run = $xml.'test-run'
    "EditMode: $($run.passed) passed, $($run.failed) failed, $($run.skipped) skipped of $($run.total)"
    if ([int] $run.failed -gt 0 -or $code -ne 0) {
        $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object { "  FAILED $($_.fullname)" }
        throw "EditMode tests failed (exit $code); nothing was built. Results: $results"
    }

    # 2. The player.
    $log = Join-Path $root 'Builds\webgl-build.log'
    "Building WebGL v$version ($($cfg.buildMethod)); log: $log"
    $code = Invoke-Unity @('-quit', '-buildTarget', 'WebGL', '-executeMethod', $cfg.buildMethod, '-poBuildVersion', $version) $log
    if ($code -ne 0) { Get-Content $log -Tail 40; throw "Unity build failed (exit $code). Full log: $log" }
}

if (-not (Test-Path (Join-Path $buildDir 'index.html'))) { throw "No WebGL build at $buildDir." }
$built = Select-String -Path (Join-Path $buildDir 'index.html') -Pattern 'name="po-version" content="(\d{6})"' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
if ($built) { $version = $built }       # -SkipBuild: diag.html follows the build's own stamp

# Static Web Apps 404s .unityweb without this MIME config.
Copy-Item (Join-Path $PSScriptRoot 'webgl\staticwebapp.config.json') $buildDir -Force

# /diag: the live checks of scripts/webgl.json plus this build's bundle files.
$bundle = @(Get-ChildItem (Join-Path $buildDir 'Build') -File | ForEach-Object { $_.Name } | Sort-Object)
$checks = @($cfg.diagChecks | ForEach-Object { [ordered]@{ section = $_.section; name = $_.name; url = $_.url; used = $_.used } })
$diag = (Get-Content (Join-Path $PSScriptRoot 'webgl\diag.template.html') -Raw).
    Replace('__TITLE__', $cfg.title).Replace('__VERSION__', $version).
    Replace('__CHECKS__', (ConvertTo-Json -InputObject $checks -Depth 4 -Compress)).
    Replace('__BUNDLE__', (ConvertTo-Json -InputObject $bundle -Compress))
[IO.File]::WriteAllText((Join-Path $buildDir 'diag.html'), $diag)
"diag.html: v$version, $($checks.Count) service checks, $($bundle.Count) bundle files"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $buildDir '*') -DestinationPath $zip -CompressionLevel Optimal
'{0}: {1:N1} MB' -f $zip, ((Get-Item $zip).Length / 1MB)

if ($NoPublish) { 'Built and zipped; not published (-NoPublish).'; return }

$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner).Trim()
$stamp = (Get-Date).ToUniversalTime().ToString('yyMMdd-HHmm')
$tag = "webgl-$stamp-$($sha.Substring(0,7))"
gh release create $tag $zip -R $repo --target $sha --title "WebGL v$version ($stamp UTC)" --latest=false `
    --notes "Local WebGL build v$version of $($sha.Substring(0,7)). Publishing this release deploys it to Azure (.github/workflows/webgl.yml)."
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
"PUBLISHED $tag  ->  deploy: https://github.com/$repo/actions/workflows/webgl.yml"

# Only when asked (-Keep N): keep the newest N webgl-* releases (and their tags). By default nothing is deleted.
if ($Keep -le 0) { return }
$old = gh release list -R $repo --limit 100 --json tagName,createdAt |
    ConvertFrom-Json | Where-Object { $_.tagName -like 'webgl-*' } |
    Sort-Object createdAt -Descending | Select-Object -Skip $Keep
foreach ($r in $old) { gh release delete $r.tagName -R $repo --yes --cleanup-tag; "pruned $($r.tagName)" }
