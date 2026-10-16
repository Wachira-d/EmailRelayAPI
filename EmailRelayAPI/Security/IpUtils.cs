using System.Net;
using System.Net.Sockets;

namespace EmailRelayAPI.Security;

public static class IpUtils
{
    public static IPAddress? Normalize(IPAddress? ip)
    {
        if (ip == null) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    public static bool TryParseCidr(string value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(value, out var single)) return false;
            network = Normalize(single)!;
            prefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return true;
        }

        if (!IPAddress.TryParse(value[..slash], out var net)) return false;
        if (!int.TryParse(value[(slash + 1)..], out prefix)) return false;
        network = Normalize(net)!;
        var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > max) return false;
        return true;
    }

    public static bool IsInRange(IPAddress address, IPAddress network, int prefix)
    {
        address = Normalize(address)!;
        if (address.AddressFamily != network.AddressFamily) return false;

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i]) return false;
        }

        if (remBits > 0 && fullBytes < a.Length)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            if ((a[fullBytes] & mask) != (n[fullBytes] & mask)) return false;
        }

        return true;
    }

    /// <summary>True when the address matches any entry (IP or CIDR) in the list. Invalid entries are ignored.</summary>
    public static bool MatchesAny(IPAddress address, IEnumerable<string> ranges)
    {
        foreach (var r in ranges)
        {
            if (TryParseCidr(r, out var net, out var prefix) && IsInRange(address, net, prefix))
                return true;
        }
        return false;
    }

    public static List<string> SplitList(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Normalize(address)!);
}
