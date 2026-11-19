using Legible.Core;

const string Usage = """
Usage: legible [options] <input.pdf> <output.pdf>

Adds a searchable text layer to a PDF. Everything runs locally.

Options:
  --mode <skip|force|redo|strict>  Pages that already have text (default: skip)
                                     skip    leave them as they are
                                     force   rasterize and OCR every page
                                     redo    replace text layers added by Legible
                                     strict  fail if any page has text
  --no-deskew                      Don't correct skew
  --no-rotate                      Don't auto-rotate sideways pages
  -l, --lang <codes>               Tesseract languages, e.g. eng or eng+deu (default: eng)
  -j, --jobs <n>                   Pages processed in parallel
  --dpi <n>                        Render resolution for OCR (default: 300)
  --tessdata <folder>              Folder with *.traineddata and pdf.ttf
  -q, --quiet                      Only print errors
""";

var positional = new List<string>();
var options = new OcrOptions();
bool quiet = false;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
        switch (args[i])
        {
            case "-h" or "--help" or "/?": Console.WriteLine(Usage); return 0;
            case "--mode":
                options = options with
                {
                    Mode = Next() switch
                    {
                        "skip" => TextMode.SkipText, "force" => TextMode.ForceOcr, "redo" => TextMode.RedoOcr, "strict" => TextMode.Strict,
                        var m => throw new ArgumentException("Unknown mode: " + m),
                    }
                };
                break;
            case "--no-deskew": options = options with { Deskew = false }; break;
            case "--no-rotate": options = options with { AutoRotate = false }; break;
            case "-l" or "--lang": options = options with { Language = Next() }; break;
            case "-j" or "--jobs": options = options with { Jobs = int.Parse(Next()) }; break;
            case "--dpi": options = options with { Dpi = int.Parse(Next()) }; break;
            case "--tessdata": options = options with { TessdataPath = Next() }; break;
            case "-q" or "--quiet": quiet = true; break;
            case var a when a.StartsWith('-') && a.Length > 1: throw new ArgumentException("Unknown option: " + a);
            default: positional.Add(args[i]); break;
        }
    }
    if (positional.Count != 2) throw new ArgumentException("Expected an input and an output file.");
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    // Write to a temporary file first so a failure never leaves a half-written output.
    var output = Path.GetFullPath(positional[1]);
    var temporary = Path.Combine(Path.GetDirectoryName(output)!, ".legible-" + Guid.NewGuid().ToString("N") + ".pdf");
    try
    {
        var result = PdfOcr.Process(Path.GetFullPath(positional[0]), temporary, options, new SyncProgress(line => { if (!quiet) Console.WriteLine(line); }), cts.Token);
        File.Move(temporary, output, overwrite: true);
        if (!quiet) Console.WriteLine($"Saved {output} ({result.OcrPages} of {result.PageCount} page(s) OCR'd)");
        return 0;
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + (ex is OcrException ? ex.Message : ex.GetBaseException().Message));
    if (Environment.GetEnvironmentVariable("LEGIBLE_DEBUG") == "1") Console.Error.WriteLine(ex);
    return 1;
}

sealed class SyncProgress(Action<string> report) : IProgress<string>
{
    readonly Lock gate = new();
    public void Report(string value) { lock (gate) report(value); }
}
