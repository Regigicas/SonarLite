using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SonarLite;

/// <summary>
/// Tray icon with a WPF context menu, so the menu follows the app's Fluent theme (light/dark,
/// rounded corners) instead of WinForms' classic ContextMenuStrip look.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private readonly ContextMenu _menu;
    private readonly MenuItem _autostartItem;
    private Window? _menuHost;

    public TrayIcon(Action open, Func<Task> restartSonar, Func<Task> switchToGg, Func<Task> exit)
    {
        _image = LoadIcon();

        // Not IsCheckable: the Fluent template would then reserve a check-box column on every
        // item. The state is shown with a check-box glyph in the icon column instead.
        _autostartItem = CreateItem("Iniciar con Windows (en lugar de GG)", null, () =>
        {
            if (Autostart.IsEnabled)
                Autostart.Disable();
            else
                Autostart.Enable();
        });

        _menu = new ContextMenu();
        _menu.Items.Add(CreateItem("Abrir", "", open));
        _menu.Items.Add(CreateItem("Reiniciar Sonar", "", async () => await restartSonar()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(CreateItem("Volver a SteelSeries GG", "", async () => await switchToGg()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CreateItem("Salir", "", async () => await exit()));
        _menu.Closed += (_, _) => _menuHost?.Hide();

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Sonar Lite",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
            else if (e.Button == Forms.MouseButtons.Right)
                ShowMenu();
        };
    }

    public string Tooltip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void Dispose()
    {
        _menu.IsOpen = false;
        _menuHost?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    private void ShowMenu()
    {
        // Checked / empty check-box glyphs, so the option reads as a toggle in both states.
        _autostartItem.Icon = Glyph(Autostart.IsEnabled ? "" : "");

        // A WPF popup only closes on an outside click if one of our windows is in the
        // foreground, so the menu is owned by an invisible 1x1 window placed at the cursor
        // (on the cursor's monitor, so the menu gets that monitor's DPI).
        var host = _menuHost ??= CreateMenuHost();
        var cursor = Forms.Cursor.Position;
        host.Show();
        var hwnd = new WindowInteropHelper(host).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, cursor.X, cursor.Y, 1, 1, SwpNoZOrder | SwpNoActivate);
        SetForegroundWindow(hwnd);
        host.Activate();

        _menu.PlacementTarget = host;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private static Window CreateMenuHost() => new()
    {
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = true,
        Topmost = true,
        Width = 1,
        Height = 1,
    };

    private static MenuItem CreateItem(string header, string? glyph, Action onClick)
    {
        var item = new MenuItem { Header = header, Icon = glyph is null ? null : Glyph(glyph) };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 14,
    };

    /// <summary>Loads app.ico at the tray's icon size (it has a frame per size, see scripts/make-icon.ps1).</summary>
    private static Drawing.Icon LoadIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
