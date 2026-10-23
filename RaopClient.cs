using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AirPlayCast;

/// <summary>
/// AirPlay 1 (RAOP) sender: RTSP session control over TCP, ALAC audio over RTP/UDP,
/// plus the timing (NTP-like) and control (sync / retransmit) channels.
/// </summary>
public sealed class RaopClient : IDisposable
{
    public const int SampleRate = 44100;
    private const int Frames = AlacEncoder.FramesPerPacket;
    private const int HistorySize = 1024;

    private readonly IPEndPoint _remote;
    private readonly TcpClient _tcp = new(AddressFamily.InterNetwork) { NoDelay = true };
    private NetworkStream _rtsp = null!;
    private readonly SemaphoreSlim _rtspLock = new(1, 1);
    private int _cseq;
    private string? _session;
    private string _url = "";
    private IPAddress _localIp = IPAddress.Any;

    private readonly string _clientId = RandomHex(8);
    private readonly uint _activeRemote = (uint)Random.Shared.Next();
    private readonly uint _ssrc = (uint)Random.Shared.Next();
    private readonly ushort _seq0 = (ushort)Random.Shared.Next();
    private readonly uint _rtp0 = (uint)Random.Shared.Next();

    private UdpClient _audio = null!, _control = null!, _timing = null!;
    private IPEndPoint _serverAudio = null!, _serverControl = null!;
    private readonly byte[]?[] _history = new byte[]?[HistorySize];

    private readonly CancellationTokenSource _cts = new();
    private Thread? _sender;
    private int _disconnectRaised;

    // Clock shared by RTP scheduling and the NTP timestamps we hand to the receiver.
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly long NtpBaseTicks = (DateTime.UtcNow - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks;

    /// <summary>How far ahead of playback audio is sent, in frames (AirPlay default is 2 s).</summary>
    public int LatencyFrames { get; init; } = 88200;
    public int? DeviceLatencyFrames { get; private set; }

    public event Action<string>? Log;
    /// <summary>Raised once if the receiver drops the session (e.g. another source took over).</summary>
    public event Action<string>? Disconnected;

    public RaopClient(IPAddress host, int port) => _remote = new IPEndPoint(host, port);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcp.ConnectAsync(_remote, timeout.Token);
        }
        _rtsp = _tcp.GetStream();
        _localIp = ((IPEndPoint)_tcp.Client.LocalEndPoint!).Address.MapToIPv4();
        _url = $"rtsp://{_localIp}/{(uint)Random.Shared.Next()}";

        _audio = new UdpClient(new IPEndPoint(_localIp, 0));
        _control = new UdpClient(new IPEndPoint(_localIp, 0));
        _timing = new UdpClient(new IPEndPoint(_localIp, 0));
        StartBackground(TimingLoop, "raop-timing");
        StartBackground(ControlLoop, "raop-control");

        await RequestAsync("OPTIONS", "*", ct: ct);

        var sdp = string.Join("\r\n",
            "v=0",
            $"o=iTunes {_url[(_url.LastIndexOf('/') + 1)..]} 0 IN IP4 {_localIp}",
            "s=iTunes",
            $"c=IN IP4 {_remote.Address}",
            "t=0 0",
            "m=audio 0 RTP/AVP 96",
            "a=rtpmap:96 AppleLossless",
            $"a=fmtp:96 {Frames} 0 16 40 10 14 2 255 0 0 {SampleRate}",
            "");
        await RequestAsync("ANNOUNCE", _url, body: sdp, contentType: "application/sdp", ct: ct);

        int controlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
        int timingPort = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;
        var setup = await RequestAsync("SETUP", _url, headers: new()
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={controlPort};timing_port={timingPort}",
        }, ct: ct);

        _session = setup.Headers.GetValueOrDefault("Session")?.Split(';')[0].Trim()
                   ?? throw new IOException("SETUP response has no Session");
        var transport = setup.Headers.GetValueOrDefault("Transport") ?? "";
        int serverPort = TransportPort(transport, "server_port") ?? throw new IOException("SETUP response has no server_port");
        _serverAudio = new IPEndPoint(_remote.Address, serverPort);
        _serverControl = new IPEndPoint(_remote.Address, TransportPort(transport, "control_port") ?? serverPort + 1);
        var serverTiming = new IPEndPoint(_remote.Address, TransportPort(transport, "timing_port") ?? serverPort + 2);
        Log?.Invoke($"Transport: {transport}");

        // The receiver initiates timing requests to us. Sending to its ports first opens the
        // Windows Firewall state for those flows, so its unsolicited UDP gets through.
        SendTimingRequest(serverTiming);
        SendSync(_rtp0, first: true);
        await Task.Delay(100, ct);

        var record = await RequestAsync("RECORD", _url, headers: new()
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_seq0};rtptime={_rtp0}",
        }, ct: ct);
        if (int.TryParse(record.Headers.GetValueOrDefault("Audio-Latency"), out var lat))
        {
            DeviceLatencyFrames = lat;
            Log?.Invoke($"Receiver reports Audio-Latency {lat} frames ({lat * 1000 / SampleRate} ms)");
        }

        StartBackground(KeepAliveLoop, "raop-keepalive");
    }

    /// <summary>Starts streaming; <paramref name="fill"/> must write 352 interleaved stereo frames.</summary>
    public void StartStreaming(Action<Span<short>> fill)
    {
        _sender = new Thread(() => SendLoop(fill)) { IsBackground = true, Name = "raop-audio", Priority = ThreadPriority.Highest };
        _sender.Start();
    }

    /// <summary>Sets AirPlay volume from 0..100 (mapped to -30..0 dB, 0 = mute).</summary>
    public Task SetVolumeAsync(double percent, CancellationToken ct = default)
    {
        double db = percent <= 0 ? -144 : -30 + 30 * Math.Clamp(percent, 0, 100) / 100;
        return RequestAsync("SET_PARAMETER", _url, body: $"volume: {db.ToString("F6", CultureInfo.InvariantCulture)}\r\n",
            contentType: "text/parameters", ct: ct);
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _sender?.Join(500);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_session != null && _tcp.Connected) await RequestAsync("TEARDOWN", _url, ct: timeout.Token, ignoreCancel: true);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcp.Dispose();
        _audio?.Dispose();
        _control?.Dispose();
        _timing?.Dispose();
    }

    // ---------------------------------------------------------------- audio

    private void SendLoop(Action<Span<short>> fill)
    {
        var pcm = new short[Frames * 2];
        var frame = new byte[AlacEncoder.MaxFrameBytes];
        long ticksPerPacket = Stopwatch.Frequency * Frames / SampleRate;
        long start = Clock.ElapsedTicks;
        long n = 0;
        int packetsPerSecond = SampleRate / Frames;
        WinMm.TimeBeginPeriod(1);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long target = start + n * ticksPerPacket;
                long now = Clock.ElapsedTicks;
                if (now < target)
                {
                    long waitMs = (target - now) * 1000 / Stopwatch.Frequency;
                    if (waitMs >= 2) Thread.Sleep((int)waitMs - 1);
                    else Thread.SpinWait(200);
                    continue;
                }
                if (now - target > Stopwatch.Frequency / 2)
                {
                    // We stalled for > 0.5 s; skip ahead instead of bursting stale audio.
                    long skip = (now - target) / ticksPerPacket;
                    n += skip;
                    Log?.Invoke($"Sender stalled, skipped {skip} packets");
                }

                uint rtpTime = unchecked(_rtp0 + (uint)(n * Frames));
                if (n % packetsPerSecond == 0) SendSync(rtpTime, first: n == 0);

                fill(pcm);
                int len = AlacEncoder.Encode(pcm, frame);
                var pkt = new byte[12 + len];
                pkt[0] = 0x80;
                pkt[1] = (byte)(n == 0 ? 0xE0 : 0x60);
                ushort seq = unchecked((ushort)(_seq0 + n));
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), seq);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), rtpTime);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(8), _ssrc);
                frame.AsSpan(0, len).CopyTo(pkt.AsSpan(12));
                _history[seq % HistorySize] = pkt;
                try { _audio.Send(pkt, pkt.Length, _serverAudio); } catch (SocketException) { }
                n++;
            }
        }
        catch (ObjectDisposedException) { }
        finally { WinMm.TimeEndPeriod(1); }
    }

    private void SendSync(uint rtpNow, bool first)
    {
        var p = new byte[20];
        p[0] = (byte)(first ? 0x90 : 0x80);
        p[1] = 0xD4;
        p[3] = 0x07;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), unchecked(rtpNow - (uint)LatencyFrames));
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(8), NtpNow());
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), rtpNow);
        try { _control.Send(p, p.Length, _serverControl); } catch (SocketException) { }
    }

    private void ControlLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            byte[] d;
            try { d = _control.Receive(ref any); }
            catch { if (_cts.IsCancellationRequested) return; continue; }
            if (d.Length < 8 || (d[1] & 0x7F) != 0x55) continue; // retransmit request
            ushort first = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6));
            for (int i = 0; i < count; i++)
            {
                ushort seq = unchecked((ushort)(first + i));
                var pkt = _history[seq % HistorySize];
                if (pkt == null || BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(2)) != seq) continue;
                var r = new byte[4 + pkt.Length];
                r[0] = 0x80; r[1] = 0xD6; r[3] = 0x01;
                pkt.CopyTo(r, 4);
                try { _control.Send(r, r.Length, _serverControl); } catch (SocketException) { }
            }
        }
    }

    private void TimingLoop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            byte[] d;
            try { d = _timing.Receive(ref from); }
            catch { if (_cts.IsCancellationRequested) return; continue; }
            if (d.Length < 32 || (d[1] & 0x7F) != 0x52) continue; // timing request
            ulong received = NtpNow();
            if (Interlocked.Increment(ref _timingRequests) == 1) Log?.Invoke($"First timing request from {from}");
            var r = new byte[32];
            r[0] = 0x80; r[1] = 0xD3; r[3] = 0x07;
            d.AsSpan(24, 8).CopyTo(r.AsSpan(8));             // origin = their transmit time
            BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(16), received);
            BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(24), NtpNow());
            try { _timing.Send(r, r.Length, from); } catch (SocketException) { }
        }
    }

    private int _timingRequests;

    private void SendTimingRequest(IPEndPoint to)
    {
        var p = new byte[32];
        p[0] = 0x80; p[1] = 0xD2; p[3] = 0x07;
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(24), NtpNow());
        try { _timing.Send(p, p.Length, to); } catch (SocketException) { }
    }

    private static ulong NtpNow()
    {
        long ticks = NtpBaseTicks + (long)(Clock.ElapsedTicks * (10_000_000.0 / Stopwatch.Frequency));
        ulong sec = (ulong)(ticks / 10_000_000);
        ulong frac = (ulong)(ticks % 10_000_000) * (1UL << 32) / 10_000_000;
        return sec << 32 | frac;
    }

    // ---------------------------------------------------------------- RTSP

    private record RtspResponse(int Status, Dictionary<string, string> Headers, string Body);

    private async Task KeepAliveLoop()
    {
        int seconds = 0;
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            bool closed;
            try { closed = _tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0; }
            catch { closed = true; }
            if (!closed && ++seconds % 15 == 0)
            {
                try { await RequestAsync("OPTIONS", "*", ct: _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch { closed = true; }
            }
            if (closed)
            {
                RaiseDisconnected("音響中斷了連線（可能被其他裝置接手或關機）");
                return;
            }
        }
    }

    private void RaiseDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0 && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            Disconnected?.Invoke(reason);
        }
    }

    private async Task<RtspResponse> RequestAsync(string method, string uri, Dictionary<string, string>? headers = null,
        string? body = null, string? contentType = null, CancellationToken ct = default, bool ignoreCancel = false)
    {
        await _rtspLock.WaitAsync(ignoreCancel ? CancellationToken.None : ct);
        try
        {
            var sb = new StringBuilder();
            sb.Append($"{method} {uri} RTSP/1.0\r\n");
            sb.Append($"CSeq: {++_cseq}\r\n");
            sb.Append("User-Agent: iTunes/11.0.4 (Windows; N)\r\n");
            sb.Append($"Client-Instance: {_clientId}\r\n");
            sb.Append($"DACP-ID: {_clientId}\r\n");
            sb.Append($"Active-Remote: {_activeRemote}\r\n");
            if (_session != null) sb.Append($"Session: {_session}\r\n");
            if (headers != null) foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
            var bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
            if (contentType != null) sb.Append($"Content-Type: {contentType}\r\n");
            if (bodyBytes.Length > 0) sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
            sb.Append("\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            await _rtsp.WriteAsync(head, ct);
            if (bodyBytes.Length > 0) await _rtsp.WriteAsync(bodyBytes, ct);

            var resp = await ReadResponseAsync(ct);
            if (method != "OPTIONS" || resp.Status != 200)
                Log?.Invoke($"{method} -> {resp.Status} {string.Join("; ", resp.Headers.Select(kv => $"{kv.Key}={kv.Value}"))}");
            if (resp.Status != 200) throw new IOException($"{method} 失敗：RTSP {resp.Status}");
            return resp;
        }
        finally { _rtspLock.Release(); }
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        string status = await ReadLineAsync(ct);
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int code)) throw new IOException($"無效的 RTSP 回應：{status}");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (string line; (line = await ReadLineAsync(ct)).Length > 0;)
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        string body = "";
        if (int.TryParse(headers.GetValueOrDefault("Content-Length"), out int len) && len > 0)
        {
            var buf = new byte[len];
            await _rtsp.ReadExactlyAsync(buf, ct);
            body = Encoding.UTF8.GetString(buf);
        }
        return new RtspResponse(code, headers, body);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await _rtsp.ReadAsync(one, ct) == 0) throw new IOException("RTSP 連線已關閉");
            if (one[0] == '\n') break;
            if (one[0] != '\r') bytes.Add(one[0]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int? TransportPort(string transport, string key)
    {
        var m = Regex.Match(transport, $@"(?:^|;){key}=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private void StartBackground(Action loop, string name) =>
        new Thread(() => loop()) { IsBackground = true, Name = name }.Start();

    private void StartBackground(Func<Task> loop, string name) => _ = Task.Run(loop);

    private static string RandomHex(int bytes) => Convert.ToHexString(Random.Shared.GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), bytes));
}

internal static class WinMm
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint ms);
}
