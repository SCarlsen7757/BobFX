using System.Net;
namespace BobFx.Core.Services;
public sealed class SecurityOptions
{
    public string[] AllowedClientNetworks { get; set; } = [];
    public bool IsValid() => AllowedClientNetworks.Length > 0 && AllowedClientNetworks.All(value => IPNetwork.TryParse(value, out _));
    public bool Allows(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return AllowedClientNetworks.Any(value => IPNetwork.TryParse(value, out var network) && network.Contains(address));
    }
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        if (string.IsNullOrEmpty(origin)) return true; // Trusted LAN CLI/device callers do not send Origin.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo) &&
            string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate($"{request.Scheme}://{request.Host.Value}", UriKind.Absolute, out var target) &&
            string.Equals(uri.Host, target.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == target.Port;
    }
}
