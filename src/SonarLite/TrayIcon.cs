using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SonarLite;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action open, Func<Task> restartSonar, Func<Task> switchToGg, Func<Task> exit)
    {
        _image = CreateIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => open());
        menu.Items.Add("Reiniciar Sonar", null, async (_, _) => await restartSonar());
        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Iniciar con Windows (en lugar de GG)") { CheckOnClick = true };
        autostart.Click += (_, _) =>
        {
            if (autostart.Checked)
                Autostart.Enable();
            else
                Autostart.Disable();
        };
        menu.Items.Add(autostart);
        menu.Items.Add("Volver a SteelSeries GG", null, async (_, _) => await switchToGg());
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, async (_, _) => await exit());

        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "Sonar Lite",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                open();
        };
    }

    public Icon Image => _image;

    public string Tooltip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>Simple sound-wave glyph, drawn at runtime so the app needs no binary assets.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(255, 255, 106, 0));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int[] heights = [6, 14, 20, 14, 6];
            for (int i = 0; i < heights.Length; i++)
            {
                float x = 8 + i * 4;
                g.DrawLine(pen, x, 16 - heights[i] / 2f, x, 16 + heights[i] / 2f);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
