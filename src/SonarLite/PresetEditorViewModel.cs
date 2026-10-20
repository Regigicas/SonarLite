using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using SonarLite.Core;

namespace SonarLite;

/// <summary>One editable value of a preset, bound to a dotted path inside its <c>data</c>.</summary>
public abstract class PresetSetting(PresetEditorViewModel editor, string label, Func<bool>? enabledWhen) : ObservableObject
{
    private bool _isEnabled = true;

    protected PresetEditorViewModel Editor { get; } = editor;
    public string Label { get; } = label;

    /// <summary>Greyed out while the effect it belongs to is off (same rules as GG).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    internal void RefreshEnabled() => IsEnabled = enabledWhen?.Invoke() ?? true;

    internal abstract void Load(SonarConfig config);
}

public sealed class ToggleSetting(PresetEditorViewModel editor, string label, string path,
    Func<bool>? enabledWhen = null, Action<bool>? onChanged = null) : PresetSetting(editor, label, enabledWhen)
{
    private bool _value;

    public bool Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value) || Editor.IsLoading)
                return;
            Editor.Set(path, value);
            onChanged?.Invoke(value);
            Editor.Commit();
        }
    }

    internal override void Load(SonarConfig config)
    {
        _value = config.GetBool(path);
        OnPropertyChanged(nameof(Value));
    }
}

/// <summary>
/// A slider over a numeric value. The slider works in display units (dB, %), which
/// <paramref name="toSlider"/> and <paramref name="fromSlider"/> convert from and to Sonar's value.
/// </summary>
public sealed class SliderSetting(PresetEditorViewModel editor, string label, string path,
    double minimum, double maximum, double step, string format,
    Func<double, double>? toSlider = null, Func<double, double>? fromSlider = null,
    Func<bool>? enabledWhen = null, Action<double>? onChanged = null) : PresetSetting(editor, label, enabledWhen)
{
    private double _value;

    public double Minimum => minimum;
    public double Maximum => maximum;
    public double SmallChange => step;
    public double LargeChange => step * 10;

    public double Value
    {
        get => _value;
        set
        {
            double v = Math.Clamp(Math.Round(value / step) * step, minimum, maximum);
            if (!SetProperty(ref _value, v))
                return;
            OnPropertyChanged(nameof(ValueText));
            if (Editor.IsLoading)
                return;
            double stored = Math.Round(fromSlider?.Invoke(v) ?? v, 4);
            Editor.Set(path, stored);
            onChanged?.Invoke(stored);
            Editor.Commit();
        }
    }

    public string ValueText => string.Format(format, _value);

    internal override void Load(SonarConfig config)
    {
        double stored = config.GetNumber(path);
        _value = Math.Clamp(toSlider?.Invoke(stored) ?? stored, minimum, maximum);
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
    }
}

public sealed record ChoiceOption(string Value, string Label);

public sealed class ChoiceSetting(PresetEditorViewModel editor, string label, string path,
    IReadOnlyList<ChoiceOption> options, Func<bool>? enabledWhen = null) : PresetSetting(editor, label, enabledWhen)
{
    private ChoiceOption? _selected;

    public IReadOnlyList<ChoiceOption> Options => options;

    public ChoiceOption? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || Editor.IsLoading || value is null)
                return;
            Editor.Set(path, value.Value);
            Editor.Commit();
        }
    }

    internal override void Load(SonarConfig config)
    {
        var current = config.GetString(path);
        _selected = options.FirstOrDefault(o => o.Value == current);
        OnPropertyChanged(nameof(Selected));
    }
}

/// <summary>A card in the editor: one effect, optionally with its on/off switch.</summary>
public sealed class PresetSection(string title, ToggleSetting? toggle, IReadOnlyList<PresetSetting> items, string? note = null)
{
    public string Title { get; } = title;
    public ToggleSetting? Toggle { get; } = toggle;
    public bool HasToggle => Toggle != null;
    public IReadOnlyList<PresetSetting> Items { get; } = items;
    public string? Note { get; } = note;
    public bool HasNote => Note != null;

    internal IEnumerable<PresetSetting> AllSettings => Toggle is null ? Items : Items.Prepend(Toggle);
}

/// <summary>
/// "Presets" tab: edits the selected preset of one channel and manages the channel's presets.
/// Like GG, every change goes straight to Sonar (which applies and saves it), built-in presets
/// included; "Restablecer" brings back a preset's original values.
/// </summary>
public sealed class PresetEditorViewModel : ObservableObject
{
    private const int MaxFavorites = 9;
    private const int MaxNameLength = 40;
    private static readonly TimeSpan LocalChangeGrace = TimeSpan.FromSeconds(1.5);

    private readonly MainViewModel _owner;
    private readonly LatestValueSender<string> _sender;
    private readonly Dictionary<string, SonarConfig> _selected = [];
    private ChannelViewModel _channel;
    private SonarConfig? _config;
    private IReadOnlyList<PresetSection> _sections = [];
    private ToggleSetting _eq = null!;
    private IReadOnlyList<EqBand> _bands = [];
    private DateTime _lastLocalChange;

    internal PresetEditorViewModel(MainViewModel owner)
    {
        _owner = owner;
        _sender = new LatestValueSender<string>(json => owner.Client!.UpdateConfigAsync(json), owner.ReportError);
        _channel = owner.Channels[0];
        _channel.PropertyChanged += OnChannelPropertyChanged;
        BuildSections();
        NewCommand = new RelayCommand(() => _ = CreateAsync(duplicate: false));
        DuplicateCommand = new RelayCommand(() => _ = CreateAsync(duplicate: true));
        RenameCommand = new RelayCommand(() => _ = RenameAsync());
        ResetCommand = new RelayCommand(Reset);
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync());
    }

    internal bool IsLoading { get; private set; }

    public IReadOnlyList<ChannelViewModel> Channels => _owner.Channels;

    public ChannelViewModel Channel
    {
        get => _channel;
        set
        {
            if (value is null || value == _channel)
                return;
            _channel.PropertyChanged -= OnChannelPropertyChanged;
            _channel = value;
            _channel.PropertyChanged += OnChannelPropertyChanged;
            OnPropertyChanged(nameof(Channel));
            BuildSections();
            Load(_selected.GetValueOrDefault(value.Vad!));
        }
    }

    public IReadOnlyList<PresetSection> Sections
    {
        get => _sections;
        private set => SetProperty(ref _sections, value);
    }

    /// <summary>The parametric EQ's switch (<c>parametricEQ.enabled</c>).</summary>
    public ToggleSetting Eq
    {
        get => _eq;
        private set => SetProperty(ref _eq, value);
    }

    public IReadOnlyList<EqBand> Bands
    {
        get => _bands;
        private set => SetProperty(ref _bands, value);
    }

    public bool HasConfig => _config != null;

    public string PresetInfo => _config switch
    {
        null => "",
        { IsPreset: true } => "Preset de SteelSeries: los cambios se guardan en él y «Restablecer» recupera sus valores originales. Para tener tu versión aparte, duplícalo.",
        _ => "Preset propio: los cambios se aplican y se guardan al momento.",
    };

    public bool CanRename => _config is { IsPreset: false };

    /// <summary>Sonar's built-in presets can't be deleted, and a channel keeps at least one of its own.</summary>
    public bool CanDelete => _config is { IsPreset: false } && _channel.Presets.Count(p => !p.IsPreset) > 1;

    public bool IsFavorite
    {
        get => _config?.IsFavorite ?? false;
        set
        {
            if (_config is null || value == _config.IsFavorite)
                return;
            if (value && _channel.Presets.Count(p => p.IsFavorite) >= MaxFavorites)
            {
                _owner.Status = $"{_channel.DisplayName} ya tiene {MaxFavorites} favoritos, el máximo de Sonar";
                OnPropertyChanged(nameof(IsFavorite));
                return;
            }
            var id = _config.Id;
            _ = ManageAsync(c => c.SetConfigFavoriteAsync(id, value));
        }
    }

    public ICommand NewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Latest selected configs from Sonar; reloads the editor unless the user is mid-edit.</summary>
    internal void ApplySelected(IReadOnlyList<SonarConfig> selected)
    {
        _selected.Clear();
        foreach (var config in selected)
            _selected[config.VirtualAudioDevice] = config;
        var server = _selected.GetValueOrDefault(_channel.Vad!);
        bool editing = _sender.IsBusy || DateTime.UtcNow - _lastLocalChange < LocalChangeGrace;
        if (server is null || server.Id != _config?.Id || !editing)
            Load(server);
    }

    internal void Clear()
    {
        _selected.Clear();
        Load(null);
    }

    internal async Task ReloadAsync()
    {
        var client = _owner.Client;
        if (client is null)
            return;
        ApplySelected(await client.GetSelectedConfigsAsync());
    }

    internal void Set(string path, JsonNode? value) => _config?.Set(path, value);

    /// <summary>Sends the edited preset (only the latest state while values keep changing).</summary>
    internal void Commit()
    {
        if (_config is null || _owner.Client is null)
            return;
        _lastLocalChange = DateTime.UtcNow;
        RefreshEnabled();
        _sender.Post(_config.Json.ToJsonString());
    }

    private void Load(SonarConfig? config)
    {
        _config = config?.Clone();
        IsLoading = true;
        try
        {
            if (_config != null)
            {
                foreach (var setting in AllSettings)
                    setting.Load(_config);
            }
        }
        finally
        {
            IsLoading = false;
        }
        RefreshEnabled();
        OnPropertyChanged(nameof(HasConfig));
        OnPropertyChanged(nameof(PresetInfo));
        RefreshActions();
    }

    private void RefreshActions()
    {
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsFavorite));
    }

    private void RefreshEnabled()
    {
        foreach (var setting in AllSettings)
            setting.RefreshEnabled();
    }

    private IEnumerable<PresetSetting> AllSettings =>
        Sections.SelectMany(s => s.AllSettings).Append(Eq).Concat(Bands);

    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The preset list changes after creating, renaming or deleting one.
        if (e.PropertyName == nameof(ChannelViewModel.SelectedPreset))
            RefreshActions();
    }

    // --- Preset management -------------------------------------------------------

    private async Task CreateAsync(bool duplicate)
    {
        var source = _config;
        if (duplicate && source is null)
            return;
        var name = AskName(duplicate ? "Duplicar preset" : "Nuevo preset",
            UniqueName(duplicate ? source!.Name + " *" : "Nuevo preset"), current: null);
        if (name is null)
            return;
        var vad = _channel.Vad!;
        var json = new JsonObject
        {
            ["name"] = name,
            ["virtualAudioDevice"] = vad,
            ["isPreset"] = false,
            // -1: Sonar fills in its current schema version.
            ["schemaVersion"] = duplicate ? source!.Json["schemaVersion"]?.DeepClone() : -1,
        };
        if (duplicate)
        {
            // As GG: copy the settings, but a copy of a built-in preset gets the channel's
            // default icon. Its reset values are Sonar's neutral ones, not the source's.
            json["data"] = source!.Data.DeepClone();
            if (!source.IsPreset && source.Json["image"] is JsonNode image)
                json["image"] = image.DeepClone();
        }
        await ManageAsync(async c =>
        {
            var created = await c.CreateConfigAsync(json);
            await c.SelectConfigAsync(created.Id);
        });
    }

    private async Task RenameAsync()
    {
        if (_config is not { IsPreset: false } config)
            return;
        var name = AskName("Renombrar preset", config.Name, current: config.Name);
        if (name is null || name == config.Name)
            return;
        config.Name = name;
        var json = config.Json.ToJsonString();
        await ManageAsync(c => c.UpdateConfigAsync(json));
    }

    private void Reset()
    {
        if (_config is null)
            return;
        if (!Confirm("Restablecer preset",
                $"¿Restablecer «{_config.Name}»? Todos sus ajustes volverán a los valores predeterminados.", "Restablecer"))
            return;
        _config.ResetData();
        var config = _config;
        Load(config);
        Commit();
    }

    private async Task DeleteAsync()
    {
        if (_config is null || !CanDelete)
            return;
        if (!Confirm("Borrar preset", $"¿Borrar el preset «{_config.Name}»? No se puede deshacer.", "Borrar"))
            return;
        var id = _config.Id;
        await ManageAsync(c => c.DeleteConfigAsync(id));
    }

    /// <summary>Runs a change to the preset list, then reloads the list and the selection.</summary>
    private async Task ManageAsync(Func<SonarClient, Task> action)
    {
        var client = _owner.Client;
        if (client is null)
            return;
        try
        {
            await action(client);
        }
        catch (Exception e)
        {
            _owner.ReportError(e);
        }
        _owner.InvalidatePresets(_channel.Vad!);
        await _owner.RefreshAllAsync();
        RefreshActions();
    }

    /// <summary>GG adds " *" until the name is free.</summary>
    private string UniqueName(string name)
    {
        while (IsNameTaken(name, current: null) && name.Length < MaxNameLength - 1)
            name += " *";
        return name;
    }

    private bool IsNameTaken(string name, string? current) =>
        !string.Equals(name, current, StringComparison.OrdinalIgnoreCase)
        && _channel.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private string? AskName(string title, string initial, string? current)
    {
        var dialog = new NameDialog(title, initial, name =>
            name.Length == 0 ? "Escribe un nombre"
            : name.Length > MaxNameLength ? $"Máximo {MaxNameLength} caracteres"
            : IsNameTaken(name, current) ? $"Ya hay un preset de {_channel.DisplayName} con ese nombre"
            : null)
        {
            Owner = OwnerWindow(),
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static bool Confirm(string title, string message, string confirmText) =>
        new ConfirmDialog(title, message, confirmText) { Owner = OwnerWindow() }.ShowDialog() == true;

    private static Window? OwnerWindow() => Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

    // --- Settings per kind of channel (ranges and rules taken from GG's UI) ----------

    private static readonly string[] Boosts = ["bassBoostState", "voiceClarityState", "trebleBoostState"];

    private void BuildSections()
    {
        bool isVoice = _channel.Vad is "chatRender" or "chatCapture";
        // GG keeps the bass / voice / treble boosts switched on and off together with the EQ.
        var eq = new ToggleSetting(this, "Ecualizador", "parametricEQ.enabled", onChanged: isVoice ? null : on =>
        {
            foreach (var boost in Boosts)
                Set(boost + ".enabled", on);
        });
        Eq = eq;
        Bands = Enumerable.Range(1, 10).Select(n => new EqBand(this, n, () => eq.Value)).ToArray();
        Sections = isVoice ? VoiceSections() : PlaybackSections(eq);
        RefreshEnabled();
    }

    /// <summary>Game, media and aux.</summary>
    private IReadOnlyList<PresetSection> PlaybackSections(ToggleSetting eq)
    {
        SliderSetting Boost(string label, string state) => new(this, label, state + ".value", -12, 12, 0.1, "{0:0.0} dB",
            enabledWhen: () => eq.Value, onChanged: _ => Set(state + ".enabled", eq.Value));

        var surround = new ToggleSetting(this, "Audio espacial", "virtualSurroundState");
        var smartVolume = new ToggleSetting(this, "Smart Volume", "smartVolume.enabled");
        return
        [
            new PresetSection("Graves, voz y agudos", null,
                [Boost("Graves", "bassBoostState"), Boost("Voz", "voiceClarityState"), Boost("Agudos", "trebleBoostState")],
                "Refuerzos aparte de las bandas; se activan y desactivan con el ecualizador."),
            new PresetSection("Audio espacial", surround,
                [new ChoiceSetting(this, "Modo", "formFactor",
                    [new ChoiceOption("headphones", "Auriculares"), new ChoiceOption("speakers", "Altavoces")],
                    () => surround.Value)]),
            new PresetSection("Aumento de volumen", null,
                [new SliderSetting(this, "Ganancia", "generalGain", 0, 12, 0.1, "{0:0.0} dB")]),
            new PresetSection("Smart Volume", smartVolume,
                [Percent("Nivel", "smartVolume.volumeLevel", () => smartVolume.Value)],
                "Iguala el volumen entre sonidos fuertes y suaves."),
        ];
    }

    /// <summary>Chat (incoming voice) and microphone.</summary>
    private IReadOnlyList<PresetSection> VoiceSections()
    {
        var clearCast = new ToggleSetting(this, "Cancelación de ruido IA", "noiseCancelingState.enabled");
        var background = new ToggleSetting(this, "Ruido de fondo", "noiseReductionState.enabled", () => !clearCast.Value);
        var impact = new ToggleSetting(this, "Ruido de impactos", "impactNoiseReductionState.enabled", () => !clearCast.Value);
        var gate = new ToggleSetting(this, "Puerta de ruido", "noiseGateState.enabled");
        var autoGate = new ToggleSetting(this, "Umbral automático", "automaticNoiseGateState.enabled", () => gate.Value);
        var stabilizer = new ToggleSetting(this, "Estabilizador de volumen", "volumeStabilizerState.enabled");
        bool isMic = _channel.Vad == "chatCapture";
        return
        [
            new PresetSection("Cancelación de ruido IA (ClearCast)", clearCast,
                [new SliderSetting(this, "Intensidad", "noiseCancelingState.value", 0, 100, 1, "{0:0} %",
                    NoiseCancelingToPercent, PercentToNoiseCanceling, () => clearCast.Value)],
                isMic ? "Elimina el ruido de tu micrófono." : "Elimina el ruido de las voces que te llegan."),
            new PresetSection("Reducción de ruido", null,
                [
                    background, Percent("Intensidad", "noiseReductionState.value", () => !clearCast.Value && background.Value),
                    impact, Percent("Intensidad", "impactNoiseReductionState.value", () => !clearCast.Value && impact.Value),
                ],
                "No se usa con la cancelación de ruido IA activada."),
            new PresetSection("Puerta de ruido", gate,
                [
                    autoGate,
                    new SliderSetting(this, "Umbral", "noiseGateState.value", -60, -10, 0.1, "{0:0.0} dB",
                        enabledWhen: () => gate.Value && !autoGate.Value),
                ]),
            new PresetSection("Estabilizador de volumen", stabilizer,
                [Percent("Nivel", "volumeStabilizerState.value", () => stabilizer.Value)]),
        ];
    }

    /// <summary>A 0..1 value shown as 0..100 %.</summary>
    private SliderSetting Percent(string label, string path, Func<bool> enabledWhen) =>
        new(this, label, path, 0, 100, 1, "{0:0} %", v => v * 100, p => p / 100, enabledWhen);

    // GG shows ClearCast's strength on a log scale: value v ∈ [0, 1] maps to a gain from -60 to 0 dB.
    private static double NoiseCancelingToPercent(double v) => 100 * Math.Clamp(Math.Pow(10, (60 * v - 60) / 20), 0, 1);

    private static double PercentToNoiseCanceling(double percent) =>
        percent <= 0 ? 0 : Math.Clamp(1 + Math.Log10(percent / 100) / 3, 0, 1);
}
