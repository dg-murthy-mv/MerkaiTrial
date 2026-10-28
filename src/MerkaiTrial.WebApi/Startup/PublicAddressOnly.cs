// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/PublicAddressOnly.cs
//
// NEW FILE (106 — VUL-29). An HttpClient handler that will only CONNECT to
// public internet addresses.
//
// Used by the client that downloads received-email attachments. Their
// links come from Resend's answer, so they should always be Resend's own
// storage — but the server follows them, and a link that pointed at
// 127.0.0.1, 10.x, 192.168.x, 169.254.169.254 (the cloud's own metadata
// service) or any other inside address would make this server fetch
// something on its own network for whoever wrote the link (SSRF).
//
// The check is made on the address actually connected to, after DNS, so
// a public-looking name that resolves to an inside address is refused too.
// =====================================================================

using System.Net;
using System.Net.Sockets;

namespace MerkaiTrial.WebApi.Startup;

public static class PublicAddressOnly
{
    public static SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException(
                    $"Refused to connect to {context.DnsEndPoint.Host}: it is not a public internet address.");

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>False for loopback, private, link-local, carrier-grade NAT, multicast and other non-internet ranges.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0
                  || b[0] == 10
                  || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // carrier-grade NAT
                  || b[0] == 127
                  || (b[0] == 169 && b[1] == 254)                    // link-local, cloud metadata
                  || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                  || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                  || (b[0] == 192 && b[1] == 168)
                  || (b[0] == 198 && (b[1] == 18 || b[1] == 19))     // benchmarking
                  || b[0] >= 224);                                   // multicast, reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None)) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                 // fc00::/7 unique local
            return true;
        }

        return false;
    }
}
