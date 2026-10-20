using System.IO;
using System.Text.Json;

namespace SonarLite;

internal sealed class AppSettings
{
    public bool ShowDecibels { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON next to the logs.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Write-then-rename so a crash mid-write never leaves a truncated file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return new AppSettings();
    }
}
