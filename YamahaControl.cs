using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace AirPlayCast;

/// <summary>
/// Yamaha network receivers (RX-V series) through their YNC HTTP API: power, master volume
/// (they ignore AirPlay volume unless "volume interlock" is enabled in the setup menu), input,
/// sound program and the main sound options.
/// </summary>
public sealed class YamahaControl(IPAddress host)
{
    public IPAddress Host { get; } = host;

    public record Status(
        bool PowerOn, string? Input, double Db, bool Muted, string? Sleep,
        string? Program, bool Straight, bool Enhancer, double Bass, double Treble,
        bool Direct, bool ExtraBass, bool AdaptiveDrc);

    public record InputItem(string Param, string Title);

    /// <summary>
    /// DSP programs of the RX-V4xx/5xx line, taken from the receiver's own web UI (which lists every
    /// model's programs and drops the 6xx+ ones); the 5-channel model calls its stereo mode "5ch Stereo".
    /// </summary>
    public static readonly string[] Programs =
    [
        "Hall in Munich", "Hall in Vienna", "Chamber", "Cellar Club", "The Roxy Theatre", "The Bottom Line",
        "Sports", "Action Game", "Roleplaying Game", "Music Video",
        "Standard", "Spectacle", "Sci-Fi", "Adventure", "Drama", "Mono Movie",
        "2ch Stereo", "5ch Stereo", "Surround Decoder",
    ];

    public static readonly string[] SleepValues = ["Off", "30 min", "60 min", "90 min", "120 min"];

    /// <summary>Returns a controller if <paramref name="host"/> answers the Yamaha API, otherwise null.</summary>
    public static async Task<YamahaControl?> TryCreateAsync(IPAddress host)
    {
        var y = new YamahaControl(host);
        try { return await y.GetStatusAsync() != null ? y : null; }
        catch { return null; }
    }

    /// <summary>Main zone state, or null if the reply can't be parsed.</summary>
    public async Task<Status?> GetStatusAsync()
    {
        // The RX-V477 rejects GET on the whole <Volume> node (RC=2), so read everything from Basic_Status.
        var xml = await SendAsync("<YAMAHA_AV cmd=\"GET\"><Main_Zone><Basic_Status>GetParam</Basic_Status></Main_Zone></YAMAHA_AV>");
        var lvl = Regex.Match(xml, @"<Lvl><Val>(-?\d+)</Val><Exp>(\d)</Exp>");
        if (!lvl.Success) return null;
        string? Text(string tag) => Regex.Match(xml, $"<{tag}>([^<]*)</{tag}>") is { Success: true } m ? m.Groups[1].Value : null;
        bool On(string tag) => Text(tag) is "On" or "Auto";
        double Level(string tag) => Regex.Match(xml, $@"<{tag}><Val>(-?\d+)</Val><Exp>(\d)</Exp>") is { Success: true } m
            ? int.Parse(m.Groups[1].Value) / Math.Pow(10, int.Parse(m.Groups[2].Value)) : 0;
        return new Status(
            PowerOn: Text("Power") == "On",
            Input: Text("Input_Sel"),
            Db: Level("Lvl"),
            Muted: On("Mute"),
            Sleep: Text("Sleep"),
            Program: Text("Sound_Program"),
            Straight: On("Straight"),
            Enhancer: On("Enhancer"),
            Bass: Level("Bass"),
            Treble: Level("Treble"),
            Direct: Regex.IsMatch(xml, "<Direct><Mode>On</Mode></Direct>"),
            ExtraBass: On("Extra_Bass"),
            AdaptiveDrc: On("Adaptive_DRC"));
    }

    /// <summary>Selectable inputs (read-only ones such as "iPod (USB)" are left out).</summary>
    public async Task<List<InputItem>> GetInputsAsync()
    {
        var xml = await SendAsync("<YAMAHA_AV cmd=\"GET\"><Main_Zone><Input><Input_Sel_Item>GetParam</Input_Sel_Item></Input></Main_Zone></YAMAHA_AV>");
        return Regex.Matches(xml, @"<Param>([^<]*)</Param><RW>([^<]*)</RW><Title>([^<]*)</Title>")
            .Where(m => m.Groups[2].Value.Contains('W'))
            .Select(m => new InputItem(m.Groups[1].Value, m.Groups[3].Value.Trim()))
            .ToList();
    }

    /// <summary>Turns the main zone on, or puts the receiver in standby.</summary>
    public Task SetPowerAsync(bool on) =>
        PutAsync($"<Power_Control><Power>{(on ? "On" : "Standby")}</Power></Power_Control>");

    public Task SetVolumeAsync(double db) => PutAsync($"<Volume><Lvl>{Tenths(db)}</Lvl></Volume>");

    public Task SetMuteAsync(bool on) => PutAsync($"<Volume><Mute>{OnOff(on)}</Mute></Volume>");

    /// <summary>Applies one sound setting chosen in the panel.</summary>
    public Task SetOptionAsync(string key, string value) => key switch
    {
        "input" => PutAsync($"<Input><Input_Sel>{SecurityElement.Escape(value)}</Input_Sel></Input>"),
        "program" when Programs.Contains(value) => PutAsync($"<Surround><Program_Sel><Current><Sound_Program>{SecurityElement.Escape(value)}</Sound_Program></Current></Program_Sel></Surround>"),
        "straight" => PutAsync($"<Surround><Program_Sel><Current><Straight>{OnOff(value == "true")}</Straight></Current></Program_Sel></Surround>"),
        "enhancer" => PutAsync($"<Surround><Program_Sel><Current><Enhancer>{OnOff(value == "true")}</Enhancer></Current></Program_Sel></Surround>"),
        "bass" => PutAsync($"<Sound_Video><Tone><Bass>{Tenths(ToneDb(value))}</Bass></Tone></Sound_Video>"),
        "treble" => PutAsync($"<Sound_Video><Tone><Treble>{Tenths(ToneDb(value))}</Treble></Tone></Sound_Video>"),
        "direct" => PutAsync($"<Sound_Video><Direct><Mode>{OnOff(value == "true")}</Mode></Direct></Sound_Video>"),
        "extrabass" => PutAsync($"<Sound_Video><Extra_Bass>{(value == "true" ? "Auto" : "Off")}</Extra_Bass></Sound_Video>"),
        "drc" => PutAsync($"<Sound_Video><Adaptive_DRC>{(value == "true" ? "Auto" : "Off")}</Adaptive_DRC></Sound_Video>"),
        "sleep" when SleepValues.Contains(value) => PutAsync($"<Power_Control><Sleep>{value}</Sleep></Power_Control>"),
        _ => throw new ArgumentException($"Unknown receiver setting {key}={value}"),
    };

    private static double ToneDb(string value) => Math.Clamp(double.Parse(value, CultureInfo.InvariantCulture), -6, 6);
    private static string OnOff(bool on) => on ? "On" : "Off";

    /// <summary>A dB value as the API's tenths-of-a-dB element, rounded to 0.5 dB steps.</summary>
    private static string Tenths(double db) =>
        $"<Val>{((int)Math.Round(db * 2) * 5).ToString(CultureInfo.InvariantCulture)}</Val><Exp>1</Exp><Unit>dB</Unit>";

    /// <summary>PUT into the main zone; throws when the receiver rejects it (RC other than 0).</summary>
    private async Task PutAsync(string mainZoneXml)
    {
        var xml = await SendAsync($"<YAMAHA_AV cmd=\"PUT\"><Main_Zone>{mainZoneXml}</Main_Zone></YAMAHA_AV>");
        var rc = Regex.Match(xml, "RC=\"(\\d+)\"");
        if (rc.Success && rc.Groups[1].Value != "0") throw new IOException($"Yamaha API rejected the change (RC={rc.Groups[1].Value})");
    }

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
