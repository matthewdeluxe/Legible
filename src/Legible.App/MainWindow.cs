using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Legible.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
namespace Legible;

public sealed class MainWindow : Window
{
 static readonly string[] ModeKeys = ["skip-text", "force-ocr", "redo-ocr", "strict"];
 readonly ObservableCollection<string> files = new();
 readonly ListView fileList = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 220 };
 readonly TextBox output = new(), suffix = new() { Text = "_ocr" };
 readonly CheckBox overwrite = Check("Replace original PDFs (confirmation required)"), deskew = Check("Deskew (visibly straightens pages only with Force OCR)", true), rotate = Check("Auto-rotate sideways and upside-down pages", true);
 readonly CheckBox backup = Check("Keep a backup of each original (.bak)", true);
 readonly ComboBox mode = new();
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
 static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
 readonly string config = Path.Combine(AppData, "Legible", "settings.json");
 CancellationTokenSource? cts;
 string? lastFolder;
 bool closing;
 static CheckBox Check(string label, bool selected = false) => new() { Content = label, IsChecked = selected };
 static TextBlock Label(string text) => new() { Text = text, FontSize = 15 };
 static Button Btn(string text, RoutedEventHandler handler) { var b = new Button { Content = text }; b.Click += handler; return b; }
 static StackPanel Row(params UIElement[] children) { var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var child in children) r.Children.Add(child); return r; }
 public MainWindow()
 {
  Title = "Legible"; AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Legible.ico")); AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 850)); SystemBackdrop = new MicaBackdrop();
  root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
  var header = new StackPanel { Spacing = 2 }; header.Children.Add(new TextBlock { Text = "Legible", FontSize = 30 }); header.Children.Add(new TextBlock { Text = "Make scanned PDFs searchable. Everything runs on this PC.", Opacity = 0.7 });
  root.Children.Add(header); Grid.SetRow(nav, 1); root.Children.Add(nav);

  fileList.ItemsSource = files; filePanel.Children.Add(Label("PDF queue — folders include subfolders")); filePanel.Children.Add(fileList);
  filePanel.Children.Add(Row(Btn("Add files…", AddFiles), Btn("Add folder…", AddFolder), Btn("Paste paths…", PastePaths), Btn("Remove selected", (_, _) => { foreach (string f in fileList.SelectedItems.ToArray()) files.Remove(f); }), Btn("Clear all", (_, _) => files.Clear())));
  filePanel.Children.Add(overwrite); filePanel.Children.Add(backup); filePanel.Children.Add(Label("Output folder (blank saves beside each original)")); filePanel.Children.Add(output); filePanel.Children.Add(Btn("Browse output…", BrowseOutput)); filePanel.Children.Add(Label("Filename suffix")); filePanel.Children.Add(suffix);
  overwrite.Checked += (_, _) => OutputState(); overwrite.Unchecked += (_, _) => OutputState();
  foreach (var m in new[] { "Skip pages that already have text", "Force OCR on every page (pages become images)", "Redo OCR (replace text added by Legible)", "Strict (fail if any page has text)" }) mode.Items.Add(m);
  mode.SelectedIndex = 0; settingsPanel.Children.Add(Label("Text handling")); settingsPanel.Children.Add(mode);
  foreach (var c in new[] { deskew, rotate }) settingsPanel.Children.Add(c);
  settingsPanel.Children.Add(new TextBlock { Text = "OCR language: English.", Opacity = 0.7 });
  var logs = new StackPanel { Spacing = 12 }; logs.Children.Add(Row(Btn("Clear log", (_, _) => log.Text = ""), Btn("Save log…", SaveLog))); logs.Children.Add(log);
  logViewport.Content = logs;
  log.LayoutUpdated += (_, _) => { if (!logScrollPending || !logViewport.IsLoaded) return; logScrollPending = false; logViewport.ChangeView(null, logViewport.ScrollableHeight, null, disableAnimation: true); };
  logViewport.Loaded += (_, _) => { logScrollPending = true; logViewport.ChangeView(null, logViewport.ScrollableHeight, null, disableAnimation: true); };
  nav.TabItems.Add(new TabViewItem { Header = "Files & output", IsClosable = false, Content = new ScrollViewer { Content = filePanel } }); nav.TabItems.Add(new TabViewItem { Header = "OCR settings", IsClosable = false, Content = new ScrollViewer { Content = settingsPanel } }); nav.TabItems.Add(new TabViewItem { Header = "Log & results", IsClosable = false, Content = logViewport });
  var footer = new StackPanel { Spacing = 10 }; footer.Children.Add(Row(run, cancel, open)); footer.Children.Add(progress); footer.Children.Add(status); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
  LoadSettings(); OutputState(); run.Click += Run; cancel.Click += (_, _) => cts?.Cancel(); open.Click += async (_, _) => { try { if (lastFolder != null) Process.Start(new ProcessStartInfo(lastFolder) { UseShellExecute = true }); } catch (Exception ex) { await Error(ex.Message); } };
  Closed += (_, _) => { cts?.Cancel(); SaveSettings(); };
  AppWindow.Closing += async (_, e) => { if (cts == null || closing) return; e.Cancel = true; if (await Confirm("Close while OCR is running?", "The current batch will be cancelled. Originals are not changed.")) { closing = true; cts?.Cancel(); Close(); } };
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
 async Task<bool> Confirm(string title, string message) => await new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = message, PrimaryButtonText = "Continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
 async Task Error(string message) => await new ContentDialog { XamlRoot = root.XamlRoot, Title = "Legible", Content = message, CloseButtonText = "OK" }.ShowAsync();
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
 async void SaveLog(object sender, RoutedEventArgs e) { try { var p = new FileSavePicker { SuggestedFileName = "Legible-log" }; InitPicker(p); p.FileTypeChoices.Add("Text", new List<string> { ".txt" }); var f = await p.PickSaveFileAsync(); if (f != null) await File.WriteAllTextAsync(f.Path, log.Text); } catch (Exception ex) { await Error(ex.Message); } }
 OcrOptions Options() => new() { Mode = (TextMode)mode.SelectedIndex, Deskew = deskew.IsChecked == true, AutoRotate = rotate.IsChecked == true };
 sealed class LogProgress(Action<string> write) : IProgress<string> { public void Report(string value) => write(value); }
 async void Run(object sender, RoutedEventArgs e)
 {
  if (cts != null) return;
  try
  {
   if (files.Count == 0) throw new ArgumentException("Add at least one PDF."); if (files.Any(f => !File.Exists(f))) throw new ArgumentException("One or more inputs no longer exist.");
   if (suffix.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The suffix contains invalid filename characters.");
   var replace = overwrite.IsChecked == true; var options = Options();
   var jobs = files.Select(f => (Input: f, Output: replace ? f : Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(output.Text) ? Path.GetDirectoryName(f)! : output.Text.Trim(), Path.GetFileNameWithoutExtension(f) + suffix.Text.Trim() + ".pdf")))).ToArray();
   var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach (var j in jobs) { if (!targets.Add(j.Output)) throw new ArgumentException("Two inputs produce the same filename. Use separate batches or output folders."); if (!replace && (files.Any(f => string.Equals(Path.GetFullPath(f), j.Output, StringComparison.OrdinalIgnoreCase)) || File.Exists(j.Output))) throw new ArgumentException("An output would replace an existing file: " + j.Output); }
   if (replace && !await Confirm("Replace original PDFs?", "Successful PDFs will replace originals. Failed or cancelled jobs leave originals intact.")) return;
   cts = new(); var token = cts.Token; SetEnabled(filePanel, false); SetEnabled(settingsPanel, false); run.IsEnabled = open.IsEnabled = false; cancel.IsEnabled = true; nav.SelectedIndex = 2; progress.Maximum = jobs.Length; progress.Value = 0; lastFolder = null; int succeeded = 0, failed = 0, completed = 0; SaveSettings();
   var progressLog = new LogProgress(Append);
   foreach (var job in jobs)
   {
    if (token.IsCancellationRequested) break; status.Text = $"Processing {completed + 1}/{jobs.Length}: {Path.GetFileName(job.Input)}";
    Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!); var temporary = Path.Combine(Path.GetDirectoryName(job.Output)!, ".ocr-" + Guid.NewGuid().ToString("N") + ".pdf");
    try
    {
     Append("\n=== " + Path.GetFileName(job.Input) + " ===");
     await PdfOcr.ProcessAsync(job.Input, temporary, options, progressLog, token);
     if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("No output PDF was produced.");
     if (replace) File.Replace(temporary, job.Output, backup.IsChecked == true ? job.Output + ".bak" : null); else File.Move(temporary, job.Output, false); succeeded++; lastFolder = Path.GetDirectoryName(job.Output); Append("Saved: " + job.Output);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex) { if (token.IsCancellationRequested) break; failed++; Append("FAILED: " + (ex is OcrException ? ex.Message : ex.GetBaseException().Message)); }
    finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) { Append("Temporary file cleanup: " + ex.Message); } }
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
   // Fall back to settings from the earlier OCRmyPDF front end so existing users keep their choices.
   var path = new[] { config, Path.Combine(AppData, "OCRmyPDF WinUI", "settings.json"), Path.Combine(AppData, "OCRmyPDF GUI", "settings.json") }.FirstOrDefault(File.Exists);
   if (path == null) return;
   using var doc = JsonDocument.Parse(File.ReadAllText(path)); var s = doc.RootElement;
   string Text(string key, string fallback) => s.TryGetProperty(key, out var v) ? v.ToString() : fallback;
   bool Bool(string key, bool fallback) => s.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
   output.Text = Text("output_dir", ""); suffix.Text = Text("suffix", "_ocr"); mode.SelectedIndex = Math.Max(0, Array.IndexOf(ModeKeys, Text("mode", "skip-text")));
   deskew.IsChecked = Bool("deskew", true); rotate.IsChecked = Bool("rotate", true); backup.IsChecked = Bool("keep_backup", true);
  } catch (Exception ex) { Append("Settings: using defaults. " + ex.Message); }
 }
 void SaveSettings()
 {
  try { Directory.CreateDirectory(Path.GetDirectoryName(config)!); File.WriteAllText(config, JsonSerializer.Serialize(new { output_dir = output.Text, suffix = suffix.Text, mode = ModeKeys[mode.SelectedIndex], deskew = deskew.IsChecked == true, rotate = rotate.IsChecked == true, keep_backup = backup.IsChecked == true }, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { Append("Could not save settings: " + ex.Message); }
 }
}
