using System.Net.Http.Json;
using System.Text.Json;

namespace BobFx.Core.Services;

/// <summary>
/// Sets up WLED's own sync between controllers for the time BobFX sends no frames: the master transmits its
/// effect and the followers receive it, so the idle boards show one picture. WLED keeps the sync going by
/// itself afterwards; this only configures it and brings the followers to the master's current state.
/// </summary>
public sealed class WLedIdleSync(InstallationSettingsStore settings, WLedDiscoveryService discovery, IHttpClientFactory http)
{
    // What a follower copies from the master's main segment. Bounds are left out: a follower keeps its own LEDs.
    private static readonly string[] EffectKeys = ["fx", "sx", "ix", "pal", "col", "c1", "c2", "c3", "o1", "o2", "o3"];
    private static readonly string[] PowerKeys = ["on", "bri"];
    private IdleSyncSettings? applied;

    /// <summary>Applies the saved idle sync to its controllers.</summary>
    /// <param name="report">Told what went wrong for a controller, or null once it is in order.</param>
    /// <param name="configure">False to only bring the followers to the master's state, without writing the
    /// sync configuration to the controllers' flash again, as after every game.</param>
    public async Task ApplyAsync(Action<string, string?> report, bool configure, CancellationToken token)
    {
        var saved = settings.Snapshot;
        var sync = saved.IdleSync;
        DeviceSettings? Device(string mac) => saved.Devices.FirstOrDefault(d => d.Mac == mac);
        if (configure)
            // A member that left the sync is told to stop; a simulated or removed one has nothing to tell.
            foreach (var device in Members(applied).Except(Members(sync)).Select(Device).OfType<DeviceSettings>().Where(device => !device.Simulated))
                if (!await TryAsync(() => DisableAsync(discovery.Resolve(device), token), token))
                    report(device.Mac, "Previous idle sync member could not be disabled.");
        applied = sync;
        if (!sync.Enabled) return;

        var mask = 1 << (sync.Group - 1);
        var master = discovery.Resolve(Device(sync.MasterMac!)!);
        var idleState = new Dictionary<string, object?> { ["live"] = false };
        // A simulated controller has no WLED to ask or configure; its followers get the plain idle state.
        var masterRead = !master.Simulated && await TryAsync(async () => await ReadIdleStateAsync(master, idleState, token), token, alsoInvalidJson: true);
        if (!master.Simulated && !masterRead) report(master.Mac, "Master idle state could not be read.");

        foreach (var mac in Members(sync))
        {
            var device = Device(mac)!;
            if (device.Simulated) { report(mac, null); continue; }
            if (discovery.KnownOffline(device)) continue;
            var isMaster = mac == sync.MasterMac;
            if (!await TryAsync(() => ApplyToAsync(discovery.Resolve(device), isMaster, mask, idleState, configure, token), token))
                report(mac, "Idle sync could not be applied.");
            else if (!isMaster || masterRead) report(mac, null);
        }
        // WLED emits subsequent native effect/playlist changes through its notifier. Mirroring current parameters
        // gets followers into the right state immediately without toggling the master's power or restarting its playlist.
    }

    private static IEnumerable<string> Members(IdleSyncSettings? sync) => sync is { Enabled: true, MasterMac: { } master } ? sync.Followers.Prepend(master) : [];

    /// <summary>Runs a request to a controller. False when the controller did not answer or refused; a cancelled
    /// operation is not a controller failure and goes on up.</summary>
    private static async Task<bool> TryAsync(Func<Task> request, CancellationToken token, bool alsoInvalidJson = false)
    {
        try { await request(); return true; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException || (alsoInvalidJson && ex is JsonException)) && !token.IsCancellationRequested)
        { return false; }
    }

    private async Task PostAsync(DeviceSettings device, string path, object body, CancellationToken token)
    {
        using var client = http.CreateClient("wled");
        using var response = await client.PostAsJsonAsync($"http://{device.Address}:{device.HttpPort}{path}", body, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task DisableAsync(DeviceSettings device, CancellationToken token)
    {
        await PostAsync(device, "/json/cfg", new { @if = new { sync = new { send = new { en = false, dir = false, grp = 0 }, recv = new { grp = 0 } } } }, token);
        await PostAsync(device, "/json/state", new { udpn = new { send = false, recv = false } }, token);
    }

    /// <summary>Adds the master's power, brightness and the effect of its main segment to <paramref name="idleState"/>.</summary>
    private async Task ReadIdleStateAsync(DeviceSettings master, Dictionary<string, object?> idleState, CancellationToken token)
    {
        using var client = http.CreateClient("wled");
        using var response = await client.GetAsync($"http://{master.Address}:{master.HttpPort}/json/state", token);
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        if (state.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid WLED state.");
        Copy(state, PowerKeys, idleState);
        if (!state.TryGetProperty("seg", out var segments) || segments.ValueKind != JsonValueKind.Array) return;
        var main = state.TryGetProperty("mainseg", out var id) ? id.GetInt32() : 0;
        var segment = segments.EnumerateArray().FirstOrDefault(seg => seg.TryGetProperty("id", out var index) && index.GetInt32() == main);
        if (segment.ValueKind != JsonValueKind.Object) return;
        var effect = new Dictionary<string, object?>();
        Copy(segment, EffectKeys, effect);
        idleState["seg"] = effect;
    }
    /// <summary>Copies the properties the controller reported; one it left out is left out.</summary>
    private static void Copy(JsonElement from, string[] keys, Dictionary<string, object?> to)
    {
        foreach (var key in keys.Where(key => from.TryGetProperty(key, out _))) to[key] = from.GetProperty(key).Clone();
    }

    private async Task ApplyToAsync(DeviceSettings device, bool master, int mask, Dictionary<string, object?> idleState, bool configure, CancellationToken token)
    {
        // Persist native sync behavior; master transmits, followers only receive.
        if (configure)
            await PostAsync(device, "/json/cfg", new
            {
                @if = new { sync = new { send = new { en = master, dir = master, grp = master ? mask : 0 }, recv = new { grp = master ? 0 : mask, bri = true, col = true, fx = true, pal = true } } }
            }, token);
        // The master is never switched on or changed; a follower takes over what the master shows.
        var payload = master ? new Dictionary<string, object?> { ["live"] = false } : new Dictionary<string, object?>(idleState);
        payload["udpn"] = new { send = master, sgrp = master ? mask : 0, rgrp = master ? 0 : mask };
        await PostAsync(device, "/json/state", payload, token);
    }
}
