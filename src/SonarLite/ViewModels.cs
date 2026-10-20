using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SonarLite.Core;

namespace SonarLite;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Sends only the latest value of a fast-changing input (e.g. a slider being dragged),
/// never more than one request in flight. Runs on the UI thread.
/// </summary>
internal sealed class LatestValueSender<T>(Func<T, Task> send, Action<Exception> onError)
{
    private T? _latest;
    private bool _running;

    public void Post(T value)
    {
        _latest = value;
        if (!_running)
            _ = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            T sent;
            do
            {
                sent = _latest!;
                await send(sent);
                await Task.Delay(30);
            } while (!EqualityComparer<T>.Default.Equals(sent, _latest));
        }
        catch (Exception e)
        {
            onError(e);
        }
        finally
        {
            _running = false;
        }
    }
}

public sealed class ChannelViewModel : ObservableObject
{
    // Ignore server values for a moment after a local change, so a refresh doesn't
    // yank the slider back while the user is still dragging it.
    private static readonly TimeSpan LocalChangeGrace = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ChannelToggleGrace = TimeSpan.FromSeconds(4);

    private readonly MainViewModel _owner;
    private readonly LatestValueSender<double> _volumeSender;
    private bool _fromServer;
    private DateTime _lastLocalChange;
    private double _volume;
    private bool _muted;
    private AudioDevice? _selectedDevice;
    private ConfigSummary? _selectedPreset;
    private bool _isEnabled = true;
    private DateTime _lastEnabledChange;

    public ChannelViewModel(MainViewModel owner, string volumeRole, string displayName,
        string? redirectionId = null, string? vad = null, string dataFlow = "render")
    {
        _owner = owner;
        VolumeRole = volumeRole;
        DisplayName = displayName;
        RedirectionId = redirectionId;
        Vad = vad;
        DataFlow = dataFlow;
        _volumeSender = new LatestValueSender<double>(
            v => owner.Client!.SetVolumeAsync(VolumeRole, v / 100.0), owner.ReportError);
    }

    public string VolumeRole { get; }
    public string DisplayName { get; }
    /// <summary>Id for classicRedirections (physical output); null for master.</summary>
    public string? RedirectionId { get; }
    /// <summary>Virtual audio device for presets; null for master.</summary>
    public string? Vad { get; }
    public string DataFlow { get; }
    public bool IsMaster => RedirectionId is null;
    /// <summary>Sonar lets only these be turned off; game, chat and mic are always on.</summary>
    public bool CanDisable => VolumeRole is "media" or "aux";

    public ObservableCollection<AudioDevice> Devices { get; } = [];
    public ObservableCollection<ConfigSummary> Presets { get; } = [];

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Round(value)))
                return;
            OnPropertyChanged(nameof(VolumeText));
            if (_fromServer || _owner.Client is null)
                return;
            _lastLocalChange = DateTime.UtcNow;
            _volumeSender.Post(_volume);
        }
    }

    public string VolumeText
    {
        get
        {
            if (!_owner.ShowDecibels)
                return $"{Volume:0} %";
            double db = VolumeCurve.ToDecibels(Volume / 100.0);
            return double.IsNegativeInfinity(db) ? "−∞ dB" : $"{db:0.0} dB";
        }
    }

    internal void RefreshVolumeText() => OnPropertyChanged(nameof(VolumeText));

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value) || _fromServer || _owner.Client is null)
                return;
            _lastLocalChange = DateTime.UtcNow;
            _owner.Run(c => c.SetMuteAsync(VolumeRole, value));
        }
    }

    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value) || _fromServer || value is null || _owner.Client is null)
                return;
            _owner.Run(c => c.SetRedirectionDeviceAsync(RedirectionId!, value.Id));
        }
    }

    public ConfigSummary? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || _fromServer || value is null || _owner.Client is null)
                return;
            _owner.Run(c => c.SelectConfigAsync(value.Id));
        }
    }

    /// <summary>Channel's virtual endpoint exists in Windows; disabled channels are hidden.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value) || _fromServer || _owner.Client is null)
                return;
            _lastEnabledChange = DateTime.UtcNow;
            _owner.SetChannelEnabled(this, value);
        }
    }

    internal void ApplyEnabled(bool enabled)
    {
        // Sonar disables endpoints in the background: give it time before trusting a read.
        if (DateTime.UtcNow - _lastEnabledChange < ChannelToggleGrace)
            return;
        FromServer(() => IsEnabled = enabled);
    }

    internal void ApplyVolume(VolumeState state)
    {
        if (DateTime.UtcNow - _lastLocalChange < LocalChangeGrace)
            return;
        FromServer(() =>
        {
            Volume = Math.Round(state.Volume * 100);
            Muted = state.Muted;
        });
    }

    internal void ApplyDevices(IEnumerable<AudioDevice> devices, string? selectedId)
    {
        FromServer(() =>
        {
            Sync(Devices, devices.ToList(), d => d.Id);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId);
        });
    }

    internal void ApplyPresets(IReadOnlyList<ConfigSummary>? presets, Guid? selectedId)
    {
        FromServer(() =>
        {
            if (presets != null)
                Sync(Presets, presets.ToList(), p => p.Id);
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == selectedId);
        });
    }

    private void FromServer(Action apply)
    {
        _fromServer = true;
        try { apply(); }
        finally { _fromServer = false; }
    }

    internal static void Sync<T, TKey>(ObservableCollection<T> target, IList<T> source, Func<T, TKey> key)
    {
        if (target.Select(key).SequenceEqual(source.Select(key)))
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (!Equals(target[i], source[i]))
                    target[i] = source[i];
            }
            return;
        }
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<string, IReadOnlyList<ConfigSummary>> _presetCache = [];
    private CancellationTokenSource? _eventsCts;
    private bool _isVisible;
    private bool _refreshQueued;
    private string _status = "Iniciando Sonar…";
    private bool _isConnected;
    private double _chatMix;
    private bool _chatMixEnabled;
    private bool _chatMixFromServer;
    private readonly LatestValueSender<double> _chatMixSender;
    private IReadOnlyList<ChannelOption> _channelOptions = [];

    internal MainViewModel(Dispatcher dispatcher, ILogger log, SettingsStore settings)
    {
        _dispatcher = dispatcher;
        _log = log;
        _settings = settings;
        Master = new ChannelViewModel(this, "master", "Master");
        Channels =
        [
            new ChannelViewModel(this, "game", "Juego", "game", "game"),
            new ChannelViewModel(this, "chatRender", "Chat", "chat", "chatRender"),
            new ChannelViewModel(this, "media", "Media", "media", "media"),
            new ChannelViewModel(this, "aux", "Aux", "aux", "aux"),
            new ChannelViewModel(this, "chatCapture", "Micrófono", "mic", "chatCapture", "capture"),
        ];
        OptionalChannels = Channels.Where(c => c.CanDisable).ToList();
        _chatMixSender =new LatestValueSender<double>(v => Client!.SetChatMixAsync(v / 100.0), ReportError);
        _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _pollTimer.Stop();
        InitializeApps();
    }

    internal SonarClient? Client { get; private set; }

    public ChannelViewModel Master { get; }
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    /// <summary>Channels the user can turn off (media, aux).</summary>
    public IReadOnlyList<ChannelViewModel> OptionalChannels { get; }

    public IReadOnlyList<ChannelOption> ChannelOptions
    {
        get => _channelOptions;
        private set => SetProperty(ref _channelOptions, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Show channel levels as the real attenuation in dB instead of a percentage.</summary>
    public bool ShowDecibels
    {
        get => _settings.Current.ShowDecibels;
        set
        {
            if (_settings.Current.ShowDecibels == value)
                return;
            _settings.Current.ShowDecibels = value;
            _settings.Save();
            OnPropertyChanged(nameof(ShowDecibels));
            Master.RefreshVolumeText();
            foreach (var channel in Channels)
                channel.RefreshVolumeText();
            RefreshChatMixText();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>-100 = all game, 100 = all chat.</summary>
    public double ChatMix
    {
        get => _chatMix;
        set
        {
            if (!SetProperty(ref _chatMix, Math.Round(value)))
                return;
            RefreshChatMixText();
            if (_chatMixFromServer || Client is null)
                return;
            _chatMixSender.Post(_chatMix);
        }
    }

    public string ChatMixGameText => $"Juego {FormatGain(ChatMixGains(ChatMix / 100.0).Game)}";
    public string ChatMixChatText => $"Chat {FormatGain(ChatMixGains(ChatMix / 100.0).Chat)}";

    private void RefreshChatMixText()
    {
        OnPropertyChanged(nameof(ChatMixGameText));
        OnPropertyChanged(nameof(ChatMixChatText));
    }

    /// <summary>
    /// Same mapping as Sonar's ChatMixService: the balance is rescaled non-linearly and only
    /// the opposite side is attenuated (the side the slider moves towards stays at 100 %).
    /// </summary>
    private static (double Game, double Chat) ChatMixGains(double balance)
    {
        double magnitude = Math.Abs(balance);
        if (magnitude < 1e-6)
            return (1, 1);
        double scaled = Math.Sign(balance) * 2 * magnitude / (magnitude + 1);
        return (Math.Clamp(1 - scaled, 0, 1), Math.Clamp(1 + scaled, 0, 1));
    }

    /// <summary>Linear gain as % or, when showing dB, as 20·log10.</summary>
    private string FormatGain(double gain)
    {
        if (!ShowDecibels)
            return $"{gain * 100:0} %";
        return gain <= 0 ? "−∞ dB" : $"{20 * Math.Log10(gain):0.0} dB";
    }

    public bool ChatMixEnabled
    {
        get => _chatMixEnabled;
        private set => SetProperty(ref _chatMixEnabled, value);
    }

    public void Attach(SonarClient client)
    {
        Detach();
        Client = client;
        _presetCache.Clear();
        IsConnected = true;
        Status = "Conectado";
        _eventsCts = new CancellationTokenSource();
        client.EventReceived += OnSonarEvent;
        _ = client.RunEventLoopAsync(_eventsCts.Token);
        _ = RefreshAllAsync();
        UpdatePolling();
    }

    public void Detach()
    {
        _eventsCts?.Cancel();
        _eventsCts = null;
        if (Client != null)
        {
            Client.EventReceived -= OnSonarEvent;
            Client.Dispose();
            Client = null;
        }
        IsConnected = false;
        UpdatePolling();
    }

    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        UpdatePolling();
        if (visible && Client != null)
            _ = RefreshAllAsync();
    }

    internal void Run(Func<SonarClient, Task> action)
    {
        var client = Client;
        if (client is null)
            return;
        _ = RunAsync();

        async Task RunAsync()
        {
            try { await action(client); }
            catch (Exception e) { ReportError(e); }
        }
    }

    internal void SetChannelEnabled(ChannelViewModel channel, bool enabled)
    {
        Run(async c =>
        {
            await c.SetChannelEnabledAsync(channel.VolumeRole, enabled);
            // The endpoint appears/disappears asynchronously; then the app list follows.
            await Task.Delay(1000);
            await RefreshAllAsync();
        });
    }

    internal void ReportError(Exception e)
    {
        _log.LogWarning(e, "Sonar API call failed");
        Status = "Error al hablar con Sonar: " + e.Message;
    }

    public async Task RefreshAllAsync()
    {
        var client = Client;
        if (client is null)
            return;
        try
        {
            var volumes = client.GetVolumesAsync();
            var chatMix = client.GetChatMixAsync();
            var devices = client.GetAudioDevicesAsync();
            var redirections = client.GetRedirectionsAsync();
            var selected = client.GetSelectedConfigsAsync();
            await Task.WhenAll(volumes, chatMix, devices, redirections, selected);

            ApplyVolumes(volumes.Result);
            ApplyChatMix(chatMix.Result);
            ApplyDevices(devices.Result, redirections.Result);
            await ApplyPresetsAsync(client, selected.Result);
            await RefreshRoutingAsync();
            Status = "Conectado";
        }
        catch (Exception e)
        {
            ReportError(e);
        }
    }

    private async Task PollAsync()
    {
        var client = Client;
        if (client is null)
            return;
        try
        {
            var volumes = client.GetVolumesAsync();
            var chatMix = client.GetChatMixAsync();
            await Task.WhenAll(volumes, chatMix);
            ApplyVolumes(volumes.Result);
            ApplyChatMix(chatMix.Result);
            await RefreshRoutingAsync();
        }
        catch (Exception e)
        {
            ReportError(e);
        }
    }

    private void UpdatePolling()
    {
        if (_isVisible && Client != null)
            _pollTimer.Start();
        else
            _pollTimer.Stop();
    }

    private void OnSonarEvent(SonarEvent ev)
    {
        if (ev.Name is "EVENT_SONAR_CHATMIX_DATA" or "SONAR_EVENT_CHATMIX_DATA")
        {
            var mix = ev.Data.Deserialize<ChatMix>(WebJson);
            if (mix != null)
                _dispatcher.InvokeAsync(() => ApplyChatMix(mix));
            return;
        }
        if (ev.Name == "SONAR_EVENT_AUDIO_SESSION_OPENED_DATA")
        {
            // Routing rules must react right away (even with the window closed): this is
            // the moment an app can still be moved before it settles on a device.
            var opened = ev.Data.Deserialize<DeviceSessions>(WebJson);
            if (opened != null)
                _dispatcher.InvokeAsync(() => Router.OnSessionOpenedAsync(opened));
        }
        // Anything else (volumes, routing, presets, devices) triggers one coalesced refresh.
        if (!_isVisible || _refreshQueued)
            return;
        _refreshQueued = true;
        _dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(200);
            _refreshQueued = false;
            await RefreshAllAsync();
        });
    }

    private void ApplyVolumes(VolumeSettings volumes)
    {
        Master.ApplyVolume(volumes.Master);
        foreach (var channel in Channels)
        {
            if (volumes.Devices.TryGetValue(channel.VolumeRole, out var state))
                channel.ApplyVolume(state);
        }
    }

    private void ApplyChatMix(ChatMix mix)
    {
        ChatMixEnabled = mix.IsEnabled;
        _chatMixFromServer = true;
        try { ChatMix = Math.Round(mix.Balance * 100); }
        finally { _chatMixFromServer = false; }
    }

    /// <summary>A disabled channel's virtual endpoint is not among Sonar's active devices.</summary>
    internal void ApplyChannelStates(IReadOnlyList<AudioDevice> devices)
    {
        if (!devices.Any(d => d.IsVad))
            return;
        foreach (var channel in OptionalChannels)
            channel.ApplyEnabled(devices.Any(d => d.IsVad && d.State == "active" && d.Role == channel.VolumeRole));
    }

    private void ApplyDevices(IReadOnlyList<AudioDevice> devices, IReadOnlyList<ClassicRedirection> redirections)
    {
        ApplyChannelStates(devices);
        foreach (var channel in Channels)
        {
            var candidates = devices
                .Where(d => !d.IsVad && d.State == "active" && d.DataFlow == channel.DataFlow)
                .OrderBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase);
            var selectedId = redirections.FirstOrDefault(r => r.Id == channel.RedirectionId)?.DeviceId;
            channel.ApplyDevices(candidates, selectedId);
        }
    }

    private async Task ApplyPresetsAsync(SonarClient client, IReadOnlyList<ConfigSummary> selected)
    {
        foreach (var channel in Channels)
        {
            if (!_presetCache.TryGetValue(channel.Vad!, out var presets))
            {
                presets = (await client.GetConfigsAsync(channel.Vad!))
                    .OrderByDescending(c => c.IsFavorite)
                    .ThenBy(c => c.IsPreset)
                    .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                _presetCache[channel.Vad!] = presets;
                channel.ApplyPresets(presets, null);
            }
            var selectedId = selected.FirstOrDefault(c => c.VirtualAudioDevice == channel.Vad)?.Id;
            channel.ApplyPresets(null, selectedId);
        }
    }

}
