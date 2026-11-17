param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\WinUI-App'))
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $env:USERPROFILE 'scoop\apps\dotnet-sdk\current\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
& $sdk publish (Join-Path $PSScriptRoot 'OcrMyPdf.WinUI.csproj') -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
Write-Host "Built: $OutputDirectory\OcrMyPdf.WinUI.exe"
