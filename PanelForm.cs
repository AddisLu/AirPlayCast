using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AirPlayCast;

/// <summary>Borderless popup above the tray that hosts ui/index.html in WebView2.</summary>
public sealed class PanelForm : Form
{
    private const int LogicalWidth = 380;
    private readonly StreamController _ctl;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x15, 0x17, 0x1C) };
    private readonly System.Windows.Forms.Timer _levelTimer = new() { Interval = 50 };
    private int _logicalHeight = 700;
    private bool _ready;
    public long HiddenAt { get; private set; }

    public event Action? QuitRequested;

    public PanelForm(StreamController ctl)
    {
        _ctl = ctl;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x15, 0x17, 0x1C);
        Text = "AirPlayCast";
        Controls.Add(_web);
        _ctl.Changed += PushState;
        _levelTimer.Tick += (_, _) => Post(new JsonObject { ["type"] = "level", ["v"] = Math.Round(_ctl.Level, 4) });
        Deactivate += (_, _) => HidePanel();
        _ = InitWebAsync();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    private async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("airplaycast.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://airplaycast.local/index.html");
        }
        catch (Exception ex)
        {
            Log.Write($"WebView2 init failed: {ex}");
            MessageBox.Show("無法載入介面（需要 Microsoft Edge WebView2 Runtime）。\n" + ex.Message, "AirPlayCast", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = JsonNode.Parse(e.WebMessageAsJson)!;
            switch ((string?)m["cmd"])
            {
                case "ready":
                    _ready = true;
                    PushState();
                    break;
                case "size":
                    if ((int?)m["height"] is int h && h > 200 && h != _logicalHeight) { _logicalHeight = h; if (Visible) Reposition(); }
                    break;
                case "toggle": await _ctl.ToggleAsync(); break;
                case "refresh": await _ctl.RefreshDevicesAsync(); break;
                case "power": await _ctl.SetReceiverPowerAsync((bool)m["value"]!); break;
                case "device": await _ctl.SelectDeviceAsync((string)m["value"]!); break;
                case "source": _ctl.SelectSource((string?)m["value"]); break;
                case "volume": _ctl.SetVolume((double)m["value"]!); break;
                case "hide": HidePanel(); break;
                case "quit": QuitRequested?.Invoke(); break;
                case "set":
                    bool v = (bool)m["value"]!;
                    switch ((string?)m["key"])
                    {
                        case "autostart": Autostart.IsEnabled = v; PushState(); break;
                        case "standbyonstop": _ctl.SetStandbyOnStop(v); break;
                        case "mutelocal": _ctl.SetMuteLocal(v); break;
                    }
                    break;
            }
        }
        catch (Exception ex) { Log.Write($"UI message failed: {ex.Message}"); }
    }

    private void PushState()
    {
        if (!_ready) return;
        var dev = _ctl.SelectedDevice;
        Post(new JsonObject
        {
            ["type"] = "state",
            ["status"] = _ctl.Status.ToString().ToLowerInvariant(),
            ["message"] = _ctl.Message,
            ["searching"] = _ctl.Searching,
            ["devices"] = new JsonArray(_ctl.Devices.Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d.Name,
                ["ip"] = d.Address.ToString(),
                ["model"] = d.Txt.GetValueOrDefault("am"),
            }).ToArray()),
            ["device"] = dev?.Name,
            ["deviceIp"] = dev?.Address.ToString(),
            ["volume"] = _ctl.Settings.Volume,
            ["volumeDb"] = _ctl.VolumeDb,
            ["latencyMs"] = _ctl.LatencyMs ?? 0,
            ["sources"] = new JsonArray(_ctl.Sources().Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["name"] = s.Name }).ToArray()),
            ["source"] = _ctl.Settings.SourceId ?? "",
            ["autostart"] = Autostart.IsEnabled,
            ["standbyonstop"] = _ctl.Settings.StandbyOnStop,
            ["receiverOn"] = _ctl.ReceiverOn,
            ["mutelocal"] = _ctl.Settings.MuteLocal,
            ["version"] = Application.ProductVersion.Split('+')[0],
        });
    }

    private void Post(JsonNode msg)
    {
        if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(msg.ToJsonString());
    }

    public void TogglePanel()
    {
        if (Visible) { HidePanel(); return; }
        if (Environment.TickCount64 - HiddenAt < 300) return; // the tray click that just closed us
        Reposition();
        Show();
        Activate();
        _web.Focus();
        PushState();
        Post(new JsonObject { ["type"] = "shown" });
        _levelTimer.Start();
    }

    private void HidePanel()
    {
        if (!Visible) return;
        _levelTimer.Stop();
        HiddenAt = Environment.TickCount64;
        Hide();
    }

    private void Reposition()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        float scale = DeviceDpi / 96f;
        int w = (int)(LogicalWidth * scale), h = Math.Min((int)(_logicalHeight * scale), wa.Height - 24);
        int margin = (int)(12 * scale);
        // Anchor to the tray corner: bottom-right unless the taskbar sits on the left or top.
        int x = wa.Left > screen.Bounds.Left ? wa.Left + margin : wa.Right - w - margin;
        int y = wa.Top > screen.Bounds.Top ? wa.Top + margin : wa.Bottom - h - margin;
        SetBounds(x, y, w, h);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); return; }
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
