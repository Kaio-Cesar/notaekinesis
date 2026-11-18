using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Notaekinesis.App.Services;

namespace Notaekinesis.App;

public partial class App : Application
{
    /// <summary>Win+Shift+N opens the basket. (Win+Shift+S is the screenshot tool.)</summary>
    private const uint HotkeyModifiers = Native.MOD_WIN | Native.MOD_SHIFT;
    private const uint HotkeyKey = 'N';

    /// <summary>The value name under the Run key, as shown in Task Manager's Startup apps.</summary>
    internal const string StartupName = "notaekinesis";

    private readonly string _dataDir = Environment.GetEnvironmentVariable("NOTAEKINESIS_DATA_DIR") ?? Core.Settings.DefaultDirectory;
    private Mutex? _instance;
    private AppSession? _session;
    private MainWindow? _window;
    private BasketWindow? _basket;
    private ShellHost? _shell;
    private EdgeStrip? _edge;
    private bool _quitting;

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
        // One instance per data folder: a second launch brings the running one forward.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_dataDir).ToUpperInvariant())))[..16];
        var instanceName = "notaekinesis-" + key;
        _instance = new Mutex(initiallyOwned: true, @"Local\" + instanceName, out var first);
        if (!first)
        {
            ShellHost.SignalExisting(instanceName);
            Exit();
            return;
        }

        _session = new AppSession(_dataDir);
        _session.TryResume();
        _window = new MainWindow(_session);
        _basket = new BasketWindow(_session);

        _shell = new ShellHost(instanceName, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), "notaekinesis");
        _shell.OpenRequested += (_, _) => Native.BringToFront(_window);
        _shell.QuickAddRequested += (_, _) => _basket.Toggle();
        _shell.QuitRequested += async (_, _) => await Quit();
        _shell.Error += (_, e) => LogCrash(e);
        if (!_shell.RegisterHotkey(HotkeyModifiers, HotkeyKey))
            _window.ShowNotice("Win+Shift+N is taken by another app, so the quick-add hotkey is off. The tray menu still opens it.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);

        // Dragging something onto the screen edge opens the basket right there.
        // It's an extra: if it fails, the rest of the app carries on.
        try
        {
            var strip = new EdgeStrip();
            _edge = strip;
            strip.DragArrived += (_, point) => _basket.ShowAtEdge(point, strip.Edge);
            strip.Place(ParseEdge(_session.Settings.DropEdge));
            _session.StateChanged += (_, _) => _window.DispatcherQueue.TryEnqueue(() =>
            {
                var edge = ParseEdge(_session.Settings.DropEdge);
                if (edge != strip.Edge) strip.Place(edge);
            });
            if (strip.Problem is not null) LogCrash(new InvalidOperationException(strip.Problem));
        }
        catch (Exception e)
        {
            LogCrash(e);
        }

        // Closing the window keeps the app in the tray, ready for the hotkey; Quit is in the tray menu.
        _window.AppWindow.Closing += (sender, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;
            sender.Hide();
        };

        // If the app was moved since "Start with Windows" was turned on, follow it.
        try
        {
            new Core.StartupRegistration(StartupName).UpdatePath(Environment.ProcessPath!);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LogCrash(e);
        }

        // Started with Windows: stay in the tray until needed. Signed out there's nothing to wait
        // for in the background, so show the sign-in instead.
        var background = Environment.GetCommandLineArgs().Contains(Core.StartupRegistration.BackgroundArg);
        if (!background || !_session.IsSignedIn) _window.Activate();
    }

    private async Task Quit()
    {
        _quitting = true;
        _shell?.Dispose();
        _edge?.Dispose();
        _basket?.Shutdown();
        _window?.Close();
        if (_session is not null) await _session.DisposeAsync();
        _instance?.ReleaseMutex();
        Exit();
    }

    private static Edge ParseEdge(string value) =>
        value switch
        {
            "left" => Edge.Left,
            "off" => Edge.Off,
            _ => Edge.Right,
        };

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
