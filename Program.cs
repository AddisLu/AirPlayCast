using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;

namespace AirPlayCast;

static class Program
{
    private const string MutexName = "AirPlayCast.SingleInstance";
    private const string ShowEventName = "AirPlayCast.ShowPanel";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--console")) return RunConsole(args).GetAwaiter().GetResult();
        if (args.SkipWhile(a => a != "--write-icon").Skip(1).FirstOrDefault() is { } icoPath) { WriteIco(icoPath); return 0; }

        using var mutex = new Mutex(true, MutexName, out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            showEvent.Set(); // already running: open its panel instead
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        var ctx = new TrayContext(fromAutostart: args.Contains("--autostart"));
        var sync = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => sync.Post(_ => ctx.ShowPanel(), null), null, -1, false);
        Application.ThreadException += (_, e) => Log.Write($"UI exception: {e.Exception}");
        Application.Run(ctx);
        return 0;
    }

    /// <summary>Writes the app icon (PNG-in-ICO, several sizes) for the exe resource.</summary>
    private static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 256];
        var images = sizes.Select(s =>
        {
            using var bmp = TrayContext.DrawIcon(s, active: true);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        images.ForEach(w.Write);
    }

    /// <summary>
    /// Headless mode for testing / scripting:
    /// AirPlayCast --console [--host IP] [--port N] [--volume 0-100] [--seconds N] [--tone] [--latency ms]
    /// </summary>
    private static async Task<int> RunConsole(string[] args)
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

        IPAddress? host = Arg("--host") is { } h ? IPAddress.Parse(h) : null;
        int port = int.TryParse(Arg("--port"), out var p) ? p : 0;
        if (host == null)
        {
            Console.WriteLine("Searching for AirPlay receivers...");
            var devices = await MdnsBrowser.BrowseAsync(TimeSpan.FromSeconds(3));
            devices.ForEach(d => Console.WriteLine($"  {d}  {string.Join(' ', d.Txt.Select(kv => $"{kv.Key}={kv.Value}"))}"));
            var dev = devices.FirstOrDefault(d => d.Name.Contains("RX-V", StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault();
            if (dev == null) { Console.WriteLine("No receiver found"); return 1; }
            host = dev.Address;
            port = dev.Port;
        }
        if (port == 0) port = 1025;

        int latencyFrames = int.TryParse(Arg("--latency"), out var ms) ? ms * RaopClient.SampleRate / 1000 : 88200;
        using var client = new RaopClient(host, port) { LatencyFrames = latencyFrames };
        client.Log += m => Console.WriteLine($"[raop] {m}");
        var done = new TaskCompletionSource();
        client.Disconnected += r => { Console.WriteLine($"[raop] {r}"); done.TrySetResult(); };

        Console.WriteLine($"Connecting to {host}:{port}...");
        await client.ConnectAsync();
        await client.SetVolumeAsync(double.TryParse(Arg("--volume"), out var v) ? v : 40);

        using var source = args.Contains("--tone") ? null : new LoopbackSource();
        if (source != null)
        {
            Console.WriteLine($"Capturing: {source.DeviceName}");
            source.Start();
            client.StartStreaming(source.Fill);
        }
        else
        {
            double phase = 0;
            client.StartStreaming(pcm =>
            {
                for (int i = 0; i < pcm.Length; i += 2)
                {
                    short s = (short)(Math.Sin(phase) * 6000);
                    pcm[i] = pcm[i + 1] = s;
                    phase += 2 * Math.PI * 440 / RaopClient.SampleRate;
                }
            });
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        if (int.TryParse(Arg("--seconds"), out var secs)) _ = Task.Delay(secs * 1000).ContinueWith(_ => done.TrySetResult());
        Console.WriteLine("Streaming. Ctrl+C to stop.");
        await done.Task;
        await client.StopAsync();
        Console.WriteLine("Stopped.");
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}
