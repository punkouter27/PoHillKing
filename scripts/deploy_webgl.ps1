# Deploys the WebGL build in Builds/WebGL (written by scripts/publish_webgl.ps1 -NoPublish or the menu
# Tools/PoHillKing/Build WebGL) straight from this PC to the game's Azure Static Web App, Free tier. The app and resource group
# come from scripts/webgl.json; sign in to the punkouter27 tenant first (`az login --tenant`; a
# bare `az login` finds no subscription).
#
#   pwsh -File scripts\deploy_webgl.ps1
#
# Static Web Apps 404s any extension it has no MIME type for, and every file a
# Unity build with the decompression fallback writes ends in .unityweb, so
# scripts/webgl/staticwebapp.config.json is copied into the build first. The build
# folder is git-ignored and rewritten by every build; the config lives here.
#
# The SWA CLI is pinned on purpose.

param(
    [string] $ResourceGroup,
    [string] $AppName,
    [string] $BuildDir = "Builds\WebGL"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$cfg = Get-Content (Join-Path $PSScriptRoot "webgl.json") -Raw | ConvertFrom-Json
if (-not $ResourceGroup) { $ResourceGroup = $cfg.resourceGroup }
if (-not $AppName) { $AppName = $cfg.staticWebApp }

if (-not (Test-Path (Join-Path $BuildDir "index.html"))) {
    throw "No WebGL build at $BuildDir. Build it first (the game's WebGL build menu item, or CI)."
}

Copy-Item (Join-Path $PSScriptRoot "webgl\staticwebapp.config.json") $BuildDir -Force

$token = az staticwebapp secrets list --name $AppName --resource-group $ResourceGroup `
    --query "properties.apiKey" -o tsv
if (-not $token) {
    throw "No deployment token for $AppName in $ResourceGroup. Signed in? az account show"
}

npx --yes "@azure/static-web-apps-cli@2.0.10" deploy $BuildDir `
    --deployment-token $token --env production
if ($LASTEXITCODE -ne 0) {
    throw "swa deploy failed ($LASTEXITCODE)."
}

$hostName = az staticwebapp show --name $AppName --resource-group $ResourceGroup `
    --query "defaultHostname" -o tsv
"DEPLOYED: https://$hostName"
