using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
namespace OcrMyPdf.WinUI;

public sealed class MainWindow : Window
{
 readonly ObservableCollection<string> files = new();
 readonly ListView fileList = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 220 };
 readonly TextBox output = new(), suffix = new() { Text = "_ocr" }, extra = new(), tool = new();
 readonly CheckBox overwrite = Check("Replace original PDFs (confirmation required)"), deskew = Check("Deskew", true), rotate = Check("Auto-rotate pages", true), clean = Check("Clean before OCR (unpaper)"), cleanFinal = Check("Clean final output"), background = Check("Remove background"), pdfa = Check("Archival PDF/A", true);
 readonly CheckBox backup = Check("Keep a backup of each original (.bak)", true);
 readonly ComboBox mode = new(), optimize = new();
 readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), MinHeight = 330 };
 readonly ScrollViewer logViewport = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
 bool logScrollPending;
 readonly System.Text.StringBuilder logBuffer = new(); bool logFlushQueued; const int MaxLogChars = 400_000;
 readonly TextBlock status = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
 readonly ProgressBar progress = new();
 readonly Button run = new() { Content = "Run OCR" }, cancel = new() { Content = "Cancel", IsEnabled = false }, open = new() { Content = "Open output folder", IsEnabled = false };
 readonly StackPanel filePanel = new() { Spacing = 12 }, settingsPanel = new() { Spacing = 12 };
 readonly Grid root = new() { Padding = new Thickness(24), RowSpacing = 16 };
 readonly TabView nav = new() { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
 readonly string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OCRmyPDF WinUI", "settings.json");
 CancellationTokenSource? cts;
 Process? process;
 string? lastFolder;
 bool closing;
 static CheckBox Check(string label, bool selected = false) => new() { Content = label, IsChecked = selected };
 static TextBlock Label(string text) => new() { Text = text, FontSize = 15 };
 static Button Btn(string text, RoutedEventHandler handler) { var b = new Button { Content = text }; b.Click += handler; return b; }
 static StackPanel Row(params UIElement[] children) { var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var child in children) r.Children.Add(child); return r; }
 public MainWindow()
 {
  Title = "OCRmyPDF — WinUI 3"; AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "OcrMyPdf.ico")); AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 850)); SystemBackdrop = new MicaBackdrop();
  root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
  root.Children.Add(new TextBlock { Text = "OCRmyPDF", FontSize = 30 }); Grid.SetRow(nav, 1); root.Children.Add(nav);
  
  fileList.ItemsSource = files; filePanel.Children.Add(Label("PDF queue — folders include subfolders")); filePanel.Children.Add(fileList);
  filePanel.Children.Add(Row(Btn("Add files…", AddFiles), Btn("Add folder…", AddFolder), Btn("Paste paths…", PastePaths), Btn("Remove selected", (_, _) => { foreach (string f in fileList.SelectedItems.ToArray()) files.Remove(f); }), Btn("Clear all", (_, _) => files.Clear())));
  filePanel.Children.Add(overwrite); filePanel.Children.Add(backup); filePanel.Children.Add(Label("Output folder (blank saves beside each original)")); filePanel.Children.Add(output); filePanel.Children.Add(Btn("Browse output…", BrowseOutput)); filePanel.Children.Add(Label("Filename suffix")); filePanel.Children.Add(suffix);
  overwrite.Checked += (_, _) => OutputState(); overwrite.Unchecked += (_, _) => OutputState();
  settingsPanel.Children.Add(Label("OCRmyPDF executable")); settingsPanel.Children.Add(tool);
  foreach (var m in new[] { "Skip pages with text", "Force OCR on every page", "Redo existing OCR", "Strict (fail on existing text)" }) mode.Items.Add(m);
  mode.SelectedIndex = 0; settingsPanel.Children.Add(Label("Text handling")); settingsPanel.Children.Add(mode);
  foreach (var c in new[] { deskew, rotate, clean, cleanFinal, background, pdfa }) settingsPanel.Children.Add(c);
  for (int i = 0; i < 4; i++) optimize.Items.Add(i.ToString()); optimize.SelectedIndex = 1;
  settingsPanel.Children.Add(Label("Optimization level")); settingsPanel.Children.Add(optimize); settingsPanel.Children.Add(Label("Extra arguments (quote values containing spaces)")); settingsPanel.Children.Add(extra);
  var logs = new StackPanel { Spacing = 12 }; logs.Children.Add(Row(Btn("Clear log", (_, _) => log.Text = ""), Btn("Save log…", SaveLog))); logs.Children.Add(log);
  logViewport.Content = logs;
  log.LayoutUpdated += (_, _) => { if (!logScrollPending || !logViewport.IsLoaded) return; logScrollPending = false; logViewport.ChangeView(null, logViewport.ScrollableHeight, null, disableAnimation: true); };
  logViewport.Loaded += (_, _) => { logScrollPending = true; logViewport.ChangeView(null, logViewport.ScrollableHeight, null, disableAnimation: true); };
  nav.TabItems.Add(new TabViewItem { Header = "Files & output", IsClosable = false, Content = new ScrollViewer { Content = filePanel } }); nav.TabItems.Add(new TabViewItem { Header = "OCR settings", IsClosable = false, Content = new ScrollViewer { Content = settingsPanel } }); nav.TabItems.Add(new TabViewItem { Header = "Log & results", IsClosable = false, Content = logViewport });
  var footer = new StackPanel { Spacing = 10 }; footer.Children.Add(Row(run, cancel, open)); footer.Children.Add(progress); footer.Children.Add(status); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
  tool.Text = FindTool("ocrmypdf"); LoadSettings(); OutputState(); run.Click += Run; cancel.Click += (_, _) => Cancel(); open.Click += async (_, _) => { try { if (lastFolder != null) Process.Start(new ProcessStartInfo(lastFolder) { UseShellExecute = true }); } catch (Exception ex) { await Error(ex.Message); } };
  Closed += (_, _) => { Cancel(); SaveSettings(); };
  AppWindow.Closing += async (_, e) => { if (cts == null || closing) return; e.Cancel = true; if (await Confirm("Close while OCR is running?", "The running OCR process will be cancelled.")) { closing = true; Cancel(); Close(); } };
  foreach (var input in Environment.GetCommandLineArgs().Skip(1)) if (File.Exists(input) && Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) Add(Path.GetFullPath(input));

 }
 static void SetEnabled(DependencyObject node, bool enabled) { if (node is Control control) control.IsEnabled = enabled; if (node is Panel panel) foreach (var child in panel.Children) SetEnabled(child, enabled); }
 void OutputState() { output.IsEnabled = suffix.IsEnabled = overwrite.IsChecked != true; backup.IsEnabled = overwrite.IsChecked == true; }
 void Append(string text) => DispatcherQueue.TryEnqueue(() =>
 {
  // Batch lines and flush at low priority; appending to log.Text per line is quadratic on big batches.
  logBuffer.Append(text).Append(Environment.NewLine);
  if (logFlushQueued) return;
  logFlushQueued = true;
  DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
  {
   logFlushQueued = false; var combined = log.Text + logBuffer.ToString(); logBuffer.Clear();
   log.Text = combined.Length > MaxLogChars ? combined[^MaxLogChars..] : combined; logScrollPending = true;
  });
 });
 void Cancel() { cts?.Cancel(); try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { } }
 async Task<bool> Confirm(string title, string message) => await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = message, PrimaryButtonText = "Continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
 async Task Error(string message) => await new ContentDialog { XamlRoot = root.XamlRoot, Title = "OCRmyPDF", Content = message, CloseButtonText = "OK" }.ShowAsync();
 void InitPicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
 void Add(string path) { if (!files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) files.Add(path); }
 async void AddFiles(object sender, RoutedEventArgs e) { try { var p = new FileOpenPicker(); InitPicker(p); p.FileTypeFilter.Add(".pdf"); foreach (var f in await p.PickMultipleFilesAsync()) Add(f.Path); } catch (Exception ex) { await Error(ex.Message); } }
 async void PastePaths(object sender, RoutedEventArgs e)
 {
  var entry = new TextBox { AcceptsReturn = true, MinHeight = 100, PlaceholderText = "One PDF path per line" };
  var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Add PDF paths", Content = entry, PrimaryButtonText = "Add", CloseButtonText = "Cancel" };
  if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
  try { var paths = entry.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => Path.GetFullPath(p.Trim().Trim('"'))).ToArray(); if (paths.Any(p => !File.Exists(p) || !Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Every path must point to an existing PDF."); foreach (var path in paths) Add(path); } catch (Exception ex) { await Error(ex.Message); }
 }
 async Task<string?> PickFolder() { var p = new FolderPicker(); InitPicker(p); p.FileTypeFilter.Add("*"); return (await p.PickSingleFolderAsync())?.Path; }
 async void AddFolder(object sender, RoutedEventArgs e) { try { var folder = await PickFolder(); if (folder == null) return; var found = await Task.Run(() => Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).Order().ToArray()); foreach (var f in found) Add(f); } catch (Exception ex) { await Error(ex.Message); } }
 async void BrowseOutput(object sender, RoutedEventArgs e) { try { var f = await PickFolder(); if (f != null) output.Text = f; } catch (Exception ex) { await Error(ex.Message); } }
 async void SaveLog(object sender, RoutedEventArgs e) { try { var p = new FileSavePicker { SuggestedFileName = "OCRmyPDF-log" }; InitPicker(p); p.FileTypeChoices.Add("Text", new List<string> { ".txt" }); var f = await p.PickSaveFileAsync(); if (f != null) await File.WriteAllTextAsync(f.Path, log.Text); } catch (Exception ex) { await Error(ex.Message); } }
 static bool ToolExists(string exe)
 {
  if (Path.IsPathRooted(exe) || exe.Contains('\\') || exe.Contains('/')) return File.Exists(exe);
  foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) { try { if (dir.Length > 0 && (File.Exists(Path.Combine(dir, exe)) || File.Exists(Path.Combine(dir, exe + ".exe")))) return true; } catch (ArgumentException) { } }
  return false;
 }
 static string FindTool(string name)
 {
  foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) { try { var p = Path.Combine(dir, name + ".exe"); if (File.Exists(p)) return p; } catch (ArgumentException) { } }
  var scoop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "python", "current", "Scripts", name + ".exe"); return File.Exists(scoop) ? scoop : name;
 }
 List<string> Arguments()
 {
  var a = new List<string> { "-l", "eng" }; if (mode.SelectedIndex < 3) a.Add(new[] { "--skip-text", "--force-ocr", "--redo-ocr" }[mode.SelectedIndex]);
  foreach (var pair in new[] { (deskew, "--deskew"), (rotate, "--rotate-pages"), (clean, "--clean"), (cleanFinal, "--clean-final"), (background, "--remove-background") }) if (pair.Item1.IsChecked == true) a.Add(pair.Item2);
  a.AddRange(["--optimize", optimize.SelectedIndex.ToString(), "--output-type", pdfa.IsChecked == true ? "pdfa" : "pdf"]); a.AddRange(ParseArguments(extra.Text)); return a;
 }
 [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
 static string[] ParseArguments(string text) { if (string.IsNullOrWhiteSpace(text)) return []; var ptr = CommandLineToArgvW("ocrmypdf " + text, out int count); if (ptr == IntPtr.Zero) throw new ArgumentException("Cannot parse extra arguments."); try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!).ToArray(); } finally { LocalFree(ptr); } }
 async void Run(object sender, RoutedEventArgs e)
 {
  if (cts != null) return;
  try
  {
   if (files.Count == 0) throw new ArgumentException("Add at least one PDF."); if (files.Any(f => !File.Exists(f))) throw new ArgumentException("One or more inputs no longer exist.");
   if (mode.SelectedIndex == 2 && (deskew.IsChecked == true || cleanFinal.IsChecked == true || background.IsChecked == true)) throw new ArgumentException("Redo OCR cannot be combined with deskew, clean final, or background removal.");
   if (suffix.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The suffix contains invalid filename characters.");
   var replace = overwrite.IsChecked == true; var args = Arguments(); var exe = tool.Text.Trim().Trim('"'); if (exe.Length == 0) throw new ArgumentException("Specify the OCRmyPDF executable."); if (!ToolExists(exe)) throw new ArgumentException("OCRmyPDF executable not found: " + exe + ". Check the OCR settings tab.");
   var jobs = files.Select(f => (Input: f, Output: replace ? f : Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(output.Text) ? Path.GetDirectoryName(f)! : output.Text.Trim(), Path.GetFileNameWithoutExtension(f) + suffix.Text.Trim() + ".pdf")))).ToArray();
   var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach (var j in jobs) { if (!targets.Add(j.Output)) throw new ArgumentException("Two inputs produce the same filename. Use separate batches or output folders."); if (!replace && (files.Any(f => string.Equals(Path.GetFullPath(f), j.Output, StringComparison.OrdinalIgnoreCase)) || File.Exists(j.Output))) throw new ArgumentException("An output would replace an existing file: " + j.Output); }
   if (replace && !await Confirm("Replace original PDFs?", "Successful PDFs will replace originals. Failed or cancelled jobs leave originals intact.")) return;
   cts = new(); var token = cts.Token; SetEnabled(filePanel, false); SetEnabled(settingsPanel, false); run.IsEnabled = open.IsEnabled = false; cancel.IsEnabled = true; nav.SelectedIndex = 2; progress.Maximum = jobs.Length; progress.Value = 0; lastFolder = null; int succeeded = 0, failed = 0, completed = 0; SaveSettings();
   foreach (var job in jobs)
   {
    if (token.IsCancellationRequested) break; status.Text = $"Processing {completed + 1}/{jobs.Length}: {Path.GetFileName(job.Input)}";
    Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!); var temporary = Path.Combine(Path.GetDirectoryName(job.Output)!, ".ocr-" + Guid.NewGuid().ToString("N") + ".pdf");
    try
    {
     var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 }; info.Environment["PYTHONUTF8"] = "1"; foreach (var a in args) info.ArgumentList.Add(a); info.ArgumentList.Add(job.Input); info.ArgumentList.Add(temporary); Append("\n=== " + Path.GetFileName(job.Input) + " ===");
     process = new Process { StartInfo = info }; process.Start(); using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
     async Task Pump(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) Append(line); }
     await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync()); if (token.IsCancellationRequested) break; if (process.ExitCode != 0) throw new IOException("OCRmyPDF exit code " + process.ExitCode);
     if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("No output PDF was produced.");
     if (replace) File.Replace(temporary, job.Output, backup.IsChecked == true ? job.Output + ".bak" : null); else File.Move(temporary, job.Output, false); succeeded++; lastFolder = Path.GetDirectoryName(job.Output); Append("Saved: " + job.Output);
    }
    catch (Exception ex) { if (token.IsCancellationRequested) break; failed++; Append("FAILED: " + ex.Message); }
    finally { process?.Dispose(); process = null; try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) { Append("Temporary file cleanup: " + ex.Message); } }
    progress.Value = ++completed;
   }
   status.Text = $"Succeeded: {succeeded}   Failed: {failed}   {(token.IsCancellationRequested ? "Cancelled" : "Unprocessed")}: {jobs.Length - completed}"; Append(status.Text);
  }
  catch (Exception ex) { await Error(ex.Message); }
  finally { cts?.Dispose(); cts = null; SetEnabled(filePanel, true); SetEnabled(settingsPanel, true); run.IsEnabled = true; cancel.IsEnabled = false; open.IsEnabled = lastFolder != null; OutputState(); }
 }
 void LoadSettings()
 {
  try
  {
   var path = File.Exists(config) ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OCRmyPDF GUI", "settings.json"); using var doc = JsonDocument.Parse(File.ReadAllText(path)); var s = doc.RootElement;
   string Text(string key, string fallback) => s.TryGetProperty(key, out var v) ? v.ToString() : fallback;
   bool Bool(string key, bool fallback) => s.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
   output.Text = Text("output_dir", ""); suffix.Text = Text("suffix", "_ocr"); extra.Text = Text("extra_args", ""); var savedTool = Text("executable", ""); if (savedTool.Length > 0 && ToolExists(savedTool)) tool.Text = savedTool; mode.SelectedIndex = Array.IndexOf(new[] { "skip-text", "force-ocr", "redo-ocr", "strict" }, Text("mode", "skip-text")); if (mode.SelectedIndex < 0) mode.SelectedIndex = 0;
   deskew.IsChecked = Bool("deskew", true); rotate.IsChecked = Bool("rotate", true); clean.IsChecked = Bool("clean", false); cleanFinal.IsChecked = Bool("clean_final", false); background.IsChecked = Bool("remove_background", false); pdfa.IsChecked = Bool("pdfa", true); backup.IsChecked = Bool("keep_backup", true); optimize.SelectedIndex = int.TryParse(Text("optimize", "1"), out var n) ? Math.Clamp(n, 0, 3) : 1;
  } catch (Exception ex) { Append("Settings: using defaults. " + ex.Message); }
 }
 void SaveSettings()
 {
  try { Directory.CreateDirectory(Path.GetDirectoryName(config)!); File.WriteAllText(config, JsonSerializer.Serialize(new { output_dir = output.Text, suffix = suffix.Text, mode = new[] { "skip-text", "force-ocr", "redo-ocr", "strict" }[mode.SelectedIndex], deskew = deskew.IsChecked == true, rotate = rotate.IsChecked == true, clean = clean.IsChecked == true, clean_final = cleanFinal.IsChecked == true, remove_background = background.IsChecked == true, pdfa = pdfa.IsChecked == true, keep_backup = backup.IsChecked == true, optimize = optimize.SelectedIndex.ToString(), languages = new[] { "eng" }, extra_args = extra.Text, executable = tool.Text }, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { Append("Could not save settings: " + ex.Message); }
 }
}





