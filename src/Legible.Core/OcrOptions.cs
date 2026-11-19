namespace Legible.Core;

/// <summary>What to do with pages that already contain text.</summary>
public enum TextMode
{
    /// <summary>OCR only pages without text; leave pages with text untouched.</summary>
    SkipText,
    /// <summary>Rasterize every page and OCR it. Existing text and vector content become an image.</summary>
    ForceOcr,
    /// <summary>Replace text layers previously added by Legible; skip pages with other text.</summary>
    RedoOcr,
    /// <summary>Fail if any page already contains text.</summary>
    Strict,
}

public sealed record OcrOptions
{
    public TextMode Mode { get; init; } = TextMode.SkipText;

    /// <summary>Correct small skew. With <see cref="TextMode.ForceOcr"/> the visible page is straightened;
    /// otherwise the page image is left as is and only the text layer follows the skew.</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>Detect sideways or upside-down pages and set the page rotation so they display upright.</summary>
    public bool AutoRotate { get; init; } = true;

    /// <summary>Minimum orientation confidence before a page is rotated.</summary>
    public float RotateConfidence { get; init; } = 14f;

    /// <summary>Resolution pages are rendered at for OCR.</summary>
    public int Dpi { get; init; } = 300;

    /// <summary>Tesseract language code(s), for example "eng" or "eng+deu".</summary>
    public string Language { get; init; } = "eng";

    /// <summary>Pages processed in parallel.</summary>
    public int Jobs { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Folder holding *.traineddata and pdf.ttf. Defaults to "tessdata" next to the app.</summary>
    public string? TessdataPath { get; init; }

    internal string ResolvedTessdata => TessdataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
}

public sealed record OcrResult(int PageCount, int OcrPages, int SkippedPages, int RotatedPages);

public sealed class OcrException(string message, Exception? inner = null) : Exception(message, inner);
