using System.Text.Json.Nodes;

namespace SonarLite.Core;

/// <summary>
/// A full Sonar config (preset) as Sonar sends it. Kept as raw JSON so that updates send back
/// every field, including ones this client doesn't know: <c>PUT /configs</c> replaces the whole
/// config, and the effect settings live in a free-form <c>data</c> object (see docs/sonar-api.md).
/// </summary>
public sealed class SonarConfig
{
    public SonarConfig(JsonObject json)
    {
        Json = json;
        Data = json["data"] as JsonObject ?? [];
        json["data"] = Data;
    }

    public JsonObject Json { get; }

    /// <summary>Effect settings (EQ, boosts, noise gate, …), addressed with dotted paths.</summary>
    public JsonObject Data { get; private set; }

    public Guid Id => Json["id"]?.GetValue<Guid>() ?? Guid.Empty;

    public string Name
    {
        get => Json["name"]?.GetValue<string>() ?? "";
        set => Json["name"] = value;
    }

    public string VirtualAudioDevice => Json["virtualAudioDevice"]?.GetValue<string>() ?? "";

    /// <summary>Ships with Sonar: it can be edited and reset, but not renamed or deleted.</summary>
    public bool IsPreset => Json["isPreset"]?.GetValue<bool>() ?? false;

    public bool IsFavorite => Json["isFavorite"]?.GetValue<bool>() ?? false;

    public ConfigSummary Summary => new(Id, Name, VirtualAudioDevice, IsPreset, IsFavorite);

    public SonarConfig Clone() => new(Json.DeepClone().AsObject());

    /// <summary>Puts back the factory values (what GG's "Restablecer" does).</summary>
    public void ResetData()
    {
        if (Json["defaultData"] is JsonObject defaults)
        {
            Data = defaults.DeepClone().AsObject();
            Json["data"] = Data;
        }
    }

    public double GetNumber(string path, double fallback = 0) =>
        Find(path) is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    public bool GetBool(string path) =>
        Find(path) is JsonValue v && v.TryGetValue(out bool b) && b;

    public string? GetString(string path) =>
        Find(path) is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public void Set(string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var node = Data;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = [];
                node[part] = child;
            }
            node = child;
        }
        node[parts[^1]] = value;
    }

    private JsonNode? Find(string path)
    {
        JsonNode? node = Data;
        foreach (var part in path.Split('.'))
            node = (node as JsonObject)?[part];
        return node;
    }
}
