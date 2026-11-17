# OCRmyPDF WinUI 3

Native C# WinUI 3 front end for the existing OCRmyPDF installation. Windows x64, Windows 10 1809 or later. The release folder bundles .NET and Windows App SDK; OCRmyPDF, Tesseract, Ghostscript and optional cleanup/compression tools remain external dependencies.

## Run

Build the app first (see Build), then run `WinUI-App/OcrMyPdf.WinUI.exe`; pin a shortcut to it if you like. Keep the entire app folder together. The original Python app and setup script remain available.

OCR uses English (Tesseract language code eng) automatically. Add PDF files, a recursive folder, or pasted PDF paths. Choose output folder and suffix, review settings, and select Run OCR. PDFs can also be passed as command-line arguments. A blank output folder saves next to each input. Existing outputs and duplicate output names are rejected.

Replacing originals requires confirmation on every batch, and by default keeps each original as `<name>.pdf.bak` (untick the backup option to disable; an existing `.bak` is overwritten). Each job writes to a unique temporary PDF beside its destination, then commits the result after OCR succeeds. Cancellation terminates the process tree and removes unfinished temporary output. Only successful jobs replace originals.

Settings migrate from `%LOCALAPPDATA%/OCRmyPDF GUI/settings.json` on first launch and are saved separately in `%LOCALAPPDATA%/OCRmyPDF WinUI/settings.json`. Replacement mode always starts off. Change the executable field if OCRmyPDF is installed elsewhere; the app checks that it exists before starting a batch. Cleaning requires unpaper; higher optimization may require pngquant/jbig2.

## Build

Install .NET 10 SDK on Windows, then run `Build.ps1`. The project uses Windows App SDK 2.5.1 and targets x64. The publish target includes the compiled WinUI resources required for unpackaged launch. No Visual Studio or MSIX installation is required to run the published app.

The original `Setup-OcrMyPdfGui.ps1` installs the OCR dependencies and the Python interface. It does not rebuild the WinUI app.

