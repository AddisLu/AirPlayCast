using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AirPlayCast;

public record AirPlayDevice(string Name, IPAddress Address, int Port, Dictionary<string, string> Txt)
{
    public override string ToString() => $"{Name}  ({Address}:{Port})";
}

/// <summary>Minimal mDNS/DNS-SD browser for _raop._tcp (AirPlay audio) receivers.</summary>
public static class MdnsBrowser
{
    private const string Service = "_raop._tcp.local";

    public static async Task<List<AirPlayDevice>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var ptrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var srv = new Dictionary<string, (int port, string target)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        var query = BuildQuery(Service);
        var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        var sockets = new List<UdpClient>();
        foreach (var local in LocalIPv4Addresses())
        {
            try
            {
                var u = new UdpClient(new IPEndPoint(local, 0));
                sockets.Add(u);
                await u.SendAsync(query, group, ct);
            }
            catch (SocketException) { }
        }

        var end = DateTime.UtcNow + duration;
        var tasks = sockets.Select(async u =>
        {
            while (DateTime.UtcNow < end)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(end - DateTime.UtcNow);
                UdpReceiveResult r;
                try { r = await u.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                try { Parse(r.Buffer, ptrs, srv, txt, hosts); } catch { /* malformed packet */ }
            }
        }).ToList();
        await Task.WhenAll(tasks);
        sockets.ForEach(s => s.Dispose());

        var result = new List<AirPlayDevice>();
        foreach (var inst in ptrs)
        {
            if (!srv.TryGetValue(inst, out var s)) continue;
            if (!hosts.TryGetValue(s.target, out var ip))
            {
                try { ip = (await Dns.GetHostAddressesAsync(s.target, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
                catch { ip = null; }
            }
            if (ip == null) continue;
            var label = inst.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) ? inst[..^(Service.Length + 1)] : inst;
            int at = label.IndexOf('@');
            if (at >= 0) label = label[(at + 1)..];
            result.Add(new AirPlayDevice(label, ip, s.port, txt.GetValueOrDefault(inst) ?? new()));
        }
        return result.OrderBy(d => d.Name).ToList();
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    private static byte[] BuildQuery(string name)
    {
        var q = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            q.Add((byte)b.Length);
            q.AddRange(b);
        }
        q.AddRange([0, 0, 12, 0x80, 1]); // PTR, IN with unicast-response bit
        return q.ToArray();
    }

    private static void Parse(byte[] d, HashSet<string> ptrs, Dictionary<string, (int, string)> srv,
        Dictionary<string, Dictionary<string, string>> txt, Dictionary<string, IPAddress> hosts)
    {
        int pos = 12;
        int qd = d[4] << 8 | d[5];
        int rr = (d[6] << 8 | d[7]) + (d[8] << 8 | d[9]) + (d[10] << 8 | d[11]);
        for (int i = 0; i < qd; i++) { ReadName(d, ref pos); pos += 4; }
        for (int i = 0; i < rr; i++)
        {
            var name = ReadName(d, ref pos);
            int type = d[pos] << 8 | d[pos + 1];
            int len = d[pos + 8] << 8 | d[pos + 9];
            pos += 10;
            int rd = pos;
            switch (type)
            {
                case 12 when name.Equals(Service, StringComparison.OrdinalIgnoreCase):
                    ptrs.Add(ReadName(d, ref rd));
                    break;
                case 33:
                    int port = d[rd + 4] << 8 | d[rd + 5];
                    rd += 6;
                    srv[name] = (port, ReadName(d, ref rd));
                    break;
                case 16:
                    var dict = new Dictionary<string, string>();
                    for (int p = rd; p < rd + len;)
                    {
                        int l = d[p++];
                        var kv = Encoding.UTF8.GetString(d, p, l);
                        p += l;
                        int eq = kv.IndexOf('=');
                        if (eq > 0) dict[kv[..eq]] = kv[(eq + 1)..];
                    }
                    txt[name] = dict;
                    break;
                case 1:
                    hosts[name] = new IPAddress(d.AsSpan(rd, 4));
                    break;
            }
            pos += len;
        }
    }

    private static string ReadName(byte[] d, ref int pos)
    {
        var parts = new List<string>();
        int p = pos;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            int l = d[p];
            if (l == 0) { p++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (!jumped) pos = p + 2;
                jumped = true;
                p = (l & 0x3F) << 8 | d[p + 1];
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(d, p + 1, l));
            p += l + 1;
        }
        if (!jumped) pos = p;
        return string.Join('.', parts);
    }
}
