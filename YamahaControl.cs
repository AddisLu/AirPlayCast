using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AirPlayCast;

/// <summary>
/// Yamaha network receivers (RX-V series) ignore AirPlay volume unless "volume interlock" is
/// enabled in their setup menu, so the master volume is driven through the YNC HTTP API instead.
/// </summary>
public sealed class YamahaControl(IPAddress host)
{
    public IPAddress Host { get; } = host;

    public record Status(bool PowerOn, string? Input, double Db, bool Muted);

    /// <summary>Returns a controller if <paramref name="host"/> answers the Yamaha API, otherwise null.</summary>
    public static async Task<YamahaControl?> TryCreateAsync(IPAddress host)
    {
        var y = new YamahaControl(host);
        try { return await y.GetStatusAsync() != null ? y : null; }
        catch { return null; }
    }

    /// <summary>Main zone power, input and volume, or null if the reply can't be parsed.</summary>
    public async Task<Status?> GetStatusAsync()
    {
        // The RX-V477 rejects GET on the whole <Volume> node (RC=2), so read everything from Basic_Status.
        var xml = await SendAsync("<YAMAHA_AV cmd=\"GET\"><Main_Zone><Basic_Status>GetParam</Basic_Status></Main_Zone></YAMAHA_AV>");
        var lvl = Regex.Match(xml, @"<Lvl><Val>(-?\d+)</Val><Exp>(\d)</Exp>");
        if (!lvl.Success) return null;
        double db = int.Parse(lvl.Groups[1].Value) / Math.Pow(10, int.Parse(lvl.Groups[2].Value));
        var input = Regex.Match(xml, @"<Input_Sel>([^<]*)</Input_Sel>");
        return new Status(
            PowerOn: Regex.IsMatch(xml, @"<Power>On</Power>"),
            Input: input.Success ? input.Groups[1].Value : null,
            Db: db,
            Muted: Regex.IsMatch(xml, @"<Mute>On</Mute>"));
    }

    /// <summary>Turns the main zone on, or puts the receiver in standby.</summary>
    public Task SetPowerAsync(bool on) =>
        SendAsync($"<YAMAHA_AV cmd=\"PUT\"><Main_Zone><Power_Control><Power>{(on ? "On" : "Standby")}</Power></Power_Control></Main_Zone></YAMAHA_AV>");

    public Task SetVolumeAsync(double db)
    {
        int val = (int)Math.Round(db * 2) * 5; // 0.5 dB steps, value in tenths of a dB
        return SendAsync($"<YAMAHA_AV cmd=\"PUT\"><Main_Zone><Volume><Lvl><Val>{val.ToString(CultureInfo.InvariantCulture)}</Val><Exp>1</Exp><Unit>dB</Unit></Lvl></Volume></Main_Zone></YAMAHA_AV>");
    }

    public Task SetMuteAsync(bool on) =>
        SendAsync($"<YAMAHA_AV cmd=\"PUT\"><Main_Zone><Volume><Mute>{(on ? "On" : "Off")}</Mute></Volume></Main_Zone></YAMAHA_AV>");

    /// <summary>
    /// Raw HTTP POST. The receiver's embedded web server only reads a body that arrives in the
    /// same segment as the headers, which HttpClient doesn't guarantee, so the request goes out in one write.
    /// </summary>
    private async Task<string> SendAsync(string body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await tcp.ConnectAsync(Host, 80, cts.Token);
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"POST /YamahaRemoteControl/ctrl HTTP/1.1\r\nHost: {Host}\r\nContent-Type: text/xml; charset=UTF-8\r\n" +
            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        var stream = tcp.GetStream();
        await stream.WriteAsync(head.Concat(payload).ToArray(), cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var resp = await reader.ReadToEndAsync(cts.Token);
        if (!resp.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !resp.StartsWith("HTTP/1.0 200", StringComparison.Ordinal))
            throw new IOException($"Yamaha API: {resp.Split('\n')[0].Trim()}");
        int bodyAt = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return bodyAt < 0 ? "" : resp[(bodyAt + 4)..];
    }
}
