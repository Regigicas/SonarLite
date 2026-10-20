using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SonarLite.Core;

public enum SonarState
{
    Stopped,
    Starting,
    Running,
    Restarting,
    Failed,
}

/// <summary>
/// Launches the official SteelSeriesSonar.exe against our <see cref="GgHost"/>, restarts it
/// if it dies, and shuts it down cleanly.
/// </summary>
public sealed class SonarProcess
{
    // GGEZ supervises the rest of the official stack, so it is stopped first or it respawns them.
    private static readonly string[] OfficialGgProcesses =
        ["SteelSeriesGGEZ", "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesPrism", "SteelSeriesSonar"];

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);
    private const int MaxRestartsInWindow = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(2);

    private readonly GgHost _gg;
    private readonly ILogger _log;
    private readonly string _logDirectory;
    private readonly List<DateTime> _recentRestarts = [];
    private Process? _process;
    private StreamWriter? _sonarLog;
    private bool _stopping;

    public SonarProcess(GgHost gg, ILoggerFactory loggerFactory, string logDirectory)
    {
        _gg = gg;
        _log = loggerFactory.CreateLogger("SonarProcess");
        _logDirectory = logDirectory;
    }

    public string SonarDirectory { get; init; } = @"C:\Program Files\SteelSeries\GG\apps\sonar";
    public string SonarDataDirectory { get; init; } = @"C:\ProgramData\SteelSeries\GG\apps\sonar";

    public SonarState State { get; private set; } = SonarState.Stopped;
    public string? Address => _gg.SonarReady ? _gg.SonarAddress : null;

    /// <summary>Raised on every state change; Address is set when State is Running.</summary>
    public event Action<SonarState>? StateChanged;

    public static bool IsInstalled(string sonarDirectory) =>
        File.Exists(Path.Combine(sonarDirectory, "SteelSeriesSonar.exe"));

    public static void StopOfficialGg()
    {
        foreach (var name in OfficialGgProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }
    }

    public async Task StartAsync()
    {
        _stopping = false;
        SetState(SonarState.Starting);
        StopOfficialGg();
        await LaunchAndWaitReadyAsync();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        _stopping = true;
        var process = _process;
        if (process is { HasExited: false })
        {
            _log.LogInformation("Requesting Sonar shutdown");
            await _gg.RequestSonarShutdownAsync();
            using var cts = new CancellationTokenSource(ShutdownTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Sonar did not exit in time, killing it");
                process.Kill();
            }
        }
        _gg.ResetSonar();
        SetState(SonarState.Stopped);
    }

    private async Task LaunchAndWaitReadyAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => ready.TrySetResult();
        _gg.ResetSonar();
        _gg.SonarReadyChanged += OnReady;
        try
        {
            _process = Launch();
            var exited = _process.WaitForExitAsync();
            var timeout = Task.Delay(ReadyTimeout);
            var first = await Task.WhenAny(ready.Task, exited, timeout);
            if (first == ready.Task)
            {
                _log.LogInformation("Sonar ready at {Address}", _gg.SonarAddress);
                SetState(SonarState.Running);
                return;
            }
            string reason = first == exited ? $"exited with code {_process.ExitCode}" : "did not become ready in time";
            _log.LogError("Sonar {Reason}", reason);
            if (!_process.HasExited)
                _process.Kill();
            SetState(SonarState.Failed);
        }
        finally
        {
            _gg.SonarReadyChanged -= OnReady;
        }
    }

    private Process Launch()
    {
        var exe = Path.Combine(SonarDirectory, "SteelSeriesSonar.exe");
        var psi = new ProcessStartInfo(exe)
        {
            // Sonar resolves driver\apoDriverPackage and db-migrations relative to the working directory.
            WorkingDirectory = SonarDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Environment normally provided by GGEZ (see ApplicationConfigurationService).
        // Plain http/ws: Sonar only validates GG's certificate on https.
        psi.Environment["GG_SERVER_ADDRESS"] = _gg.BaseAddress;
        psi.Environment["GG_WS_ENDPOINT"] = _gg.WebSocketEndpoint;
        psi.Environment["APP_DATA_PATH"] = SonarDataDirectory;
        psi.Environment["APP_INSTALL_PATH"] = SonarDirectory;
        psi.Environment["ENVIRONMENT"] = "production";
        psi.Environment["GG_VERSION"] = GetGgVersion();

        Directory.CreateDirectory(_logDirectory);
        _sonarLog?.Dispose();
        _sonarLog = new StreamWriter(Path.Combine(_logDirectory, "sonar.log"), append: false) { AutoFlush = true };
        var sonarLog = _sonarLog;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Both pipes must be drained continuously or Sonar blocks once a buffer fills.
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sonarLog) sonarLog.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sonarLog) sonarLog.WriteLine(e.Data); };
        process.Exited += OnProcessExited;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _log.LogInformation("Started SteelSeriesSonar.exe (pid {Pid})", process.Id);
        return process;
    }

    private async void OnProcessExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        if (_stopping || process != _process || State != SonarState.Running)
            return;

        _log.LogWarning("Sonar exited unexpectedly with code {Code}", process.ExitCode);
        _recentRestarts.RemoveAll(t => DateTime.UtcNow - t > RestartWindow);
        if (_recentRestarts.Count >= MaxRestartsInWindow)
        {
            _log.LogError("Sonar keeps crashing, giving up after {Count} restarts", _recentRestarts.Count);
            _gg.ResetSonar();
            SetState(SonarState.Failed);
            return;
        }
        _recentRestarts.Add(DateTime.UtcNow);
        SetState(SonarState.Restarting);
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!_stopping)
            await LaunchAndWaitReadyAsync();
    }

    private string GetGgVersion()
    {
        var ggExe = Path.GetFullPath(Path.Combine(SonarDirectory, @"..\..\SteelSeriesGG.exe"));
        return File.Exists(ggExe) ? FileVersionInfo.GetVersionInfo(ggExe).ProductVersion ?? "" : "";
    }

    private void SetState(SonarState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
