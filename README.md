# OCRmyPDF GUI

Desktop front ends for [OCRmyPDF](https://github.com/ocrmypdf/OCRmyPDF) on Windows. Add PDFs (or whole folders), pick options, and produce searchable PDFs.

| Component | What it is |
| --- | --- |
| `WinUI-Source/` | Native C# WinUI 3 app (recommended). Build instructions in `README-WinUI.md`. |
| `ocrmypdf_gui.pyw` | Original Tkinter app (legacy, still works). |
| `Setup-OcrMyPdfGui.ps1` | Installs the OCR dependencies on a fresh Windows machine. |

Both apps are front ends only. OCRmyPDF, Tesseract and Ghostscript are separate installs; the setup script handles them.

## Quick start

1. Run `Setup-OcrMyPdfGui.ps1` (right-click, Run with PowerShell). It installs Scoop, Python, Ghostscript, Tesseract with language packs, pngquant, unpaper (via MSYS2) and OCRmyPDF, then creates a desktop shortcut for the Python app. Add `-BuildJbig2` to also build the optional jbig2enc compressor.
2. For the WinUI app, install the .NET 10 SDK and run `WinUI-Source/Build.ps1`. The output goes to `WinUI-App/` (not committed; about 230 MB with the bundled runtime).

## Safety notes

- Output files are written to a temporary file and moved into place only after OCRmyPDF succeeds. Failed or cancelled jobs leave originals untouched.
- Replace mode asks for confirmation on every batch. The WinUI app keeps a `.pdf.bak` copy of each original by default.
- Existing output files and duplicate output names are rejected rather than overwritten.
- `--force-ocr` and PDF/A output can change a document more than plain OCR does. Test on copies first.

## Repository notes

`WinUI-App/` (build output) and `__pycache__/` are git-ignored; rebuild the app with `Build.ps1` rather than committing binaries. Building into a OneDrive folder syncs hundreds of MB on every rebuild, so consider passing `-OutputDirectory` to a location outside OneDrive.

## License

MIT. See `LICENSE`.
