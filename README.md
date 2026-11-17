# OCRmyPDF GUI

Windows desktop front ends for [OCRmyPDF](https://github.com/ocrmypdf/OCRmyPDF). Queue PDFs or whole folders, choose options, and get searchable PDFs back. All processing runs locally; nothing is uploaded anywhere.

| Component | What it is |
| --- | --- |
| `WinUI-Source/` | Native C# WinUI 3 app (recommended). |
| `ocrmypdf_gui.pyw` | Original Tkinter app (legacy, still works). |
| `Setup-OcrMyPdfGui.ps1` | Installs the OCR dependencies on a fresh Windows machine. |

Both apps are front ends only. OCRmyPDF, Tesseract and Ghostscript are separate installs, and the setup script handles them.

## Features

- Add files, add a folder (subfolders included), paste a list of paths, or pass PDFs as command line arguments (WinUI app).
- Output beside each original or into a chosen folder, with a configurable filename suffix (default `_ocr`).
- Replace-originals mode with a confirmation on every batch. The WinUI app keeps a `.pdf.bak` copy of each original by default.
- Text handling modes: skip pages that already have text, force OCR on every page, redo existing OCR, or strict (fail if text exists).
- Options: deskew, auto-rotate, clean with unpaper (before OCR or in the final output), background removal, PDF/A output, optimization level 0 to 3, and free-form extra arguments.
- Live log with save to file, per-batch success and failure counts, and a Cancel button that stops OCRmyPDF and its child processes.
- Settings are remembered between runs.

The WinUI app OCRs in English (`-l eng`). The Python app lets you pick any installed Tesseract languages.

## Requirements

- Windows 10 1809 or later, x64.
- OCRmyPDF, Tesseract and Ghostscript. Optional: unpaper (needed for the Clean options), pngquant and jbig2 (smaller output at higher optimization levels).
- To build the WinUI app: the .NET 10 SDK.

## Setup

1. Run `Setup-OcrMyPdfGui.ps1` (right-click, Run with PowerShell). It installs Scoop, Python, Ghostscript, Tesseract with language packs, pngquant, unpaper (through MSYS2) and OCRmyPDF, fixes the `TESSDATA_PREFIX` setting that the Tesseract language package breaks, and creates a desktop shortcut for the Python app.
   - Add `-BuildJbig2` to also compile the optional jbig2enc compressor. It needs a large MSYS2 toolchain and only shrinks scanned black-and-white output.
   - Scoop must be installed as a normal (non-administrator) user.
2. For the WinUI app, install the .NET 10 SDK and run `WinUI-Source/Build.ps1`. Output goes to `WinUI-App/` (about 230 MB including the bundled runtime). Pass `-OutputDirectory` to build somewhere else, for example outside OneDrive.
3. Run `WinUI-App/OcrMyPdf.WinUI.exe`, or start the Python app with `pythonw ocrmypdf_gui.pyw`.

## Using the WinUI app

1. **Files & output:** add your PDFs, choose an output folder (blank saves beside each original) and a suffix.
2. **OCR settings:** pick the text handling mode and options. Defaults are skip-text, deskew, auto-rotate and PDF/A at optimization level 1.
3. Select **Run OCR**. Progress and OCRmyPDF output appear on the **Log & results** tab, and **Open output folder** is enabled when a job succeeds.

## Safety notes

- Each job writes to a temporary file and moves it into place only after OCRmyPDF succeeds. Failed or cancelled jobs leave originals untouched.
- Existing output files and duplicate output names are rejected rather than overwritten.
- `--force-ocr` and PDF/A output can change a document more than plain OCR does. Try new settings on copies first.
- Settings are stored in `%LOCALAPPDATA%\OCRmyPDF WinUI\settings.json` (WinUI) and `%LOCALAPPDATA%\OCRmyPDF GUI\settings.json` (Python). Replace mode always starts off.

## Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| "OCRmyPDF executable not found" | Set the full path on the OCR settings tab, for example `...\scoop\apps\python\current\Scripts\ocrmypdf.exe`. |
| Clean options fail | unpaper is not installed or not on PATH. Re-run the setup script. |
| Tesseract reports it can't open `hocr` or config files | `TESSDATA_PREFIX` points at a folder with no `configs`. Re-run the setup script, which repairs it. |
| WinUI app won't start | Check for `startup-error.txt` next to the exe, and keep the whole `WinUI-App` folder together. |
| Non-English text is poorly recognized | The WinUI app is fixed to English. Use the Python app, which lets you select other installed languages. |

## Repository notes

`WinUI-App/` (build output) and `__pycache__/` are git-ignored. Rebuild the app with `Build.ps1` instead of committing binaries; if you want to share a build, attach a zip to a GitHub Release.

## License

MIT. See `LICENSE`.
