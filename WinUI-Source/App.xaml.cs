using Microsoft.UI.Xaml;
namespace OcrMyPdf.WinUI;
public partial class App : Application
{
 private MainWindow? window;
 public App() { UnhandledException += (_, e) => { try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), e.Exception.ToString()); } catch { } }; InitializeComponent(); }
 protected override void OnLaunched(LaunchActivatedEventArgs args) { window = new MainWindow(); window.Activate(); }
}
