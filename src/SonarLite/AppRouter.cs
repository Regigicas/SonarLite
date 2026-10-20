using Microsoft.Extensions.Logging;
using SonarLite.Core;

namespace SonarLite;

/// <summary>
/// Applies <see cref="RoutingRule"/>s: moves an app to its channel as soon as it opens an
/// audio session.
/// </summary>
/// <remarks>
/// Windows only accepts a per-app endpoint (SetPersistedDefaultAudioEndpoint, what Sonar's
/// routing API calls) once the process has initialised audio; earlier calls are silently
/// ignored. The session-opened event is therefore the earliest useful moment, and it is the
/// short window in which apps that otherwise "resist" a channel change still pick it up.
/// Windows then remembers the choice per executable path, so a one-shot rule is enough for
/// most apps; "always" rules cover apps whose path changes on update (e.g. Discord's
/// app-x.y.z folder) or that reset their device.
/// </remarks>
internal sealed class AppRouter
{
    private readonly SettingsStore _settings;
    private readonly ILogger _log;
    private readonly Func<SonarClient?> _client;
    private readonly Func<string, string?> _deviceIdForRole;
    // "Always" rules route each process once; a resisting app is not hammered on every refresh.
    private readonly HashSet<uint> _routedPids = [];

    public AppRouter(SettingsStore settings, ILogger log, Func<SonarClient?> client, Func<string, string?> deviceIdForRole)
    {
        _settings = settings;
        _log = log;
        _client = client;
        _deviceIdForRole = deviceIdForRole;
    }

    /// <summary>Raised when a rule is added, changed, removed or consumed.</summary>
    public event Action? RulesChanged;

    /// <summary>Raised with (process name, channel role) after a rule moved an app.</summary>
    public event Action<string, string>? RuleApplied;

    private List<RoutingRule> Rules => _settings.Current.RoutingRules;

    public RoutingRule? Find(string processName) =>
        Rules.FirstOrDefault(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    public void SetRule(string processName, string channelRole, bool always)
    {
        Rules.RemoveAll(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        Rules.Add(new RoutingRule(processName, channelRole, always));
        _routedPids.Clear();
        _log.LogInformation("Rule set: {Process} -> {Role} ({Mode})", processName, channelRole, always ? "always" : "next launch");
        Save();
    }

    public void RemoveRule(string processName)
    {
        if (Rules.RemoveAll(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _log.LogInformation("Rule removed: {Process}", processName);
            Save();
        }
    }

    /// <summary>Handles Sonar's SONAR_EVENT_AUDIO_SESSION_OPENED_DATA.</summary>
    public Task OnSessionOpenedAsync(DeviceSessions opened) => EvaluateAsync([opened]);

    /// <summary>Checks a routing snapshot (also catches sessions whose event was missed).</summary>
    public async Task EvaluateAsync(IReadOnlyList<DeviceSessions> routing)
    {
        if (Rules.Count == 0)
            return;
        foreach (var device in routing.Where(d => d.DataFlow == "render"))
        {
            foreach (var session in device.AudioSessions)
            {
                if (session.IsSystemSound || session.ProcessId == 0 || session.State == "expired")
                    continue;
                var rule = Find(session.ProcessName);
                if (rule is null || (rule.Always && _routedPids.Contains(session.ProcessId)))
                    continue;
                await ApplyAsync(rule, session, device.DeviceId);
            }
        }
    }

    private async Task ApplyAsync(RoutingRule rule, AudioSession session, string currentDeviceId)
    {
        var client = _client();
        var target = _deviceIdForRole(rule.ChannelRole);
        if (client is null || target is null)
            return;

        if (currentDeviceId != target)
        {
            try
            {
                await client.RouteProcessAsync("render", target, session.ProcessId);
                _log.LogInformation("Rule applied: {Process} (pid {Pid}) -> {Role}", session.ProcessName, session.ProcessId, rule.ChannelRole);
                RuleApplied?.Invoke(session.ProcessName, rule.ChannelRole);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Could not route {Process} (pid {Pid})", session.ProcessName, session.ProcessId);
                return;
            }
        }
        _routedPids.Add(session.ProcessId);
        if (!rule.Always)
        {
            Rules.Remove(rule);
            Save();
        }
    }

    private void Save()
    {
        _settings.Save();
        RulesChanged?.Invoke();
    }
}
