using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace Legible.Core;

/// <summary>
/// Adds an invisible, searchable text layer to a PDF.
/// Pipeline per page: render with PDFium, optionally rotate and deskew, OCR with Tesseract into a
/// text-only PDF page, then graft that page onto the original as a form XObject.
/// The original page content is left untouched except in <see cref="TextMode.ForceOcr"/>.
/// </summary>
public static partial class PdfOcr
{
    const string LayerPrefix = "LegibleOCR";
    static readonly Lock PdfiumLock = new();

    [GeneratedRegex(@"/LegibleOCR\d+ Do")]
    private static partial Regex LayerPattern();

    public static Task<OcrResult> ProcessAsync(string input, string output, OcrOptions? options = null, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        options ??= new OcrOptions();
        return Task.Run(() => Process(input, output, options, log, cancellationToken), cancellationToken);
    }

    public static OcrResult Process(string input, string output, OcrOptions options, IProgress<string>? log, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var tessdata = options.ResolvedTessdata;
        CheckTessdata(options, tessdata);
        var pdfBytes = File.ReadAllBytes(input);

        PdfDocument doc;
        try { doc = PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Modify); }
        catch (Exception ex) { throw new OcrException("Cannot open PDF (encrypted or damaged?): " + ex.Message, ex); }

        using (doc)
        {
            int pageCount = doc.PageCount;
            var hasText = DetectText(pdfBytes, pageCount, log);
            bool force = options.Mode == TextMode.ForceOcr;

            var todo = new List<int>();
            int skipped = 0;
            for (int i = 0; i < pageCount; i++)
            {
                switch (options.Mode)
                {
                    case TextMode.Strict when hasText[i]:
                        throw new OcrException($"Page {i + 1} already contains text (strict mode).");
                    case TextMode.RedoOcr when RemoveLayer(doc.Pages[i]):
                        log?.Report($"Page {i + 1}: removed previous Legible text layer");
                        todo.Add(i);
                        break;
                    case TextMode.SkipText or TextMode.RedoOcr when hasText[i]:
                        skipped++;
                        break;
                    default:
                        todo.Add(i);
                        break;
                }
            }
            log?.Report($"{pageCount} page(s): {todo.Count} to OCR" + (skipped > 0 ? $", {skipped} skipped (already have text)" : ""));

            var temp = Directory.CreateTempSubdirectory("legible-");
            try
            {
                var results = new PageResult?[pageCount];
                int done = 0;
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Jobs), CancellationToken = ct };
                try
                {
                    Parallel.ForEach(todo, parallel,
                        () => new Engines(tessdata, options),
                        (i, _, engines) =>
                        {
                            ct.ThrowIfCancellationRequested();
                            try { results[i] = OcrPage(pdfBytes, i, options, engines, temp.FullName, force); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { throw new OcrException($"Page {i + 1}: {ex.Message}", ex); }
                            var r = results[i]!;
                            var notes = (r.Turn != 0 ? $", rotated {r.Turn}°" : "") + (r.SkewDegrees != 0 ? $", deskewed {r.SkewDegrees:0.0}°" : "");
                            log?.Report($"Page {i + 1}: OCR done ({Interlocked.Increment(ref done)}/{todo.Count}{notes})");
                            return engines;
                        },
                        engines => engines.Dispose());
                }
                catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
                {
                    var first = ae.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ae.InnerExceptions[0];
                    throw first is OcrException or OperationCanceledException ? first : new OcrException(first.Message, first);
                }

                int rotated = 0;
                foreach (var i in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = results[i]!;
                    var page = doc.Pages[i];
                    if (force) ReplaceWithImage(page, r, options.Dpi);
                    else if (r.Turn != 0) page.Rotate = Normalize(page.Rotate + r.Turn);
                    if (r.Turn != 0) rotated++;
                    AddTextLayer(doc, page, i, r, force ? 0 : r.SkewDegrees);
                }

                ct.ThrowIfCancellationRequested();
                doc.Options.CompressContentStreams = true;
                doc.Save(output);
                log?.Report($"Finished in {clock.Elapsed.TotalSeconds:0.0} s");
                return new OcrResult(pageCount, todo.Count, skipped, rotated);
            }
            finally
            {
                try { temp.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    sealed record PageResult(string TextPdf, int Turn, double SkewDegrees, string? ImagePath, int Width, int Height);

    sealed class Engines : IDisposable
    {
        public readonly TesseractEngine Ocr;
        public readonly TesseractEngine? Osd;
        public Engines(string tessdata, OcrOptions options)
        {
            Ocr = new TesseractEngine(tessdata, options.Language, EngineMode.LstmOnly);
            Ocr.SetVariable("debug_file", "NUL");
            if (options.AutoRotate)
            {
                Osd = new TesseractEngine(tessdata, "osd", EngineMode.TesseractOnly);
                Osd.SetVariable("debug_file", "NUL");
            }
        }
        public void Dispose() { Ocr.Dispose(); Osd?.Dispose(); }
    }

    static PageResult OcrPage(byte[] pdfBytes, int index, OcrOptions options, Engines engines, string temp, bool force)
    {
        int dpi = options.Dpi;
        byte[] png;
        lock (PdfiumLock)
        {
            // PDFium is not thread-safe; rendering is serialized, OCR runs in parallel.
            using var bitmap = Conversion.ToImage(pdfBytes, page: index, options: new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false, Grayscale: !force));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }

        var pix = Pix.LoadFromMemory(png);
        try
        {
            int turn = 0;
            if (engines.Osd != null)
            {
                turn = DetectTurn(engines.Osd, pix, options.RotateConfidence);
                if (turn != 0) Replace(ref pix, turn switch { 90 => pix.Rotate90(1), 270 => pix.Rotate90(-1), _ => Rotate180(pix) });
            }

            double skew = 0;
            if (options.Deskew)
            {
                var straight = pix.Deskew(out Scew scew);
                // Leptonica returns an unrotated clone below these thresholds (pixDeskewGeneral).
                if (Math.Abs(scew.Angle) >= 0.1 && scew.Confidence >= 3.0) { skew = scew.Angle; Replace(ref pix, straight); }
                else straight.Dispose();
            }

            pix.XRes = dpi; pix.YRes = dpi;
            string basePath = Path.Combine(temp, $"p{index:D5}");
            using (var renderer = ResultRenderer.CreatePdfRenderer(basePath, options.ResolvedTessdata, true))
            using (renderer.BeginDocument($"page {index + 1}"))
            using (var page = engines.Ocr.Process(pix, PageSegMode.Auto))
            {
                renderer.AddPage(page);
            }

            string? imagePath = null;
            if (force)
            {
                imagePath = basePath + ".png";
                pix.Save(imagePath, ImageFormat.Png);
            }
            return new PageResult(basePath + ".pdf", turn, skew, imagePath, pix.Width, pix.Height);
        }
        finally { pix.Dispose(); }
    }

    static void Replace(ref Pix pix, Pix next) { if (!ReferenceEquals(pix, next)) { pix.Dispose(); pix = next; } }

    static Pix Rotate180(Pix pix) { using var half = pix.Rotate90(1); return half.Rotate90(1); }

    /// <summary>Clockwise turn (0/90/180/270) that makes the image upright.</summary>
    static int DetectTurn(TesseractEngine osd, Pix pix, float minConfidence)
    {
        try
        {
            using var page = osd.Process(pix, PageSegMode.OsdOnly);
            page.DetectBestOrientation(out int orientation, out float confidence);
            if (confidence < minConfidence) return 0;
            // Tesseract reports the counter-clockwise rotation needed to make the page upright.
            return Normalize(360 - orientation);
        }
        catch (Exception) { return 0; } // too little text to decide
    }

    static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    static bool[] DetectText(byte[] pdfBytes, int pageCount, IProgress<string>? log)
    {
        var result = new bool[pageCount];
        try
        {
            using var pig = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            for (int i = 0; i < pageCount && i < pig.NumberOfPages; i++)
            {
                try { result[i] = pig.GetPage(i + 1).Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)); }
                catch (Exception) { /* unreadable page: treat as image-only */ }
            }
        }
        catch (Exception ex) { log?.Report("Warning: could not check for existing text: " + ex.Message); }
        return result;
    }

    static void CheckTessdata(OcrOptions options, string tessdata)
    {
        if (!Directory.Exists(tessdata)) throw new OcrException("OCR data folder not found: " + tessdata);
        var needed = options.Language.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(l => l + ".traineddata").Append("pdf.ttf");
        if (options.AutoRotate) needed = needed.Append("osd.traineddata");
        var missing = needed.Where(f => !File.Exists(Path.Combine(tessdata, f))).ToArray();
        if (missing.Length > 0) throw new OcrException($"Missing OCR data in {tessdata}: {string.Join(", ", missing)}");
    }

    // ---- PDF editing -------------------------------------------------------------------------

    /// <summary>Rasterized replacement for force mode: the page becomes the processed image, upright and unrotated.</summary>
    static void ReplaceWithImage(PdfPage page, PageResult r, int dpi)
    {
        double w = r.Width * 72.0 / dpi, h = r.Height * 72.0 / dpi;
        page.Contents.Elements.Clear();
        page.Elements.Remove("/Resources");
        foreach (var box in new[] { "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox" }) page.Elements.Remove(box);
        page.Rotate = 0;
        page.Orientation = PdfSharp.PageOrientation.Portrait;
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(w, h));
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Replace);
        using var image = XImage.FromFile(r.ImagePath!);
        gfx.DrawImage(image, 0, 0, w, h);
    }

    static void AddTextLayer(PdfDocument doc, PdfPage page, int index, PageResult r, double skewDegrees)
    {
        // Import Tesseract's one-page, text-only PDF and turn its page into a form XObject.
        PdfDictionary form;
        double w, h;
        using (var textDoc = PdfReader.Open(r.TextPdf, PdfDocumentOpenMode.Import))
        {
            var imported = doc.AddPage(textDoc.Pages[0]);
            var box = imported.MediaBox;
            w = box.Width; h = box.Height;
            byte[] content = imported.Contents.CreateSingleContent().Stream.UnfilteredValue;

            form = new PdfDictionary(doc);
            form.Elements.SetName("/Type", "/XObject");
            form.Elements.SetName("/Subtype", "/Form");
            form.Elements["/BBox"] = new PdfArray(doc, new PdfReal(box.X1), new PdfReal(box.Y1), new PdfReal(box.X2), new PdfReal(box.Y2));
            if (imported.Elements["/Resources"] is { } resources) form.Elements["/Resources"] = resources;
            form.CreateStream(Deflate(content));
            form.Elements.SetName("/Filter", "/FlateDecode");
            doc.Internals.AddObject(form);
            doc.Pages.Remove(imported);
        }

        // Map the text page (displayed orientation, deskewed) onto the page's own coordinate space.
        var crop = VisibleBox(page);
        double cw = crop.Width, ch = crop.Height;
        int rotate = Normalize(page.Rotate);
        (double dw, double dh) = rotate % 180 == 0 ? (cw, ch) : (ch, cw);

        var m = Affine.Identity;
        if (skewDegrees != 0) m = m.Then(Affine.RotateAbout(w / 2, h / 2, skewDegrees));
        m = m.Then(Affine.Scale(dw / w, dh / h));
        m = m.Then(rotate switch
        {
            90 => new Affine(0, 1, -1, 0, cw, 0),
            180 => new Affine(-1, 0, 0, -1, cw, ch),
            270 => new Affine(0, -1, 1, 0, 0, ch),
            _ => Affine.Identity,
        });
        m = m.Then(Affine.Translate(crop.X1, crop.Y1));

        string name = $"/{LayerPrefix}{index}";
        var pageResources = page.Elements.GetDictionary("/Resources");
        if (pageResources == null) { pageResources = new PdfDictionary(doc); page.Elements["/Resources"] = pageResources; }
        var xobjects = pageResources.Elements.GetDictionary("/XObject");
        if (xobjects == null) { xobjects = new PdfDictionary(doc); pageResources.Elements["/XObject"] = xobjects; }
        xobjects.Elements[name] = form.Reference;

        // Isolate the original content's graphics state, then draw the layer on top.
        if (page.Contents.Elements.Count > 0) SetStream(page.Contents.PrependContent(), "q\n");
        SetStream(page.Contents.AppendContent(), (page.Contents.Elements.Count > 1 ? "\nQ\n" : "\n") + $"q {m} cm {name} Do Q\n");
    }

    /// <summary>Removes a text layer added by a previous Legible run. Returns true if one was found.</summary>
    static bool RemoveLayer(PdfPage page)
    {
        var elements = page.Contents.Elements;
        bool found = false;
        for (int k = elements.Count - 1; k >= 0; k--)
        {
            var dict = elements.GetDictionary(k);
            var bytes = dict?.Stream?.UnfilteredValue;
            if (bytes == null || bytes.Length > 512) continue;
            var text = Encoding.ASCII.GetString(bytes);
            var match = LayerPattern().Match(text);
            if (!match.Success) continue;
            found = true;
            elements.RemoveAt(k);
            var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            xobjects?.Elements.Remove(match.Value[..^3]);
        }
        if (found && elements.Count > 0 && elements.GetDictionary(0)?.Stream?.UnfilteredValue is { } first && Encoding.ASCII.GetString(first) == "q\n")
            elements.RemoveAt(0);
        return found;
    }

    static void SetStream(PdfContent content, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        if (content.Stream != null) content.Stream.Value = bytes; else content.CreateStream(bytes);
    }

    static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>The area viewers and PDFium display: CropBox clipped to MediaBox.</summary>
    static PdfRectangle VisibleBox(PdfPage page)
    {
        var media = page.MediaBox;
        var crop = page.Elements.GetRectangle("/CropBox");
        if (crop.IsZero) return media;
        double x1 = Math.Max(Math.Min(media.X1, media.X2), Math.Min(crop.X1, crop.X2));
        double y1 = Math.Max(Math.Min(media.Y1, media.Y2), Math.Min(crop.Y1, crop.Y2));
        double x2 = Math.Min(Math.Max(media.X1, media.X2), Math.Max(crop.X1, crop.X2));
        double y2 = Math.Min(Math.Max(media.Y1, media.Y2), Math.Max(crop.Y1, crop.Y2));
        return x2 > x1 && y2 > y1 ? new PdfRectangle(new XPoint(x1, y1), new XPoint(x2, y2)) : media;
    }

    /// <summary>PDF affine matrix [a b c d e f]: x' = a x + c y + e, y' = b x + d y + f.</summary>
    readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
        public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);
        public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
        /// <summary>Counter-clockwise rotation (PDF y-up space) about a point.</summary>
        public static Affine RotateAbout(double cx, double cy, double degrees)
        {
            double t = degrees * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
            return Translate(-cx, -cy).Then(new Affine(cos, sin, -sin, cos, 0, 0)).Then(Translate(cx, cy));
        }
        /// <summary>This transform followed by <paramref name="o"/>.</summary>
        public Affine Then(Affine o) => new(
            o.A * A + o.C * B, o.B * A + o.D * B,
            o.A * C + o.C * D, o.B * C + o.D * D,
            o.A * E + o.C * F + o.E, o.B * E + o.D * F + o.F);
        public override string ToString() => string.Join(' ', new[] { A, B, C, D, E, F }.Select(v => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture)));
    }
}
