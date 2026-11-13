# Deploys the WebGL build (Tools > PoHillKing > Build WebGL (browser), which writes
# Builds/WebGL) to the Azure Static Web App "pohillking" in resource group
# PoHillKing, subscription "Azure subscription 1" of punkouter27@gmail.com
# (tenant punkouter27gmail.onmicrosoft.com; `az login --tenant` it, the bare
# `az login` finds no subscription). Free tier.
#
#   powershell -File Tools\deploy_webgl.ps1
#
# Static Web Apps 404s any extension it has no MIME type for, and every file a
# Unity build with the decompression fallback writes ends in .unityweb, so
# Tools/webgl/staticwebapp.config.json is copied into the build first. The build
# folder is git-ignored and rewritten by every build; the config lives here.
#
# The SWA CLI is pinned: 2.1.0 was four days old when this was written.

param(
    [string] $ResourceGroup = "PoHillKing",
    [string] $AppName = "pohillking",
    [string] $BuildDir = "Builds\WebGL"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not (Test-Path (Join-Path $BuildDir "index.html"))) {
    throw "No WebGL build at $BuildDir. Build it first: Tools > PoHillKing > Build WebGL (browser)."
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
