using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SonarLite;

/// <summary>
/// Created on demand and destroyed when closed: the app lives in the tray, and keeping a
/// hidden WPF visual tree around costs more memory than rebuilding it.
/// </summary>
public partial class MainWindow : Window
{
    private static Rect? _lastBounds;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        if (_lastBounds is { } b)
        {
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
        }
        else
        {
            // First show: bottom-right corner, next to the tray.
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 12;
            Top = area.Bottom - Height - 12;
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
            _lastBounds = new Rect(Left, Top, Width, Height);
        base.OnClosing(e);
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
