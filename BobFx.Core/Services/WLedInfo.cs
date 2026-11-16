using System.Text.Json;
using BobFx.Core.Models.WLED;

namespace BobFx.Core.Services;

/// <summary>Asking a device who it is. Discovery and manual registration both start here, so a device is read
/// and recognised the same way however it was found.</summary>
internal static class WLedInfo
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads <c>/json/info</c>. The answer is read with a size and time limit: the address may be any
    /// device on the LAN, not necessarily a WLED controller.</summary>
    /// <exception cref="HttpRequestException">The device refused or could not be reached.</exception>
    /// <exception cref="InvalidDataException">The answer is larger than a WLED controller's.</exception>
    /// <exception cref="JsonException">The answer is not JSON.</exception>
    public static async Task<Info?> ReadAsync(HttpClient client, string address, int port, CancellationToken token)
    {
        using var response = await client.GetAsync($"http://{address}:{port}/json/info", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("Device response is too large.");
        // Reading only the headers first means the client's timeout does not cover the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ReadTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("Device response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return JsonSerializer.Deserialize<Info>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json);
    }

    /// <summary>Whether the answer is a WLED controller's, and the MAC it is known by: twelve lowercase hex digits.</summary>
    public static bool TryIdentify(Info? info, out string mac)
    {
        mac = info?.Mac?.Replace(":", "").ToLowerInvariant() ?? "";
        return info?.Brand == "WLED" && InstallationSettingsStore.ValidMac(mac);
    }
}
