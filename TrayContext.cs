using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AirPlayCast;

public sealed class TrayContext : ApplicationContext
{
    private readonly StreamController _ctl;
    private readonly PanelForm _panel;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconIdle, _iconActive;
    private readonly ToolStripMenuItem _toggleItem, _powerItem;
    private StreamStatus _lastStatus;

    public TrayContext(bool fromAutostart)
    {
        var settings = Settings.Load();
        if (!settings.FirstRunDone)
        {
            // The app is meant to live in the tray from sign-in onwards.
            Autostart.IsEnabled = true;
            settings.FirstRunDone = true;
            settings.Save();
        }
        else Autostart.Refresh();

        _ctl = new StreamController(settings);
        _panel = new PanelForm(_ctl);
        _ = _panel.Handle; // create the window (and WebView2) up front so the first open is instant
        _panel.QuitRequested += Quit;

        int size = SystemInformation.SmallIconSize.Width;
        _iconIdle = MakeIcon(size, active: false);
        _iconActive = MakeIcon(size, active: true);

        _toggleItem = new ToolStripMenuItem("開始串流", null, async (_, _) => await _ctl.ToggleAsync());
        _powerItem = new ToolStripMenuItem("關閉音響", null, async (_, _) => await _ctl.SetReceiverPowerAsync(_ctl.ReceiverOn != true)) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟控制面板", null, (_, _) => _panel.TogglePanel());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_powerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => Quit());

        _tray = new NotifyIcon { Icon = _iconIdle, Text = "AirPlayCast", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _panel.TogglePanel(); };
        _ctl.Changed += OnChanged;

        _ = _ctl.InitAsync();
        if (!fromAutostart)
        {
            _tray.BalloonTipTitle = "AirPlayCast 已在系統匣執行";
            _tray.BalloonTipText = "點擊右下角的圖示開啟控制面板。";
            _tray.ShowBalloonTip(3000);
        }
    }

    public void ShowPanel() { if (!_panel.Visible) _panel.TogglePanel(); }

    private void OnChanged()
    {
        bool active = _ctl.Status == StreamStatus.Streaming;
        _tray.Icon = active ? _iconActive : _iconIdle;
        var dev = _ctl.SelectedDevice?.Name ?? "未選擇裝置";
        string status = _ctl.Status switch
        {
            StreamStatus.Streaming => $"串流到 {dev}",
            StreamStatus.Connecting => $"正在連線 {dev}…",
            StreamStatus.Error => "連線失敗",
            _ => $"閒置 · {dev}",
        };
        _tray.Text = Truncate($"AirPlayCast\n{status}", 127);
        _toggleItem.Text = _ctl.Status is StreamStatus.Streaming or StreamStatus.Connecting ? "停止串流" : "開始串流";
        _powerItem.Visible = _ctl.ReceiverOn != null;
        _powerItem.Text = _ctl.ReceiverOn == true ? "關閉音響（待機）" : "開啟音響";

        if (_lastStatus == StreamStatus.Streaming && _ctl.Status == StreamStatus.Idle && _ctl.Message != null)
        {
            _tray.BalloonTipTitle = "AirPlay 串流已中斷";
            _tray.BalloonTipText = _ctl.Message;
            _tray.ShowBalloonTip(4000);
        }
        _lastStatus = _ctl.Status;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void Quit()
    {
        _tray.Visible = false;
        _ctl.Dispose();
        _panel.Dispose();
        ExitThread();
    }

    /// <summary>Rounded tile with a speaker glyph; amber while streaming, grey when idle.</summary>
    public static Bitmap DrawIcon(int size, bool active)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 24f;
        var tile = active ? Color.FromArgb(0xF0, 0xA2, 0x3B) : Color.FromArgb(0xC9, 0xCD, 0xD4);
        using (var path = RoundedRect(new RectangleF(0.5f * s, 0.5f * s, 23 * s, 23 * s), 6 * s))
        using (var b = new SolidBrush(tile)) g.FillPath(b, path);

        using var pen = new Pen(Color.FromArgb(0x1A, 0x12, 0x06), Math.Max(1.5f, 2.2f * s)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var ink = new SolidBrush(pen.Color);
        PointF P(float x, float y) => new(x * s, y * s);
        var speaker = new[] { P(11.5f, 5.5f), P(7, 9), P(4, 9), P(4, 15), P(7, 15), P(11.5f, 18.5f) };
        g.FillPolygon(ink, speaker);
        g.DrawPolygon(pen, speaker);
        g.DrawArc(pen, 9.5f * s, 8.5f * s, 7 * s, 7 * s, -50, 100);
        if (size >= 24) g.DrawArc(pen, 8.5f * s, 5 * s, 12 * s, 14 * s, -55, 110);
        return bmp;
    }

    private static Icon MakeIcon(int size, bool active)
    {
        using var bmp = DrawIcon(size, active);
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
