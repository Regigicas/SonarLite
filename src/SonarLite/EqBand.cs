using System.Globalization;
using SonarLite.Core;

namespace SonarLite;

/// <summary>
/// One of the 10 bands of a preset's parametric EQ (<c>parametricEQ.filter1..10</c>), with GG's
/// ranges. Frequency and Q also have a log-scale position for their sliders.
/// </summary>
public sealed class EqBand : PresetSetting
{
    public const double MinFrequency = 20;
    public const double MaxFrequency = 20000;
    public const double MaxGain = 12;
    public const double MinQ = 0.5;
    public const double MaxQ = 10;

    /// <summary>The types GG's UI offers (Sonar knows a few more, which are shown blank).</summary>
    public static IReadOnlyList<ChoiceOption> Types { get; } =
    [
        new("peakingEQ", "Campana"),
        new("lowShelving", "Estante graves"),
        new("highShelving", "Estante agudos"),
        new("lowPass", "Paso bajo"),
        new("highPass", "Paso alto"),
        new("notchFilter", "Rechazo (notch)"),
    ];

    private readonly string _path;
    private bool _isOn;
    private string _type = "peakingEQ";
    private double _frequency = 1000;
    private double _gain;
    private double _q = 0.7071;

    public EqBand(PresetEditorViewModel editor, int number, Func<bool> enabledWhen)
        : base(editor, number.ToString(CultureInfo.InvariantCulture), enabledWhen)
    {
        Number = number;
        _path = $"parametricEQ.filter{number}.";
    }

    public int Number { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value))
                return;
            OnPropertyChanged(nameof(CanEditGain));
            Send("enabled", value);
        }
    }

    public string Type => _type;

    public ChoiceOption? SelectedType
    {
        get => Types.FirstOrDefault(t => t.Value == _type);
        set
        {
            if (value is null || value.Value == _type)
                return;
            _type = value.Value;
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(UsesGain));
            OnPropertyChanged(nameof(CanEditGain));
            Send("type", value.Value);
        }
    }

    /// <summary>Pass and notch filters have no gain.</summary>
    public bool UsesGain => _type is "peakingEQ" or "lowShelving" or "highShelving";

    public bool CanEditGain => _isOn && UsesGain;

    public double Frequency
    {
        get => _frequency;
        set => SetFrequency(value, send: true);
    }

    public double FrequencyPosition
    {
        get => Math.Log10(_frequency);
        set => Frequency = Math.Pow(10, value);
    }

    public string FrequencyText
    {
        get => _frequency.ToString(_frequency < 1000 ? "0.#" : "0", CultureInfo.CurrentCulture);
        set => ParseInto(value, v => Frequency = v, nameof(FrequencyText));
    }

    public double Gain
    {
        get => _gain;
        set => SetGain(value, send: true);
    }

    public string GainText
    {
        get => _gain.ToString("0.0", CultureInfo.CurrentCulture);
        set => ParseInto(value, v => Gain = v, nameof(GainText));
    }

    public double Q
    {
        get => _q;
        set
        {
            double v = Math.Round(Math.Clamp(value, MinQ, MaxQ), value < 1 ? 3 : 2);
            if (!SetProperty(ref _q, v, nameof(Q)))
                return;
            OnPropertyChanged(nameof(QPosition));
            OnPropertyChanged(nameof(QText));
            Send("qFactor", v);
        }
    }

    public double QPosition
    {
        get => Math.Log10(_q);
        set => Q = Math.Pow(10, value);
    }

    public string QText
    {
        get => _q.ToString("0.0##", CultureInfo.CurrentCulture);
        set => ParseInto(value, v => Q = v, nameof(QText));
    }

    public static double MinFrequencyPosition => Math.Log10(MinFrequency);
    public static double MaxFrequencyPosition => Math.Log10(MaxFrequency);
    public static double MinQPosition => Math.Log10(MinQ);
    public static double MaxQPosition => Math.Log10(MaxQ);

    /// <summary>Moves the band's point on the curve: frequency and gain in one update.</summary>
    public void MoveTo(double frequency, double gain)
    {
        bool changed = SetFrequency(frequency, send: false);
        if (UsesGain)
            changed |= SetGain(gain, send: false);
        if (changed && !Editor.IsLoading)
        {
            Editor.Set(_path + "frequency", _frequency);
            Editor.Set(_path + "gain", _gain);
            Editor.Commit();
        }
    }

    internal override void Load(SonarConfig config)
    {
        _isOn = config.GetBool(_path + "enabled");
        _type = config.GetString(_path + "type") ?? "peakingEQ";
        _frequency = Math.Clamp(config.GetNumber(_path + "frequency", 1000), MinFrequency, MaxFrequency);
        _gain = Math.Clamp(config.GetNumber(_path + "gain"), -MaxGain, MaxGain);
        _q = Math.Clamp(config.GetNumber(_path + "qFactor", 0.7071), MinQ, MaxQ);
        foreach (var name in (string[])[nameof(IsOn), nameof(Type), nameof(SelectedType), nameof(UsesGain), nameof(CanEditGain),
                     nameof(Frequency), nameof(FrequencyPosition), nameof(FrequencyText),
                     nameof(Gain), nameof(GainText), nameof(Q), nameof(QPosition), nameof(QText)])
            OnPropertyChanged(name);
    }

    private bool SetFrequency(double value, bool send)
    {
        value = Math.Clamp(value, MinFrequency, MaxFrequency);
        double v = value < 1000 ? Math.Round(value, 1) : Math.Round(value);
        if (!SetProperty(ref _frequency, v, nameof(Frequency)))
            return false;
        OnPropertyChanged(nameof(FrequencyPosition));
        OnPropertyChanged(nameof(FrequencyText));
        if (send)
            Send("frequency", v);
        return true;
    }

    private bool SetGain(double value, bool send)
    {
        double v = Math.Round(Math.Clamp(value, -MaxGain, MaxGain), 1);
        if (!SetProperty(ref _gain, v, nameof(Gain)))
            return false;
        OnPropertyChanged(nameof(GainText));
        if (send)
            Send("gain", v);
        return true;
    }

    private void Send(string field, System.Text.Json.Nodes.JsonNode? value)
    {
        if (Editor.IsLoading)
            return;
        Editor.Set(_path + field, value);
        Editor.Commit();
    }

    /// <summary>Accepts both "0,7" and "0.7" (and stray units); reverts the text when it isn't a number.</summary>
    private void ParseInto(string text, Action<double> apply, string textProperty)
    {
        var number = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+').ToArray()).Replace(',', '.');
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            apply(v);
        // Re-read even when the value didn't change, so the box shows the clamped, formatted number.
        OnPropertyChanged(textProperty);
    }
}
