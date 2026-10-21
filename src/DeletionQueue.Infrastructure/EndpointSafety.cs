using System.Net;

namespace DeletionQueue.Infrastructure;

public static class EndpointSafety
{
    // Configured LAN/loopback Seerr is supported. Cloud metadata, multicast, scoped and unspecified endpoints are not.
    public static bool Allowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || (bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254));
    }
}
