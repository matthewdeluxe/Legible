# Build.ps1 - builds Legible into a self-contained folder that runs on any Windows 10/11 x64 PC
# with nothing else installed.
#
# Usage:  powershell -ExecutionPolicy Bypass -File Build.ps1 [-OutputDirectory <folder>]
#
# Needs the .NET 10 (or newer) SDK. The first build downloads the English OCR model,
# the orientation model and Tesseract's glyphless PDF font into .\tessdata.
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'Legible-App'))
$ErrorActionPreference = 'Stop'

# A stale MSBuildSDKsPath (left behind by an SDK upgrade) breaks every build; ignore it if it points nowhere.
if ($env:MSBuildSDKsPath -and -not (Test-Path -LiteralPath $env:MSBuildSDKsPath)) { $env:MSBuildSDKsPath = $null }

$sdk = Join-Path $env:USERPROFILE 'scoop\apps\dotnet-sdk\current\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }

# OCR data (Apache-2.0, from the tesseract-ocr project).
$tessdata = Join-Path $PSScriptRoot 'tessdata'
New-Item -ItemType Directory -Force -Path $tessdata | Out-Null
$downloads = [ordered]@{
    'eng.traineddata' = 'https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata'
    'osd.traineddata' = 'https://github.com/tesseract-ocr/tessdata/raw/main/osd.traineddata'
    'pdf.ttf'         = 'https://github.com/tesseract-ocr/tesseract/raw/main/tessdata/pdf.ttf'
}
foreach ($name in $downloads.Keys) {
    $target = Join-Path $tessdata $name
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "Downloading $name"
        Invoke-WebRequest -Uri $downloads[$name] -OutFile $target -UseBasicParsing
    }
}

# Tesseract's native DLLs need the Visual C++ runtime. Ship it next to the app (app-local deployment)
# so users don't have to install the redistributable.
$vcruntime = Join-Path $PSScriptRoot 'vcruntime'
New-Item -ItemType Directory -Force -Path $vcruntime | Out-Null
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
    $source = Join-Path $env:WINDIR "System32\$dll"
    if (-not (Test-Path -LiteralPath $source)) { throw "$dll not found. Install the Visual C++ 2015-2022 x64 redistributable on the build machine." }
    Copy-Item -LiteralPath $source -Destination $vcruntime -Force
}

& $sdk publish (Join-Path $PSScriptRoot 'src\Legible.App\Legible.App.csproj') -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Built: $OutputDirectory\Legible.exe"
