using System.Text.Json;
using Microsoft.Win32;

namespace AirPlayCast;

public sealed class Settings
{
    public string? DeviceName { get; set; }
    public string? DeviceAddress { get; set; }
    public int DevicePort { get; set; }
    /// <summary>Output endpoint id to capture; null follows the Windows default device.</summary>
    public string? SourceId { get; set; }
    public double Volume { get; set; } = 40;
    /// <summary>Put a Yamaha receiver in standby when streaming stops (only if it is still on the AirPlay input).</summary>
    public bool StandbyOnStop { get; set; } = true;
    public bool MuteLocal { get; set; } = true;
    /// <summary>
    /// How far ahead audio is buffered on the receiver. AirPlay's standard is 2000 ms; shorter keeps
    /// video in sync but leaves less time to recover lost Wi-Fi packets.
    /// </summary>
    public int LatencyMs { get; set; } = 500;
    /// <summary>Master volume (dB) a Yamaha receiver reaches at 100% on the slider; caps how loud the slider can go.</summary>
    public double YamahaMaxDb { get; set; } = -10;
    /// <summary>Set while we hold the local device muted, so a crash can be undone next launch.</summary>
    public string? MutedDeviceId { get; set; }
    public bool FirstRunDone { get; set; }

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPlayCast");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write($"Saving settings failed: {ex.Message}"); }
    }
}

/// <summary>Per-user "run at sign-in" entry.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AirPlayCast";

    private static string Command => $"{LaunchCommand} --autostart";

    /// <summary>
    /// How to start this app. Smart App Control blocks unsigned apphost exes, so the app runs
    /// as "dotnet AirPlayCast.dll", wrapped in a headless conhost so no console window appears.
    /// </summary>
    public static string LaunchCommand
    {
        get
        {
            var proc = Environment.ProcessPath!;
            if (!Path.GetFileNameWithoutExtension(proc).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return $"\"{proc}\"";
            var conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            var dll = Path.Combine(AppContext.BaseDirectory, "AirPlayCast.dll");
            return $"\"{conhost}\" --headless \"{proc}\" \"{dll}\"";
        }
    }

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, Command);
            else k.DeleteValue(Name, false);
        }
    }

    /// <summary>Re-points an existing entry at this exe (after the app was moved or reinstalled).</summary>
    public static void Refresh()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k?.GetValue(Name) is string s && s != Command) k.SetValue(Name, Command);
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
