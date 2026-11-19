# Legible

Make scanned PDFs searchable on Windows. Queue PDFs or whole folders, and get back PDFs with an invisible text layer you can search, select and copy. Everything runs locally; nothing is uploaded anywhere.

Legible is a single self-contained folder. There is nothing else to install: no Python, Ghostscript, Tesseract install or Visual C++ redistributable.

| Component | What it is |
| --- | --- |
| `src/Legible.App/` | The WinUI 3 desktop app. |
| `src/Legible.Core/` | The OCR engine (C# library) the app uses. |
| `src/Legible.Cli/` | A small command-line front end to the same engine. |
| `Build.ps1` | Builds the app into `Legible-App/`. |

## How it works

For each page that needs OCR, Legible renders the page with PDFium, detects sideways or upside-down pages and small skew, recognizes the text with Tesseract, and lays Tesseract's invisible text over the original page. The original page content is not touched (except in Force OCR mode, below), so image quality and file contents stay as they were.

Built on: [Tesseract](https://github.com/tesseract-ocr/tesseract) via the [Tesseract .NET wrapper](https://github.com/charlesw/tesseract) (Apache-2.0), [PDFium](https://pdfium.googlesource.com/pdfium/) via [PDFtoImage](https://github.com/sungaila/PDFtoImage) (MIT), [PDFsharp](https://github.com/empira/PDFsharp) 6.2 (MIT) and [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0).

## Features

- Add files, add a folder (subfolders included), paste a list of paths, or pass PDFs as command-line arguments.
- Output beside each original or into a chosen folder, with a configurable filename suffix (default `_ocr`).
- Replace-originals mode with a confirmation on every batch, keeping a `.pdf.bak` copy of each original by default.
- Text handling modes:
  - **Skip** pages that already have text (default).
  - **Force OCR** on every page. Each page is replaced by an image of itself plus OCR text, so existing text and vector content become part of the image.
  - **Redo OCR**: replace text layers that Legible added earlier; pages with other text are skipped.
  - **Strict**: fail if any page already has text.
- Auto-rotate sideways and upside-down pages (sets the page rotation; the content is not resampled).
- Deskew. In Force OCR mode the page is visibly straightened; otherwise the text layer follows the skew so selection still lines up.
- Pages are OCR'd in parallel. Live log with save to file, per-batch success and failure counts, and Cancel.
- Settings are remembered between runs. Settings from the earlier OCRmyPDF front end are picked up on first run.

OCR language is English.

## Build

Requirements: Windows 10 1809 or later (x64) and the .NET 10 SDK (or newer).

```powershell
powershell -ExecutionPolicy Bypass -File Build.ps1
```

The first build downloads the English model, the orientation model and Tesseract's glyphless PDF font into `tessdata/` (about 15 MB), and copies the Visual C++ runtime DLLs from the build machine into `vcruntime/` for app-local deployment. Output goes to `Legible-App/` (about 280 MB including the .NET and Windows App SDK runtimes). Pass `-OutputDirectory` to build somewhere else.

Run `Legible-App\Legible.exe`. To share a build, zip the whole `Legible-App` folder.

> Keep the repository and output in a short path (for example `C:\src\legible`). The WinUI XAML compiler and native DLL loading fail when paths pass Windows' 260-character limit.

### Command line

```powershell
dotnet run --project src/Legible.Cli -- input.pdf output.pdf
dotnet run --project src/Legible.Cli -- --mode force --no-deskew input.pdf output.pdf
```

Run with `--help` for all options (`--mode`, `--no-rotate`, `--lang`, `--jobs`, `--dpi`, `--tessdata`).

## Safety notes

- Each job writes to a temporary file and moves it into place only after it succeeds. Failed or cancelled jobs leave originals untouched.
- Existing output files and duplicate output names are rejected rather than overwritten.
- Force OCR changes a document more than plain OCR does. Try new settings on copies first.
- Settings are stored in `%LOCALAPPDATA%\Legible\settings.json`. Replace mode always starts off.

## Not included (compared with OCRmyPDF)

- PDF/A output.
- Output size optimization (pngquant, jbig2). Force OCR output can be larger than the input.
- unpaper cleaning and background removal.
- Languages other than English. Adding one is a matter of dropping its `.traineddata` into `tessdata/` and exposing a language setting.

## Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| App won't start | Check for `startup-error.txt` next to the exe, and keep the whole `Legible-App` folder together. |
| "Missing OCR data" | The `tessdata` folder next to the exe is missing files. Rebuild, or copy it from another build. |
| Build fails with "Xaml compiler error" or "Input JSON file doesn't exist" | The path is too long. Move the repository to a short path. |
| Build fails with "Could not resolve SDK Microsoft.NET.Sdk" | A stale `MSBuildSDKsPath` environment variable points at a removed SDK. `Build.ps1` ignores it; for other builds, remove the variable. |

## Licensing

Legible is MIT licensed. See `LICENSE`.

PDFsharp is pinned to 6.2.4, the last MIT release; PDFsharp 6.5 and later are AGPL-3.0, so do not upgrade it without reviewing the license. Bundled OCR data and Tesseract are Apache-2.0. The Visual C++ runtime DLLs are redistributed under Microsoft's Visual C++ redistributable terms.
