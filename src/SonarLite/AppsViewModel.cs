using System.Collections.ObjectModel;
using System.Windows.Input;
using SonarLite.Core;

namespace SonarLite;

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

public sealed record ChannelOption(string Role, string Name, string DeviceId);

/// <summary>One app in the "Aplicaciones" tab, keyed by process name (an app can have several processes).</summary>
public sealed class AppViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _fromServer;
    private string _name;
    private bool _isConnected;
    private ChannelOption? _channel;
    private string _detail = "";
    private bool _isAlways;
    private bool _hasPendingRule;

    public AppViewModel(MainViewModel owner, string processName, string name)
    {
        _owner = owner;
        ProcessName = processName;
        _name = name;
        RemoveRuleCommand = new RelayCommand(() => owner.RemoveAppRule(this));
    }

    public string ProcessName { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>Has an audio session right now.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public IReadOnlyList<uint> ProcessIds { get; private set; } = [];

    /// <summary>Secondary text: device outside Sonar, pending rule, or a hint.</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>Transient message shown after a channel change the app did not follow.</summary>
    internal string? Hint { get; set; }

    /// <summary>Channel the hint is about; the hint goes away once the app gets there.</summary>
    internal string? HintTargetRole { get; set; }

    public IReadOnlyList<ChannelOption> Options => _owner.ChannelOptions;

    public ChannelOption? Channel
    {
        get => _channel;
        set
        {
            if (!SetProperty(ref _channel, value) || _fromServer || value is null)
                return;
            _owner.OnAppChannelSelected(this, value);
        }
    }

    /// <summary>Keep this app on its channel every time it starts.</summary>
    public bool IsAlways
    {
        get => _isAlways;
        set
        {
            if (!SetProperty(ref _isAlways, value) || _fromServer)
                return;
            _owner.OnAppAlwaysChanged(this, value);
        }
    }

    /// <summary>A one-shot rule waits for the app's next audio session.</summary>
    public bool HasPendingRule
    {
        get => _hasPendingRule;
        private set
        {
            if (SetProperty(ref _hasPendingRule, value))
                OnPropertyChanged(nameof(RemoveTooltip));
        }
    }

    /// <summary>The ✕ button cancels a pending change, or forgets an app that is not playing.</summary>
    public bool CanRemove => HasPendingRule || !IsConnected;

    public string RemoveTooltip => HasPendingRule ? "Cancelar el cambio pendiente" : "Quitar de la lista";

    public ICommand RemoveRuleCommand { get; }

    internal void RefreshOptions() => OnPropertyChanged(nameof(Options));

    internal void Update(bool connected, IReadOnlyList<uint> processIds, ChannelOption? channel, string detail,
        bool always, bool hasPendingRule)
    {
        _fromServer = true;
        try
        {
            IsConnected = connected;
            ProcessIds = processIds;
            Channel = channel;
            Detail = detail;
            IsAlways = always;
            HasPendingRule = hasPendingRule;
            OnPropertyChanged(nameof(CanRemove));
        }
        finally
        {
            _fromServer = false;
        }
    }
}

public sealed partial class MainViewModel
{
    private const int MaxKnownApps = 40;
    private static readonly string[] AppChannelRoles = ["game", "chatRender", "media", "aux"];

    private IReadOnlyList<DeviceSessions> _lastRouting = [];
    private IReadOnlyList<AudioDevice> _lastDevices = [];
    private string _newAppName = "";

    internal AppRouter Router { get; private set; } = null!;

    public ObservableCollection<AppViewModel> Apps { get; } = [];

    public string NewAppName
    {
        get => _newAppName;
        set => SetProperty(ref _newAppName, value);
    }

    public ICommand AddAppCommand => new RelayCommand(AddApp);

    private void InitializeApps()
    {
        Router = new AppRouter(_settings, _log, () => Client,
            role => ChannelOptions.FirstOrDefault(o => o.Role == role)?.DeviceId);
        Router.RulesChanged += RebuildApps;
        Router.RuleApplied += (processName, role) => _ = CheckAppMovedAsync(processName, role);
        // Older versions remembered Sonar's own "Unknown" streams as an app.
        if (_settings.Current.KnownApps.RemoveAll(k => IsUnresolvedProcess(k.ProcessName)) > 0)
            _settings.Save();
        RebuildApps();
    }

    /// <summary>
    /// After moving an app, checks whether it actually followed; some apps keep their stream on
    /// the old device until restarted (Windows has stored the new channel for the next launch).
    /// </summary>
    private async Task CheckAppMovedAsync(string processName, string role)
    {
        try
        {
            await Task.Delay(1500);
            await RefreshRoutingAsync();
        }
        catch (Exception e)
        {
            ReportError(e);
            return;
        }
        var app = Apps.FirstOrDefault(a => string.Equals(a.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (app is null)
            return;
        bool resisted = app.IsConnected && app.Channel?.Role != role;
        var channelName = ChannelOptions.FirstOrDefault(o => o.Role == role)?.Name ?? role;
        app.Hint = resisted ? $"No ha cambiado a {channelName}: se aplicará al reiniciar la app" : null;
        app.HintTargetRole = resisted ? role : null;
        RebuildApps();
    }

    internal async Task RefreshRoutingAsync()
    {
        var client = Client;
        if (client is null)
            return;
        var routing = await client.GetRoutingAsync();
        var devices = await client.GetAudioDevicesAsync();
        _lastRouting = routing;
        _lastDevices = devices;
        ApplyChannelStates(devices);
        UpdateChannelOptions(routing);
        RememberApps(routing);
        await Router.EvaluateAsync(routing);
        RebuildApps();
    }

    internal void OnAppChannelSelected(AppViewModel app, ChannelOption channel)
    {
        var rule = Router.Find(app.ProcessName);
        if (rule?.Always == true)
            Router.SetRule(app.ProcessName, channel.Role, always: true);

        if (!app.IsConnected)
        {
            // No audio session: Windows would ignore the change now, so wait for the next one.
            if (rule?.Always != true)
                Router.SetRule(app.ProcessName, channel.Role, always: false);
            return;
        }

        var pids = app.ProcessIds;
        Run(async c =>
        {
            foreach (var pid in pids)
                await c.RouteProcessAsync("render", channel.DeviceId, pid);
            await CheckAppMovedAsync(app.ProcessName, channel.Role);
        });
    }

    internal void OnAppAlwaysChanged(AppViewModel app, bool always)
    {
        if (!always)
        {
            Router.RemoveRule(app.ProcessName);
            return;
        }
        var role = app.Channel?.Role;
        if (role is null)
        {
            Status = $"Elige primero un canal para {app.Name}";
            RebuildApps();
            return;
        }
        Router.SetRule(app.ProcessName, role, always: true);
    }

    internal void RemoveAppRule(AppViewModel app)
    {
        if (app.HasPendingRule)
        {
            Router.RemoveRule(app.ProcessName);
            return;
        }
        if (app.IsConnected)
            return;
        // Forget a silent app; an "always" rule would bring it straight back, so drop it too.
        _settings.Current.KnownApps.RemoveAll(k => string.Equals(k.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        Router.RemoveRule(app.ProcessName);
        RebuildApps();
    }

    private void AddApp()
    {
        var name = NewAppName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length == 0)
            return;
        var known = _settings.Current.KnownApps;
        if (!known.Any(k => string.Equals(k.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
        {
            known.Add(new KnownApp(name, name, DateTime.UtcNow));
            _settings.Save();
        }
        NewAppName = "";
        RebuildApps();
    }

    private void UpdateChannelOptions(IReadOnlyList<DeviceSessions> routing)
    {
        var options = AppChannelRoles
            .Select(role => routing.FirstOrDefault(d => d.DataFlow == "render" && d.Role == role))
            .Where(d => d != null)
            .Select(d => (Device: d!, Channel: Channels.First(c => c.VolumeRole == d!.Role)))
            .Where(x => x.Channel.IsEnabled)
            .Select(x => new ChannelOption(x.Device.Role, x.Channel.DisplayName, x.Device.DeviceId))
            .ToList();
        if (options.SequenceEqual(ChannelOptions))
            return;
        ChannelOptions = options;
        foreach (var app in Apps)
            app.RefreshOptions();
    }

    /// <summary>Remembers apps that played audio so they can be configured later while silent.</summary>
    private void RememberApps(IReadOnlyList<DeviceSessions> routing)
    {
        var known = _settings.Current.KnownApps;
        bool added = false;
        foreach (var s in ActiveSessions(routing).Select(x => x.Session))
        {
            int i = known.FindIndex(k => string.Equals(k.ProcessName, s.ProcessName, StringComparison.OrdinalIgnoreCase));
            var entry = new KnownApp(s.ProcessName, DisplayNameOf(s), DateTime.UtcNow);
            if (i >= 0)
            {
                known[i] = entry;
            }
            else
            {
                known.Add(entry);
                added = true;
            }
        }
        if (!added)
            return;
        if (known.Count > MaxKnownApps)
        {
            var keep = known.OrderByDescending(k => k.LastSeenUtc).Take(MaxKnownApps).ToList();
            known.Clear();
            known.AddRange(keep);
        }
        _settings.Save();
    }

    /// <summary>Rebuilds the app list: apps with audio first, then known/ruled apps without audio.</summary>
    private void RebuildApps()
    {
        var connected = ActiveSessions(_lastRouting)
            .GroupBy(x => x.Session.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var disconnectedNames = _settings.Current.KnownApps.Select(k => (k.ProcessName, k.DisplayName))
            .Concat(_settings.Current.RoutingRules.Select(r => (r.ProcessName, DisplayName: r.ProcessName)))
            .Where(x => !connected.ContainsKey(x.ProcessName))
            .DistinctBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var desired = connected
            .Select(kv => (ProcessName: kv.Key, Name: DisplayNameOf(kv.Value[0].Session), Connected: true))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(disconnectedNames
                .Select(x => (x.ProcessName, Name: x.DisplayName, Connected: false))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

        foreach (var stale in Apps.Where(a => !desired.Any(d => string.Equals(d.ProcessName, a.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList())
            Apps.Remove(stale);

        for (int index = 0; index < desired.Count; index++)
        {
            var (processName, name, isConnected) = desired[index];
            var app = Apps.FirstOrDefault(a => string.Equals(a.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (app is null)
            {
                app = new AppViewModel(this, processName, name);
                Apps.Insert(index, app);
            }
            else
            {
                int current = Apps.IndexOf(app);
                if (current != index)
                    Apps.Move(current, index);
                app.Name = name;
            }

            var rule = Router.Find(processName);
            var ruleChannel = rule is null ? null : ChannelOptions.FirstOrDefault(o => o.Role == rule.ChannelRole);
            if (isConnected)
            {
                var sessions = connected[processName];
                // Several sessions: the active one (and on a Sonar channel) is what the user hears.
                var main = sessions.OrderByDescending(x => Rank(x.Session, x.Device)).First();
                var channel = ChannelOptions.FirstOrDefault(o => o.DeviceId == main.Device.DeviceId);
                if (app.HintTargetRole != null && channel?.Role == app.HintTargetRole)
                    app.Hint = app.HintTargetRole = null;
                string detail = app.Hint
                    ?? (channel is null
                        ? "Fuera de Sonar · " + (_lastDevices.FirstOrDefault(d => d.Id == main.Device.DeviceId)?.FriendlyName ?? "otro dispositivo")
                        : "");
                var pids = sessions.Select(x => x.Session.ProcessId).Distinct().ToList();
                app.Update(true, pids, channel, detail, rule?.Always == true, hasPendingRule: rule is { Always: false });
            }
            else
            {
                app.Hint = app.HintTargetRole = null;
                string detail = rule is { Always: false } && ruleChannel != null
                    ? $"Sin audio · pasará a {ruleChannel.Name} al abrirse"
                    : "Sin audio";
                app.Update(false, [], ruleChannel, detail, rule?.Always == true, hasPendingRule: rule is { Always: false });
            }
        }

        static int Rank(AudioSession s, DeviceSessions d) =>
            (s.State == "active" ? 2 : 0) + (AppChannelRoles.Contains(d.Role) ? 1 : 0);
    }

    private static IEnumerable<(AudioSession Session, DeviceSessions Device)> ActiveSessions(IReadOnlyList<DeviceSessions> routing) =>
        routing.Where(d => d.DataFlow == "render")
            .SelectMany(d => d.AudioSessions.Select(s => (Session: s, Device: d)))
            .Where(x => !x.Session.IsSystemSound && x.Session.ProcessId != 0 && x.Session.State != "expired"
                && !IsUnresolvedProcess(x.Session.ProcessName));

    /// <summary>
    /// Sonar reports "Unknown" when it cannot resolve the owning process. In practice these are
    /// Sonar's own streams carrying each channel to the physical device (one per redirection,
    /// owned by audiodg): not an app, and nothing that can be routed.
    /// </summary>
    internal static bool IsUnresolvedProcess(string processName) =>
        string.Equals(processName, "Unknown", StringComparison.OrdinalIgnoreCase);

    private static string DisplayNameOf(AudioSession s) =>
        string.IsNullOrWhiteSpace(s.DisplayName) ? s.ProcessName : s.DisplayName;
}
