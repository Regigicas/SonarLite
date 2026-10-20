using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SonarLite.Core;

/// <summary>Client for the REST API and event websocket of SteelSeriesSonar.exe (see docs/sonar-api.md).</summary>
public sealed class SonarClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Uri _socketUri;

    public SonarClient(string baseAddress)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };
        _socketUri = new Uri(baseAddress.Replace("http://", "ws://") + "/sock");
    }

    /// <summary>Raised from a background thread for every event Sonar pushes.</summary>
    public event Action<SonarEvent>? EventReceived;

    // --- Volume -----------------------------------------------------------------

    public async Task<VolumeSettings> GetVolumesAsync(CancellationToken ct = default)
    {
        var root = await _http.GetFromJsonAsync<JsonElement>("volumeSettings/classic", Json, ct);
        var devices = new Dictionary<string, VolumeState>();
        foreach (var device in root.GetProperty("devices").EnumerateObject())
            devices[device.Name] = ReadVolume(device.Value);
        return new VolumeSettings { Master = ReadVolume(root.GetProperty("masters")), Devices = devices };

        static VolumeState ReadVolume(JsonElement e)
        {
            var classic = e.GetProperty("classic");
            return new VolumeState(classic.GetProperty("volume").GetDouble(), classic.GetProperty("muted").GetBoolean());
        }
    }

    /// <param name="role">"master" or a volume role (game, chatRender, chatCapture, media, aux).</param>
    public Task SetVolumeAsync(string role, double volume, CancellationToken ct = default) =>
        PutAsync($"volumeSettings/classic/{VolumeRoleSegment(role)}/Volume/{Format(Math.Clamp(volume, 0, 1))}", ct);

    public Task SetMuteAsync(string role, bool muted, CancellationToken ct = default) =>
        PutAsync($"volumeSettings/classic/{VolumeRoleSegment(role)}/Mute/{(muted ? "true" : "false")}", ct);

    // --- ChatMix ----------------------------------------------------------------

    public async Task<ChatMix> GetChatMixAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<ChatMix>("v1/chatMix", Json, ct))!;

    /// <param name="balance">-1 = all game, 1 = all chat.</param>
    public Task SetChatMixAsync(double balance, CancellationToken ct = default) =>
        PutAsync($"v1/chatMix?balance={Format(Math.Clamp(balance, -1, 1))}", ct);

    // --- Devices and per-channel output -----------------------------------------

    public async Task<IReadOnlyList<AudioDevice>> GetAudioDevicesAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<AudioDevice>>("audioDevices", Json, ct))!;

    public async Task<IReadOnlyList<ClassicRedirection>> GetRedirectionsAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<ClassicRedirection>>("classicRedirections", Json, ct))!;

    /// <param name="redirectionId">game, chat, media, aux or mic.</param>
    public Task SetRedirectionDeviceAsync(string redirectionId, string deviceId, CancellationToken ct = default) =>
        PutAsync($"classicRedirections/{redirectionId}/deviceId/{Uri.EscapeDataString(deviceId)}", ct);

    /// <summary>
    /// Shows or hides a channel's virtual endpoint in Windows. Sonar remembers it for media and
    /// aux (the only ones its UI lets you turn off); a disabled channel is missing from
    /// <see cref="GetAudioDevicesAsync"/> and <see cref="GetRoutingAsync"/>.
    /// </summary>
    /// <param name="role">Device role: media or aux.</param>
    public Task SetChannelEnabledAsync(string role, bool enabled, CancellationToken ct = default) =>
        PutAsync($"AudioInitialization/{role}/isEnabled/{(enabled ? "true" : "false")}", ct);

    // --- Presets ----------------------------------------------------------------

    /// <param name="vad">game, chatRender, chatCapture, media or aux.</param>
    public async Task<IReadOnlyList<ConfigSummary>> GetConfigsAsync(string vad, CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<ConfigSummary>>($"configs?vad={vad}", Json, ct))!;

    public async Task<IReadOnlyList<ConfigSummary>> GetSelectedConfigsAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<ConfigSummary>>("configs/selected", Json, ct))!;

    public Task SelectConfigAsync(Guid configId, CancellationToken ct = default) =>
        PutAsync($"configs/{configId}/select", ct);

    // --- Per-application routing ------------------------------------------------

    public async Task<IReadOnlyList<DeviceSessions>> GetRoutingAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<List<DeviceSessions>>("AudioDeviceRouting", Json, ct))!;

    /// <param name="deviceId">Endpoint id to send the process to (normally one of Sonar's virtual devices).</param>
    public Task RouteProcessAsync(string dataFlow, string deviceId, uint processId, CancellationToken ct = default) =>
        PutAsync($"AudioDeviceRouting/{dataFlow}/{Uri.EscapeDataString(deviceId)}/{processId}", ct);

    // --- Events -----------------------------------------------------------------

    /// <summary>Listens to Sonar's /sock websocket until cancelled, reconnecting as needed.</summary>
    public async Task RunEventLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(_socketUri, ct);
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, ct);
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    var ev = JsonSerializer.Deserialize<SonarEvent>(ms.ToArray(), Json);
                    if (ev != null)
                        EventReceived?.Invoke(ev);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or JsonException)
            {
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task PutAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PutAsync(path, new StringContent("", Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }

    private static string VolumeRoleSegment(string role) => role == "master" ? "Master" : role;

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
