using Microsoft.UI.Xaml;
using Notaekinesis.App.Services;

namespace Notaekinesis.App;

public partial class App : Application
{
    private AppSession? _session;
    private MainWindow? _window;
    private readonly string _dataDir = Environment.GetEnvironmentVariable("NOTAEKINESIS_DATA_DIR") ?? Core.Settings.DefaultDirectory;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _session = new AppSession(_dataDir);
        _session.TryResume();
        _window = new MainWindow(_session);
        _window.Closed += async (_, _) => await _session.DisposeAsync();
        _window.Activate();
    }

    /// <summary>Appends to crash.log in the data folder, so problems in release builds can be reported.</summary>
    private void LogCrash(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.AppendAllText(Path.Combine(_dataDir, "crash.log"), $"{DateTimeOffset.Now:O} {e}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
