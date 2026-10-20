using System.Text.Json.Serialization;

namespace SonarLite.Core;

// Only the fields the client uses; Sonar sends camelCase JSON with string enums.

public sealed record VolumeState(double Volume, bool Muted);

public sealed class VolumeSettings
{
    public required VolumeState Master { get; init; }

    /// <summary>Keyed by volume role: game, chatRender, chatCapture, media, aux.</summary>
    public required IReadOnlyDictionary<string, VolumeState> Devices { get; init; }
}

public sealed record ChatMix(double Balance, string State)
{
    public bool IsEnabled => State == "enabled";
}

public sealed record AudioDevice(
    string Id,
    string FriendlyName,
    string DataFlow,
    string Role,
    string State,
    bool IsVad);

public sealed record ClassicRedirection(string Id, string? DeviceId, bool IsRunning);

public sealed record ConfigSummary(
    Guid Id,
    string Name,
    string VirtualAudioDevice,
    bool IsPreset,
    bool IsFavorite);

public sealed record AudioSession(
    string ProcessName,
    uint ProcessId,
    string DisplayName,
    string State,
    bool IsSystemSound);

public sealed record DeviceSessions(
    string DeviceId,
    string Role,
    string DataFlow,
    IReadOnlyList<AudioSession> AudioSessions);

/// <summary>Event pushed by Sonar on its /sock websocket.</summary>
public sealed record SonarEvent(
    [property: JsonPropertyName("event")] string Name,
    [property: JsonPropertyName("data")] System.Text.Json.JsonElement Data);
