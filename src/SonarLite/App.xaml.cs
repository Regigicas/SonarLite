using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using SonarLite.Core;

namespace SonarLite;

public partial class App : System.Windows.Application
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonarLite");

    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequest;
    private EventWaitHandle? _exitRequest;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _log;
    private GgHost? _gg;
    private SonarProcess? _sonar;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\SonarLite", out bool isFirst);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SonarLite.Show");
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SonarLite.Exit");
        if (!isFirst)
        {
            // Already running: forward the request to that instance (open its window, or --exit).
            (e.Args.Contains("--exit") ? _exitRequest : _showRequest).Set();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--exit"))
        {
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_showRequest,
            (_, _) => Dispatcher.InvokeAsync(OpenWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_exitRequest,
            (_, _) => Dispatcher.InvokeAsync(ExitAsync), null, Timeout.Infinite, executeOnlyOnce: true);

        var logDirectory = Path.Combine(DataDirectory, "logs");
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(new FileLoggerProvider(Path.Combine(logDirectory, "sonarlite.log"))));
        _log = _loggerFactory.CreateLogger("App");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };

        var sonarDirectory = @"C:\Program Files\SteelSeries\GG\apps\sonar";
        if (!SonarProcess.IsInstalled(sonarDirectory))
        {
            System.Windows.MessageBox.Show("No se encuentra SteelSeries Sonar en\n" + sonarDirectory,
                "Sonar Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // A GG update re-adds its autostart entry; park it again so both don't start at login.
        if (Autostart.IsEnabled && Autostart.IsGgAutostartActive)
        {
            _log.LogInformation("GG autostart entry reappeared, parking it again");
            Autostart.Enable();
        }

        var settings = new SettingsStore(Path.Combine(DataDirectory, "settings.json"));
        _vm = new MainViewModel(Dispatcher, _loggerFactory.CreateLogger("UI"), settings);
        _tray = new TrayIcon(OpenWindow, RestartSonarAsync, SwitchToGgAsync, ExitAsync);
        SessionEnding += async (_, _) => await ExitAsync();

        if (!e.Args.Contains("--minimized"))
            OpenWindow();

        try
        {
            _gg = new GgHost(0, _loggerFactory, Path.Combine(DataDirectory, "sonar.json"));
            await _gg.StartAsync();
            _sonar = new SonarProcess(_gg, _loggerFactory, logDirectory) { SonarDirectory = sonarDirectory };
            _sonar.StateChanged += state => Dispatcher.InvokeAsync(() => OnSonarStateChanged(state));
            await _sonar.StartAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup failed");
            _vm.Status = "No se pudo iniciar Sonar: " + ex.Message;
        }
    }

    private void OpenWindow()
    {
        if (_exiting)
            return;
        if (_window is null)
        {
            _window = new MainWindow(_vm!);
            _window.Closed += OnWindowClosed;
            _vm!.SetVisible(true);
        }
        _window.ShowAndActivate();
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        _vm!.SetVisible(false);
        // Give the UI a moment to tear down, then hand the memory back to Windows.
        await Task.Delay(500);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    private void OnSonarStateChanged(SonarState state)
    {
        _log!.LogInformation("Sonar state: {State}", state);
        switch (state)
        {
            case SonarState.Running:
                _vm!.Attach(new SonarClient(_sonar!.Address!));
                break;
            case SonarState.Starting:
            case SonarState.Restarting:
                _vm!.Detach();
                _vm.Status = state == SonarState.Starting ? "Iniciando Sonar…" : "Sonar se ha cerrado, reiniciando…";
                break;
            case SonarState.Failed:
                _vm!.Detach();
                _vm.Status = "Sonar no arranca. Revisa los logs en " + Path.Combine(DataDirectory, "logs");
                break;
            case SonarState.Stopped:
                _vm!.Detach();
                _vm.Status = "Sonar detenido";
                break;
        }
        _tray!.Tooltip = "Sonar Lite — " + _vm!.Status;
    }

    private async Task RestartSonarAsync()
    {
        if (_sonar is null || _exiting)
            return;
        try
        {
            await _sonar.RestartAsync();
        }
        catch (Exception ex)
        {
            _log!.LogError(ex, "Restart failed");
        }
    }

    /// <summary>Hands over to the official app (e.g. for firmware updates): stop our Sonar, launch GG, exit.</summary>
    private async Task SwitchToGgAsync()
    {
        var command = Autostart.GetGgCommand()
            ?? "\"C:\\Program Files\\SteelSeries\\GG\\SteelSeriesGGEZ.exe\" -dataPath=\"C:\\ProgramData\\SteelSeries\\GG\" -dbEnv=production -auto=true";
        await ExitAsync(shutdownApp: false);
        try
        {
            var (exe, args) = SplitCommand(command);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not start SteelSeries GG");
        }
        Shutdown();
    }

    private static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return (command[1..end], command[(end + 1)..].Trim());
        }
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }

    private Task ExitAsync() => ExitAsync(shutdownApp: true);

    private async Task ExitAsync(bool shutdownApp)
    {
        if (_exiting)
            return;
        _exiting = true;
        _log?.LogInformation("Exiting");
        _vm?.Detach();
        _tray?.Dispose();
        _window?.Close();
        try
        {
            if (_sonar != null)
                await _sonar.StopAsync();
            if (_gg != null)
                await _gg.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error while stopping");
        }
        _singleInstance?.ReleaseMutex();
        if (shutdownApp)
        {
            _loggerFactory?.Dispose();
            Shutdown();
        }
    }
}
