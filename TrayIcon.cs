namespace BetterHwMonitor;

/// <summary>System tray icon (Windows Forms NotifyIcon) with Open / Exit menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        Icon = CreateIcon();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = Icon,
            Text = "Better HW Monitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    /// <summary>The generated app icon (also used for the window).</summary>
    public System.Drawing.Icon Icon { get; }

    public void ShowHint(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    /// <summary>Draws a small "two curves" chart icon so no .ico file is needed.</summary>
    private static System.Drawing.Icon CreateIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(37, 37, 38));
            g.FillRectangle(background, 1, 1, 30, 30);

            using var blue = new System.Drawing.Pen(System.Drawing.Color.FromArgb(59, 142, 234), 3f);
            g.DrawLines(blue, new[]
            {
                new System.Drawing.Point(4, 25), new System.Drawing.Point(11, 17),
                new System.Drawing.Point(17, 21), new System.Drawing.Point(27, 8),
            });

            using var red = new System.Drawing.Pen(System.Drawing.Color.FromArgb(229, 83, 75), 3f);
            g.DrawLines(red, new[]
            {
                new System.Drawing.Point(4, 15), new System.Drawing.Point(12, 11),
                new System.Drawing.Point(19, 14), new System.Drawing.Point(27, 18),
            });
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
