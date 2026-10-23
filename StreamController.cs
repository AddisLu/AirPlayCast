using System.Net;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AirPlayCast;

public enum StreamStatus { Idle, Connecting, Streaming, Error }

/// <summary>
/// Owns the capture source, the AirPlay session and what goes with it (local mute, device
/// discovery, receiver power/volume). Streaming only ever starts on the user's request.
/// Lives on the UI thread.
/// </summary>
public sealed class StreamController : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DefaultDeviceWatcher _watcher;
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    private LoopbackSource? _source;
    private RaopClient? _client;
    private MMDevice? _mutedDevice;
    private double _sentVolume = double.NaN;
    private bool _sendingVolume;

    public Settings Settings { get; }
    public List<AirPlayDevice> Devices { get; private set; } = [];
    public bool Searching { get; private set; }
    public StreamStatus Status { get; private set; }
    public string? Message { get; private set; }
    public int? LatencyMs { get; private set; }
    public float Level => _source?.Level ?? 0;
    public string SourceName => _source?.DeviceName ?? "—";

    public event Action? Changed;

    public StreamController(Settings settings)
    {
        Settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _watcher = new DefaultDeviceWatcher(() => _ui.Post(_ => { if (Settings.SourceId == null) RecreateSource(); }, null));
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
        _pollTimer.Tick += (_, _) => PollReceiver();
    }

    public async Task InitAsync()
    {
        RestoreCrashedMute();
        RecreateSource();
        _pollTimer.Start();
        await RefreshDevicesAsync();
    }

    // ---------------------------------------------------------------- devices

    public AirPlayDevice? SelectedDevice =>
        Devices.FirstOrDefault(d => d.Name == Settings.DeviceName)
        ?? (Settings.DeviceAddress != null && IPAddress.TryParse(Settings.DeviceAddress, out var ip)
            ? new AirPlayDevice(Settings.DeviceName ?? Settings.DeviceAddress, ip, Settings.DevicePort, [])
            : null);

    public async Task RefreshDevicesAsync()
    {
        if (Searching) return;
        Searching = true;
        Notify();
        try
        {
            var found = await MdnsBrowser.BrowseAsync(TimeSpan.FromSeconds(2.5));
            if (found.Count > 0 || Devices.Count == 0) Devices = found;
            var pick = Devices.FirstOrDefault(d => d.Name == Settings.DeviceName)
                       ?? (Settings.DeviceName == null
                           ? Devices.FirstOrDefault(d => d.Name.Contains("RX-V", StringComparison.OrdinalIgnoreCase)) ?? Devices.FirstOrDefault()
                           : null);
            if (pick != null)
            {
                Remember(pick);
                await EnsureYamahaAsync(pick.Address);
                await SyncFromReceiverAsync(force: true);
            }
            Log.Write($"Discovery: {string.Join(", ", Devices)}");
        }
        catch (Exception ex) { Log.Write($"Discovery failed: {ex.Message}"); }
        finally
        {
            Searching = false;
            Notify();
        }
    }

    public async Task SelectDeviceAsync(string name)
    {
        var dev = Devices.FirstOrDefault(d => d.Name == name);
        if (dev == null || dev.Name == Settings.DeviceName) return;
        bool wasStreaming = Status is StreamStatus.Streaming or StreamStatus.Connecting;
        if (wasStreaming) await StopAsync(allowStandby: false);
        Remember(dev);
        _yamaha = null;
        VolumeDb = null;
        await EnsureYamahaAsync(dev.Address);
        Notify();
        if (wasStreaming) await StartAsync();
    }

    private void Remember(AirPlayDevice dev)
    {
        Settings.DeviceName = dev.Name;
        Settings.DeviceAddress = dev.Address.ToString();
        Settings.DevicePort = dev.Port;
        Settings.Save();
    }

    // ---------------------------------------------------------------- capture source

    public List<(string Id, string Name)> Sources()
    {
        var list = new List<(string, string)>();
        try
        {
            var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            list.Add(("", $"預設輸出裝置（{def.FriendlyName}）"));
        }
        catch { list.Add(("", "預設輸出裝置")); }
        foreach (var d in LoopbackSource.OutputDevices()) list.Add((d.ID, d.FriendlyName));
        return list;
    }

    public void SelectSource(string? id)
    {
        Settings.SourceId = string.IsNullOrEmpty(id) ? null : id;
        Settings.Save();
        RecreateSource();
    }

    private void RecreateSource()
    {
        bool holdMute = _mutedDevice != null;
        if (holdMute) ApplyLocalMute(false);
        _source?.Dispose();
        _source = null;
        try
        {
            MMDevice? dev = null;
            if (Settings.SourceId != null)
            {
                try { dev = _enumerator.GetDevice(Settings.SourceId); } catch { }
                if (dev?.State != DeviceState.Active) dev = null;
            }
            var src = new LoopbackSource(dev);
            src.Stopped += ex =>
            {
                if (ex == null) return;
                Log.Write($"Capture stopped: {ex.Message}");
                _ui.Post(_ => { if (_source == src) RecreateSource(); }, null);
            };
            src.Start();
            _source = src;
            Log.Write($"Capturing {src.DeviceName}");
        }
        catch (Exception ex)
        {
            Log.Write($"Capture failed: {ex.Message}");
            Message = "無法擷取 Windows 音訊：" + ex.Message;
        }
        if (Status == StreamStatus.Streaming) ApplyLocalMute(true);
        Notify();
    }

    private void Fill(Span<short> pcm)
    {
        var s = _source;
        if (s == null) pcm.Clear();
        else s.Fill(pcm);
    }

    // ---------------------------------------------------------------- streaming

    public Task ToggleAsync() => Status is StreamStatus.Streaming or StreamStatus.Connecting ? StopAsync() : StartAsync();

    public async Task StartAsync()
    {
        if (Status is StreamStatus.Connecting or StreamStatus.Streaming) return;
        if (SelectedDevice == null) await RefreshDevicesAsync();
        if (SelectedDevice == null)
        {
            SetStatus(StreamStatus.Error, "找不到 AirPlay 裝置，請確認音響在同一個網路上");
            return;
        }
        SetStatus(StreamStatus.Connecting, null);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var dev = SelectedDevice!;
            var client = new RaopClient(dev.Address, dev.Port) { LatencyFrames = Settings.LatencyMs * RaopClient.SampleRate / 1000 };
            client.Log += m => Log.Write($"[raop] {m}");
            client.Disconnected += reason => _ui.Post(_ => OnDisconnected(client, reason), null);
            try
            {
                Log.Write($"Connecting to {dev}");
                await client.ConnectAsync();
                if (Status != StreamStatus.Connecting) { client.Dispose(); return; } // cancelled meanwhile
                await EnsureYamahaAsync(dev.Address);
                if (_yamaha != null) await SyncFromReceiverAsync(force: true);
                else
                {
                    await client.SetVolumeAsync(Settings.Volume);
                    _sentVolume = Settings.Volume;
                }
                client.StartStreaming(Fill);
                _client = client;
                LatencyMs = (client.LatencyFrames + (client.DeviceLatencyFrames ?? 0)) * 1000 / RaopClient.SampleRate;
                ApplyLocalMute(true);
                SetStatus(StreamStatus.Streaming, null);
                return;
            }
            catch (Exception ex)
            {
                client.Dispose();
                Log.Write($"Connect failed: {ex.Message}");
                if (Status != StreamStatus.Connecting) return;
                if (attempt == 0) { await RefreshDevicesAsync(); continue; } // address may have changed
                SetStatus(StreamStatus.Error, "連線失敗：" + ex.Message);            }
        }
    }

    /// <param name="allowStandby">Honour "standby on stop" (off for device switches).</param>
    public async Task StopAsync(bool allowStandby = true)
    {
        var c = _client;
        _client = null;
        ApplyLocalMute(false);
        SetStatus(StreamStatus.Idle, null);
        if (c != null)
        {
            await c.StopAsync();
            c.Dispose();
            if (allowStandby && Settings.StandbyOnStop) await StandbyIfStillOnAirPlayAsync();
        }
    }

    /// <summary>Turns the receiver off after streaming, unless someone switched it to another input meanwhile.</summary>
    private async Task StandbyIfStillOnAirPlayAsync()
    {
        if (_yamaha is not { } y) return;
        try
        {
            var s = await y.GetStatusAsync();
            if (s is not { PowerOn: true } || !string.Equals(s.Input, "AirPlay", StringComparison.OrdinalIgnoreCase)) return;
            Log.Write("Stream stopped, putting the receiver in standby");
            await y.SetPowerAsync(false);
            ReceiverOn = false;
            Notify();
        }
        catch (Exception ex) { Log.Write($"Standby failed: {ex.Message}"); }
    }

    public async Task SetReceiverPowerAsync(bool on)
    {
        if (_yamaha is not { } y) return;
        if (!on && Status is StreamStatus.Streaming or StreamStatus.Connecting) await StopAsync(allowStandby: false);
        try
        {
            await y.SetPowerAsync(on);
            ReceiverOn = on;
            Notify();
            Log.Write($"Receiver power {(on ? "on" : "standby")} from panel");
        }
        catch (Exception ex) { Log.Write($"Power change failed: {ex.Message}"); }
    }

    private void OnDisconnected(RaopClient client, string reason)
    {
        if (client != _client) return;
        _client = null;
        client.Dispose();
        Log.Write($"Disconnected: {reason}");
        ApplyLocalMute(false);        SetStatus(StreamStatus.Idle, reason);
    }

    public void SetVolume(double v)
    {
        Settings.Volume = Math.Clamp(Math.Round(v), 0, 100);
        Settings.Save();
        _lastUserVolumeAt = Environment.TickCount64;
        if (_yamaha != null) VolumeDb = PercentToDb(Settings.Volume);
        Notify();
        _ = PushVolumeAsync();
    }

    private async Task PushVolumeAsync()
    {
        if (_sendingVolume) return;
        _sendingVolume = true;
        try
        {
            while (_sentVolume != Settings.Volume)
            {
                double v = Settings.Volume;
                try
                {
                    if (_yamaha is { } y)
                    {
                        if (v <= 0) await y.SetMuteAsync(true);
                        else
                        {
                            await y.SetVolumeAsync(PercentToDb(v));
                            if (_receiverMuted) await y.SetMuteAsync(false);
                        }
                        _receiverMuted = v <= 0;
                    }
                    else if (_client is { } c) await c.SetVolumeAsync(v);
                    else break;
                }
                catch (Exception ex)
                {
                    Log.Write($"Setting volume failed: {ex.Message}");
                    break;
                }
                _sentVolume = v;
            }
        }
        finally { _sendingVolume = false; }
    }

    // ---------------------------------------------------------------- Yamaha control

    private YamahaControl? _yamaha;
    private bool _receiverMuted;
    private long _lastUserVolumeAt, _lastReceiverPoll;

    /// <summary>The receiver's real master volume in dB, when it is a Yamaha we can talk to.</summary>
    public double? VolumeDb { get; private set; }

    /// <summary>Receiver power (true = on, false = standby); null when it isn't a controllable Yamaha.</summary>
    public bool? ReceiverOn { get; private set; }

    private double MinDb => -80;
    private double MaxDb => Math.Clamp(Settings.YamahaMaxDb, -60, 16.5);

    private double PercentToDb(double p) => Math.Round((MinDb + (MaxDb - MinDb) * p / 100) * 2) / 2;
    private double DbToPercent(double db) => Math.Clamp(Math.Round((db - MinDb) / (MaxDb - MinDb) * 100), 0, 100);

    private async Task EnsureYamahaAsync(IPAddress address)
    {
        if (_yamaha?.Host.Equals(address) == true) return;
        _yamaha = await YamahaControl.TryCreateAsync(address);
        VolumeDb = null;
        ReceiverOn = null;
        Log.Write(_yamaha != null ? "Yamaha network control available: volume goes to the receiver's master volume" : "No Yamaha API: using AirPlay volume");
    }

    /// <summary>
    /// Pulls the receiver's power state and volume into the panel (so the remote and front panel stay
    /// in sync); the volume is left alone while the user is moving the slider.
    /// </summary>
    private async Task SyncFromReceiverAsync(bool force = false)
    {
        if (_yamaha is not { } y) return;
        YamahaControl.Status? s;
        try { s = await y.GetStatusAsync(); } catch { return; }
        if (s == null) return;
        bool changed = ReceiverOn != s.PowerOn;
        ReceiverOn = s.PowerOn;

        bool userBusy = !force && Environment.TickCount64 - _lastUserVolumeAt < 2500;
        double p = s.Muted ? 0 : DbToPercent(s.Db);
        if (!userBusy && !_sendingVolume && (VolumeDb != s.Db || Settings.Volume != p || _receiverMuted != s.Muted))
        {
            VolumeDb = s.Db;
            _receiverMuted = s.Muted;
            Settings.Volume = _sentVolume = p;
            Settings.Save();
            changed = true;
        }
        if (changed) Notify();
    }

    /// <summary>Changes the receiver-side buffer; an active stream reconnects to apply it.</summary>
    public async Task SetLatencyAsync(int ms)
    {
        ms = Math.Clamp(ms, 100, 2000);
        if (ms == Settings.LatencyMs) return;
        Settings.LatencyMs = ms;
        Settings.Save();
        Notify();
        if (Status != StreamStatus.Streaming) return;
        Log.Write($"Latency changed to {ms} ms, reconnecting");
        await StopAsync(allowStandby: false);
        await StartAsync();
    }

    public void SetStandbyOnStop(bool on)
    {
        Settings.StandbyOnStop = on;
        Settings.Save();
        Notify();
    }

    public void SetMuteLocal(bool on)
    {
        Settings.MuteLocal = on;
        Settings.Save();
        ApplyLocalMute(Status == StreamStatus.Streaming);
        Notify();
    }

    // ---------------------------------------------------------------- receiver polling

    /// <summary>Follows power and volume changes made with the receiver's own remote or front panel.</summary>
    private void PollReceiver()
    {
        if (_yamaha == null || Environment.TickCount64 - _lastReceiverPoll < (Status == StreamStatus.Streaming ? 3000 : 5000)) return;
        _lastReceiverPoll = Environment.TickCount64;
        _ = SyncFromReceiverAsync();
    }

    private void ApplyLocalMute(bool streaming)
    {
        bool want = streaming && Settings.MuteLocal && _source != null;
        try
        {
            if (want && _mutedDevice == null)
            {
                var d = _source!.Device;
                if (d.AudioEndpointVolume.Mute) return; // user muted it already; leave it alone
                d.AudioEndpointVolume.Mute = true;
                _mutedDevice = d;
                Settings.MutedDeviceId = d.ID;
                Settings.Save();
            }
            else if (!want && _mutedDevice != null)
            {
                _mutedDevice.AudioEndpointVolume.Mute = false;
                _mutedDevice = null;
                Settings.MutedDeviceId = null;
                Settings.Save();
            }
        }
        catch (Exception ex) { Log.Write($"Local mute failed: {ex.Message}"); }
    }

    private void RestoreCrashedMute()
    {
        if (Settings.MutedDeviceId == null) return;
        try { _enumerator.GetDevice(Settings.MutedDeviceId).AudioEndpointVolume.Mute = false; } catch { }
        Settings.MutedDeviceId = null;
        Settings.Save();
    }

    private void SetStatus(StreamStatus s, string? message)
    {
        Status = s;
        Message = message;
        if (s != StreamStatus.Streaming) LatencyMs = null;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _pollTimer.Dispose();
        var c = _client;
        _client = null;
        ApplyLocalMute(false);
        if (c != null)
        {
            try { c.StopAsync().Wait(2500); } catch { }
            c.Dispose();
        }
        _source?.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
    }

    private sealed class DefaultDeviceWatcher(Action onDefaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) onDefaultChanged();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
