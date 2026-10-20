using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SonarLite;

/// <summary>
/// Draws the parametric EQ's response (the sum of its active bands, as RBJ-cookbook biquads at
/// 48 kHz) and lets the user drag each band's point: left/right for frequency, up/down for gain,
/// mouse wheel for Q. Bass/voice/treble boosts are separate APO effects and aren't drawn.
/// </summary>
public sealed class EqCurve : FrameworkElement
{
    private const double SampleRate = 48000;
    private const double RangeDb = 15;
    private const int Samples = 240;
    private const double PointRadius = 8;
    private const double LeftMargin = 30;
    private const double BottomMargin = 18;

    private static readonly double[] GridFrequencies = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
    private static readonly double[] GridDb = [12, 6, 0, -6, -12];

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands), typeof(IReadOnlyList<EqBand>), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBandsChanged));

    public static readonly DependencyProperty CurveBrushProperty = BrushProperty(nameof(CurveBrush));
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush));
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(nameof(TextBrush));
    public static readonly DependencyProperty PointFillProperty = BrushProperty(nameof(PointFill));

    private EqBand? _hover;
    private EqBand? _drag;

    public IReadOnlyList<EqBand>? Bands
    {
        get => (IReadOnlyList<EqBand>?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public Brush? CurveBrush { get => (Brush?)GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush? PointFill { get => (Brush?)GetValue(PointFillProperty); set => SetValue(PointFillProperty, value); }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(EqCurve), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // The bands belong to the view model, which outlives this window: listen weakly.
    private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var curve = (EqCurve)d;
        curve._hover = curve._drag = null;
        foreach (var band in e.OldValue as IReadOnlyList<EqBand> ?? [])
            PropertyChangedEventManager.RemoveHandler(band, curve.OnBandChanged, "");
        foreach (var band in e.NewValue as IReadOnlyList<EqBand> ?? [])
            PropertyChangedEventManager.AddHandler(band, curve.OnBandChanged, "");
    }

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // --- Layout ------------------------------------------------------------------

    private Rect Plot => new(LeftMargin, PointRadius, Math.Max(1, ActualWidth - LeftMargin - PointRadius),
        Math.Max(1, ActualHeight - BottomMargin - PointRadius));

    private static double LogPosition(double frequency) =>
        Math.Log(frequency / EqBand.MinFrequency) / Math.Log(EqBand.MaxFrequency / EqBand.MinFrequency);

    private double XOf(double frequency) => Plot.Left + LogPosition(frequency) * Plot.Width;

    private double YOf(double db) => Plot.Top + (RangeDb - Math.Clamp(db, -RangeDb - 1, RangeDb + 1)) / (2 * RangeDb) * Plot.Height;

    private double FrequencyAt(double x) =>
        EqBand.MinFrequency * Math.Pow(EqBand.MaxFrequency / EqBand.MinFrequency, (x - Plot.Left) / Plot.Width);

    private double DbAt(double y) => RangeDb - (y - Plot.Top) / Plot.Height * 2 * RangeDb;

    private Point PointOf(EqBand band) => new(XOf(band.Frequency), YOf(band.UsesGain ? band.Gain : 0));

    // --- Drawing -----------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var plot = Plot;
        var text = TextBrush ?? Brushes.Gray;
        var curveBrush = CurveBrush ?? Brushes.DeepSkyBlue;
        var gridPen = new Pen(GridBrush ?? Brushes.DimGray, 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(string s, double size = 10, Brush? brush = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush ?? text, dpi);

        // Transparent background so the whole area takes the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        foreach (var f in GridFrequencies)
        {
            double x = XOf(f);
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Label(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}");
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width), plot.Bottom + 3));
        }
        foreach (var db in GridDb)
        {
            double y = YOf(db);
            dc.DrawLine(db == 0 ? new Pen(gridPen.Brush, 1.5) : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label(db > 0 ? $"+{db}" : $"{db}");
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var bands = (Bands ?? []).Where(b => b.IsOn).ToArray();
        var filters = bands.Select(Biquad.For).ToArray();
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineCtx = line.Open())
        using (var areaCtx = area.Open())
        {
            double zero = YOf(0);
            areaCtx.BeginFigure(new Point(plot.Left, zero), isFilled: true, isClosed: true);
            for (int i = 0; i <= Samples; i++)
            {
                double x = plot.Left + plot.Width * i / Samples;
                double w = 2 * Math.PI * FrequencyAt(x) / SampleRate;
                double db = 0;
                foreach (var filter in filters)
                    db += filter.MagnitudeDb(w);
                var p = new Point(x, YOf(db));
                if (i == 0)
                    lineCtx.BeginFigure(p, isFilled: false, isClosed: false);
                else
                    lineCtx.LineTo(p, isStroked: true, isSmoothJoin: true);
                areaCtx.LineTo(p, isStroked: false, isSmoothJoin: false);
            }
            areaCtx.LineTo(new Point(plot.Right, zero), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();

        dc.PushClip(new RectangleGeometry(plot));
        var fill = curveBrush.CloneCurrentValue();
        fill.Opacity = 0.18;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(curveBrush, 2), line);
        dc.Pop();

        foreach (var band in bands)
        {
            bool active = band == _drag || band == _hover;
            var center = PointOf(band);
            dc.DrawEllipse(active ? curveBrush : PointFill ?? Brushes.Black, new Pen(curveBrush, 1.5), center,
                PointRadius, PointRadius);
            var number = Label(band.Label, 10, active ? PointFill : text);
            dc.DrawText(number, new Point(center.X - number.Width / 2, center.Y - number.Height / 2));
        }

        // Readout of the band under the mouse.
        if ((_drag ?? _hover) is { } shown)
        {
            var readout = Label(shown.UsesGain
                ? $"Banda {shown.Label} · {shown.FrequencyText} Hz · {shown.GainText} dB · Q {shown.QText}"
                : $"Banda {shown.Label} · {shown.FrequencyText} Hz · Q {shown.QText}", 11);
            dc.DrawText(readout, new Point(plot.Left + 6, plot.Top + 2));
        }
    }

    // --- Mouse -------------------------------------------------------------------

    private EqBand? BandAt(Point p)
    {
        EqBand? best = null;
        double bestDistance = PointRadius + 4;
        foreach (var band in Bands ?? [])
        {
            if (!band.IsOn)
                continue;
            double distance = (PointOf(band) - p).Length;
            if (distance <= bestDistance)
                (best, bestDistance) = (band, distance);
        }
        return best;
    }

    private void SetHover(EqBand? band)
    {
        if (band == _hover)
            return;
        _hover = band;
        Cursor = band is null ? null : Cursors.SizeAll;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = BandAt(e.GetPosition(this));
        if (_drag != null && CaptureMouse())
            e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is null)
        {
            SetHover(BandAt(p));
            return;
        }
        var plot = Plot;
        _drag.MoveTo(FrequencyAt(Math.Clamp(p.X, plot.Left, plot.Right)), DbAt(Math.Clamp(p.Y, plot.Top, plot.Bottom)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag != null)
            ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_drag is null)
            SetHover(null);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((_drag ?? _hover) is not { } band)
            return;
        band.Q *= Math.Pow(1.1, e.Delta / 120.0);
        e.Handled = true;
    }

    /// <summary>One band as a biquad (Robert Bristow-Johnson's Audio EQ Cookbook).</summary>
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        public static Biquad For(EqBand band)
        {
            double w0 = 2 * Math.PI * band.Frequency / SampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * band.Q);
            double a = Math.Pow(10, band.Gain / 40);
            double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
            (double b0, double b1, double b2, double a0, double a1, double a2) = band.Type switch
            {
                "lowPass" => ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
                "highPass" => ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
                "notchFilter" => (1, -2 * cos, 1, 1 + alpha, -2 * cos, 1 - alpha),
                "lowShelving" => (
                    a * ((a + 1) - (a - 1) * cos + sqrtA2Alpha), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - sqrtA2Alpha),
                    (a + 1) + (a - 1) * cos + sqrtA2Alpha, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - sqrtA2Alpha),
                "highShelving" => (
                    a * ((a + 1) + (a - 1) * cos + sqrtA2Alpha), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - sqrtA2Alpha),
                    (a + 1) - (a - 1) * cos + sqrtA2Alpha, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - sqrtA2Alpha),
                "peakingEQ" => (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a),
                // Types GG doesn't offer: drawn flat.
                _ => (1, 0, 0, 1, 0, 0),
            };
            return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        /// <summary>|H(e^jw)| in dB.</summary>
        public double MagnitudeDb(double w)
        {
            double cos1 = Math.Cos(w), sin1 = Math.Sin(w), cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
            double numRe = B0 + B1 * cos1 + B2 * cos2, numIm = B1 * sin1 + B2 * sin2;
            double denRe = 1 + A1 * cos1 + A2 * cos2, denIm = A1 * sin1 + A2 * sin2;
            double num = numRe * numRe + numIm * numIm, den = denRe * denRe + denIm * denIm;
            return 10 * Math.Log10(Math.Max(num, 1e-12) / Math.Max(den, 1e-12));
        }
    }
}
