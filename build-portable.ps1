# Publishes Gauge as a self-contained x64 portable app, then zips it.
#
# Usage:  pwsh -File build-portable.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'Gauge.csproj'
$rid = 'win-x64'
$appDir = Join-Path $root "dist\portable\$rid\Gauge"
$zip = Join-Path $root "dist\GaugePortable-$rid.zip"

[xml]$projectXml = Get-Content $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Gauge.csproj does not define <Version>.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found. Install the .NET 10 SDK, then retry.'
}

Write-Host '==> Stopping any running Gauge...' -ForegroundColor Cyan
Get-Process Gauge -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "==> Cleaning $appDir ..." -ForegroundColor Cyan
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "==> Publishing portable app $version ($rid)..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release -r $rid -p:Platform=x64 --self-contained true `
    -o $appDir -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $appDir 'Gauge.exe'))) {
    throw "Gauge.exe is missing from the publish output."
}
if (-not (Test-Path (Join-Path $appDir 'Gauge.pri'))) {
    throw "Gauge.pri is missing from the publish output; the app would crash at startup."
}

Write-Host "==> Creating $zip ..." -ForegroundColor Cyan
Compress-Archive -Path $appDir -DestinationPath $zip -Force

$sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "==> Done. $zip ($sizeMB MB)" -ForegroundColor Green
