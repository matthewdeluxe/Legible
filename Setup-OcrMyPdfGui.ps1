# Setup-OcrMyPdfGui.ps1
#
# Recreates the OCRmyPDF GUI environment on a new Windows machine:
#   - Installs Scoop (if missing)
#   - Installs Python, Ghostscript, Tesseract + language packs via Scoop
#   - Installs ocrmypdf via pip into that Python
#   - Checks that the GUI app (ocrmypdf_gui.pyw) sits next to this script
#   - Creates a Desktop shortcut
#
# Usage: right-click this file -> "Run with PowerShell"
#   (or from a terminal:  powershell -ExecutionPolicy Bypass -File Setup-OcrMyPdfGui.ps1)
#
# Optional: add -BuildJbig2 to also build jbig2enc from source (large MSYS2 toolchain,
# only shrinks scanned black-and-white output).

[CmdletBinding()]
param(
    # jbig2enc is optional and needs a large MSYS2 toolchain, so it is only built on request.
    [switch]$BuildJbig2
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

# Native commands (scoop, pacman, pip) ignore $ErrorActionPreference, so check exit codes here.
function Invoke-Checked([string]$what, [scriptblock]$command, [switch]$Optional) {
    $global:LASTEXITCODE = 0
    & $command | Out-Host
    if ($LASTEXITCODE -ne 0) {
        if ($Optional) {
            Write-Host "WARNING: $what failed (exit code $LASTEXITCODE)." -ForegroundColor Yellow
        } else {
            throw "$what failed (exit code $LASTEXITCODE)."
        }
    }
}

# --- 1. Install Scoop if missing ---
Write-Step "Checking for Scoop package manager"
if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {
    Write-Host "Scoop not found, installing..."
    # Scoop's installer needs RemoteSigned or looser; only change policy if the effective one is too strict.
    if ((Get-ExecutionPolicy) -in @('Restricted', 'AllSigned')) {
        try { Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force }
        catch { Write-Host "WARNING: could not change the execution policy (Group Policy?): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    Invoke-RestMethod get.scoop.sh | Invoke-Expression
} else {
    Write-Host "Scoop already installed."
}

# --- 2. Install required packages ---
Write-Step "Installing Python, Ghostscript, Tesseract via Scoop"
$apps = @("python", "ghostscript", "tesseract", "tesseract-languages", "pngquant")
foreach ($app in $apps) {
    $installed = (scoop list 2>$null | Out-String) -match "(?m)^\s*$([regex]::Escape($app))\s"
    if ($installed) {
        Write-Host "$app already installed, skipping."
    } else {
        Invoke-Checked "scoop install $app" { scoop install $app }
    }
}

# refresh PATH in this session so newly-installed exes are visible
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

# --- 2b. Install unpaper via MSYS2 (needed for the Clean / Clean final output options) ---
# unpaper has no official Windows build and isn't packaged by Scoop directly, but MSYS2's
# package repo carries it, and Scoop can install MSYS2 without needing admin rights.
Write-Step "Installing unpaper via MSYS2 (used by --clean / --clean-final)"
$msys2Installed = (scoop list 2>$null | Out-String) -match "(?m)^\s*msys2\s"
if (-not $msys2Installed) {
    Invoke-Checked "scoop install msys2" { scoop install msys2 } -Optional
}

$msys2Bash = Join-Path $env:USERPROFILE "scoop\apps\msys2\current\usr\bin\bash.exe"
$mingwBin = Join-Path $env:USERPROFILE "scoop\apps\msys2\current\mingw64\bin"
$unpaperExe = Join-Path $mingwBin "unpaper.exe"

function Invoke-Msys([string]$command) { & $msys2Bash --login -c $command }

# Expose an MSYS2 tool through a Scoop shim instead of putting all of mingw64\bin (compilers and
# many DLLs) on PATH. The exe loads its DLLs from its own folder, so the shim needs no PATH change.
function Add-MsysShim([string]$name, [string]$exePath) {
    if (-not (Test-Path -LiteralPath $exePath)) {
        Write-Host "WARNING: $exePath not found; no '$name' shim created." -ForegroundColor Yellow
        return
    }
    if (Get-Command "$name.exe" -ErrorAction SilentlyContinue) {
        Write-Host "$name is already on PATH."
        return
    }
    $global:LASTEXITCODE = 0
    scoop shim add $name $exePath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: 'scoop shim add' failed for $name. Put $(Split-Path $exePath) on PATH manually if you want it." -ForegroundColor Yellow
    }
}

if (Test-Path $unpaperExe) {
    Write-Host "unpaper already installed, skipping."
} elseif (Test-Path $msys2Bash) {
    # Full sync (-Syu): a bare -Sy followed by an install is an unsupported partial upgrade.
    # A core-runtime update can end the first run early, so sync twice.
    Invoke-Checked "MSYS2 package sync" { Invoke-Msys "pacman -Syu --noconfirm" } -Optional
    Invoke-Checked "MSYS2 package sync (second pass)" { Invoke-Msys "pacman -Syu --noconfirm" } -Optional
    Invoke-Checked "unpaper install" { Invoke-Msys "pacman -S --noconfirm --needed mingw-w64-x86_64-unpaper" } -Optional
} else {
    Write-Host "WARNING: could not find MSYS2 bash at $msys2Bash; skipping unpaper install." -ForegroundColor Yellow
}
Add-MsysShim "unpaper" $unpaperExe

# --- 2c. Fix TESSDATA_PREFIX (tesseract-languages overwrites it and breaks config lookups) ---
# The "tesseract-languages" Scoop package repoints TESSDATA_PREFIX at its own folder, which
# holds only *.traineddata files. That shadows the base "tesseract" package's persisted
# tessdata folder, which is the one with configs/ (hocr, pdf, txt, ...). Without those config
# files, tesseract fails with "Can't open hocr" any time ocrmypdf uses hocr mode.
# Fix: hardlink the language files into the base tessdata folder (no extra disk space, no
# elevation needed - same NTFS volume) and point TESSDATA_PREFIX at that unified folder.
Write-Step "Fixing TESSDATA_PREFIX (tesseract-languages overwrites it and breaks hocr/config lookup)"
$baseTessdata = Join-Path $env:USERPROFILE "scoop\persist\tesseract\tessdata"
$langDir = Join-Path $env:USERPROFILE "scoop\apps\tesseract-languages\current"
if ((Test-Path $baseTessdata) -and (Test-Path $langDir)) {
    Get-ChildItem -Path $langDir -Filter "*.traineddata" | ForEach-Object {
        $target = Join-Path $baseTessdata $_.Name
        if (-not (Test-Path $target)) {
            New-Item -ItemType HardLink -Path $target -Target $_.FullName | Out-Null
        }
    }
    [Environment]::SetEnvironmentVariable("TESSDATA_PREFIX", $baseTessdata, "User")
    $env:TESSDATA_PREFIX = $baseTessdata
} else {
    Write-Host "WARNING: could not find $baseTessdata or $langDir; TESSDATA_PREFIX not fixed." -ForegroundColor Yellow
}

# --- 2d. Build jbig2enc from source via MSYS2 (no prebuilt Windows binary exists anywhere) ---
# jbig2enc is fully optional - ocrmypdf works fine without it, just with somewhat larger
# output files for scanned black-and-white pages. There is no official Windows build (not even
# via MSYS2), so this compiles it from source using MSYS2's mingw64 toolchain. That needs a
# real compiler toolchain (gcc, cmake, ninja, leptonica dev headers), which is a meaningfully
# large, slow install - only worth it if you actually want the smaller output files.
Write-Step "jbig2enc (optional, used for optimal scan compression)"
$jbig2Exe = Join-Path $mingwBin "jbig2.exe"
if (-not $BuildJbig2) {
    Write-Host "Skipping jbig2enc. Re-run with -BuildJbig2 to build it."
} elseif (Test-Path $jbig2Exe) {
    Write-Host "jbig2 already built, skipping."
    Add-MsysShim "jbig2" $jbig2Exe
} elseif (Test-Path $msys2Bash) {
    Invoke-Checked "MSYS2 package sync" { Invoke-Msys "pacman -Syu --noconfirm" } -Optional
    Invoke-Checked "MSYS2 build tools install" { Invoke-Msys "pacman -S --noconfirm --needed base-devel git mingw-w64-x86_64-toolchain mingw-w64-x86_64-leptonica mingw-w64-x86_64-cmake mingw-w64-x86_64-ninja pkgconf" } -Optional

    $jbig2BuildScript = @'
set -e
export MSYSTEM=MINGW64
source /etc/profile
rm -rf /tmp/jbig2enc-build
git clone --depth 1 https://github.com/agl/jbig2enc /tmp/jbig2enc-build
cd /tmp/jbig2enc-build
echo "jbig2enc commit: $(git rev-parse HEAD)"

# The jbig2 target links Ws2_32 before libjbig2enc, which GNU ld can't resolve (htonl/htons
# are pulled in by libjbig2enc's object files). Reorder so libjbig2enc links first.
sed -i 's/target_link_libraries(jbig2 PRIVATE ${LIBS} libjbig2enc)/target_link_libraries(jbig2 PRIVATE libjbig2enc ${LIBS})/' CMakeLists.txt

mkdir -p build && cd build

# CMake's FindPkgConfig strips include dirs it treats as "system" dirs (which /mingw64/include
# effectively is here), leaving Leptonica_INCLUDE_DIRS empty and configure failing. Work around
# it by supplying pkg-config's real output directly instead of letting pkg_check_modules filter it.
INCL=$(pkg-config --cflags-only-I lept | tr ' ' '\n' | sed 's/^-I//' | tr '\n' ';')
INCL="/mingw64/include;${INCL}"
LIBD=$(pkg-config --libs-only-L lept | tr ' ' '\n' | sed 's/^-L//' | tr '\n' ';')
LIBD="${LIBD}/mingw64/lib"

cmake -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DLeptonica_FOUND=TRUE \
  -DLeptonica_INCLUDE_DIRS="$INCL" \
  -DLeptonica_LIBRARY_DIRS="$LIBD" \
  -DLeptonica_LIBRARIES="leptonica" \
  ..
ninja jbig2.exe
cp jbig2.exe /mingw64/bin/jbig2.exe
'@

    Invoke-Msys $jbig2BuildScript
    if (Test-Path $jbig2Exe) {
        Add-MsysShim "jbig2" $jbig2Exe
    } else {
        Write-Host "WARNING: jbig2enc build did not produce $jbig2Exe. ocrmypdf will still work, just without jbig2 compression." -ForegroundColor Yellow
    }
} else {
    Write-Host "WARNING: could not find MSYS2 bash at $msys2Bash; skipping jbig2enc build." -ForegroundColor Yellow
}

# --- 3. Install ocrmypdf via pip ---
Write-Step "Installing ocrmypdf (pip)"
Invoke-Checked "pip upgrade" { python -m pip install --upgrade pip }
Invoke-Checked "ocrmypdf install" { python -m pip install --upgrade ocrmypdf }

# --- 4. Locate the GUI application ---
Write-Step "Checking for ocrmypdf_gui.pyw"
$appDir = $PSScriptRoot
$appPath = Join-Path $appDir "ocrmypdf_gui.pyw"
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "ocrmypdf_gui.pyw was not found next to this script ($appDir). Keep the two files together."
}
Write-Host "Found $appPath"

# --- 5. Create Desktop shortcut ---
Write-Step "Creating Desktop shortcut"
$pythonwPath = (Get-Command python).Source -replace "python\.exe$", "pythonw.exe"
$desktop = [Environment]::GetFolderPath("Desktop")
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$desktop\OCRmyPDF GUI.lnk")
$Shortcut.TargetPath = $pythonwPath
$Shortcut.Arguments = "`"$appPath`""
$Shortcut.WorkingDirectory = $appDir
$Shortcut.Description = "OCRmyPDF GUI - add searchable text to PDFs"
$Shortcut.IconLocation = (Get-Command python).Source + ",0"
$Shortcut.Save()
Write-Host "Shortcut created at $desktop\OCRmyPDF GUI.lnk"

Write-Step "Done! Launch 'OCRmyPDF GUI' from your Desktop."
