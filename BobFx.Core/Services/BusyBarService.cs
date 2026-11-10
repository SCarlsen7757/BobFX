using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace BobFx.Core.Services;

public sealed record BusyBarStatus(string State, string? ApiVersion = null, bool InputSupported = false, BusyBarSelector Selector = BusyBarSelector.Unknown);

public sealed class BusyBarService(InstallationSettingsStore settings, RgbAndSoundControlService control,
    IHttpClientFactory clients, IConfiguration configuration, TimeProvider clock, ControllerCredentials credentials,
    IBusyBarStatusSource? statusSource = null) : BackgroundService
{
    internal const int DisplayLeaseSeconds = 15;
    private readonly ConcurrentDictionary<string, BusyBarStatus> statuses = new();
    private readonly IBusyBarStatusSource source = statusSource ?? new BusyBarStatusSource();
    public IReadOnlyDictionary<string, BusyBarStatus> Statuses => new Dictionary<string, BusyBarStatus>(statuses);
    public async Task<string> TestConnectionAsync(BusyBarSettings bar, string? password = null, CancellationToken token = default)
    {
        InstallationSettingsStore.Validate(new InstallationSettings { Controllers = [bar] });
        var secret = string.IsNullOrEmpty(password) ? LoadSecret(bar) : password;
        ControllerCredentials.ValidatePassword(secret);
        using var client = clients.CreateClient("busybar");
        client.DefaultRequestHeaders.Add("X-API-Token", secret);
        var version = await client.GetFromJsonAsync<JsonElement>($"http://{bar.Address}:{bar.Port}/api/version", token);
        if (!version.TryGetProperty("api_semver", out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Device did not return a BUSY Bar API version.");
        return value.GetString()!;
    }
    private string LoadSecret(BusyBarSettings bar)
    {
        if (credentials.HasPassword(bar.Id)) return credentials.Read(bar.Id);
        var root = configuration["BusyBar:SecretsPath"] ?? "/run/secrets";
        // Path.Combine would silently discard the secrets root if SecretName were rooted.
        if (Path.IsPathRooted(bar.SecretName)) throw new InvalidDataException("Invalid secret.");
        var value = File.ReadAllText(Path.Join(root, bar.SecretName)).Trim();
        if (value.Length is < 1 or > 256 || value.Contains('\r') || value.Contains('\n')) throw new InvalidDataException("Invalid secret.");
        return value;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Dictionary<string, (BusyBarSettings Settings, long Revision, CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var enabled = settings.Read().Controllers.Where(c => c.Enabled).ToDictionary(c => c.Id);
                foreach (var (id, worker) in workers.ToArray())
                    if (!enabled.TryGetValue(id, out var updated) || updated != worker.Settings || worker.Revision != credentials.Revision(id))
                    {
                        await worker.Stop.CancelAsync(); await worker.Task; worker.Stop.Dispose(); workers.Remove(id); statuses.TryRemove(id, out _);
                    }
                foreach (var (id, bar) in enabled)
                    if (!workers.ContainsKey(id))
                    {
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        workers[id] = (bar, credentials.Revision(id), stop, RunAsync(bar, stop.Token));
                    }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: stop supervising; the finally block below cancels and awaits all workers.
            return;
        }
        finally
        {
            foreach (var worker in workers.Values) await worker.Stop.CancelAsync();
            await Task.WhenAll(workers.Values.Select(w => w.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose();
        }
    }
    private async Task RunAsync(BusyBarSettings bar, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var secret = LoadSecret(bar);
                using var client = clients.CreateClient("busybar");
                client.DefaultRequestHeaders.Add("X-API-Token", secret);
                var origin = $"http://{bar.Address}:{bar.Port}/api";
                var version = await client.GetFromJsonAsync<JsonElement>(origin + "/version", token);
                var api = version.TryGetProperty("api_semver", out var property) ? property.GetString() : null;
                // The bar accepts an animation file it cannot play and shows an error icon instead,
                // so only API versions with a reviewed native format get any output.
                if (BusyBarAnimFile.FormatFor(api) is not { } format)
                {
                    statuses[bar.Id] = new("Native playback requires API 27.5 (firmware 1.2.x) or 27.14+ (firmware 1.3+); output paused", api);
                    // Probe again later: a firmware update can move the bar onto a supported version.
                    await Task.Delay(TimeSpan.FromSeconds(30), clock, token);
                    continue;
                }
                statuses[bar.Id] = new("Waiting for selector; move to APPS to allow output", api, true);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var mode = new BusyBarModeGate();
                // Always monitor the selector, even when the START and wheel-press controls are disabled.
                await Task.WhenAll(DisplayAsync(bar, client, origin, format, mode, session.Token), InputAsync(bar, secret, mode, session.Token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            {
                // Never expose exception URLs or authentication headers to logs/UI.
                statuses[bar.Id] = new("Connection failed; check address, API access and saved device password");
                try { await Task.Delay(TimeSpan.FromSeconds(5), clock, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }
    internal async Task DisplayAsync(BusyBarSettings bar, HttpClient client, string origin, BusyBarAnimFormat format, BusyBarModeGate mode, CancellationToken token)
    {
        BusyBarScreen? previous = null;
        AnimationSettings? timing = null;
        // The countdown clip is as long as the preparation, which is the same for every game.
        var preparation = (int)CountdownService.PreCountdownDuration.TotalSeconds;
        IReadOnlyDictionary<BusyBarScreen, BusyBarAnimation> assets = new Dictionary<BusyBarScreen, BusyBarAnimation>();
        var uploaded = new HashSet<string>();
        long? lastSent = null;
        var failures = 0;
        using var uploadClient = clients.CreateClient("busybar-assets");
        uploadClient.Timeout = TimeSpan.FromSeconds(30);
        if (client.DefaultRequestHeaders.TryGetValues("X-API-Token", out var password))
            uploadClient.DefaultRequestHeaders.Add("X-API-Token", password);
        using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (mode.Selector != BusyBarSelector.Apps)
                {
                    previous = null; lastSent = null;
                    await cadence.WaitForNextTickAsync(token);
                    continue;
                }
                var updated = settings.Read().Animation;
                if (timing != updated)
                {
                    assets = BusyBarAnimations.Create(updated, format, preparation);
                    timing = updated; previous = null;
                }
                // Preload the complete small set before sampling the scene. Game transitions
                // then need only a playback command, never an animation-sized upload.
                foreach (var asset in assets.Values.Where(a => !uploaded.Contains(a.Name)))
                {
                    var sentAsset = await mode.InAppsAsync(async () =>
                    {
                        using var content = new ByteArrayContent(asset.Data);
                        content.Headers.ContentType = new("application/octet-stream");
                        using var response = await uploadClient.PostAsync(origin + "/assets/upload?application_name=BobFX&file=" + asset.Name, content, token);
                        response.EnsureSuccessStatusCode();
                    }, token);
                    if (!sentAsset) break;
                    uploaded.Add(asset.Name);
                }
                if (assets.Values.Any(a => !uploaded.Contains(a.Name)))
                {
                    previous = null; lastSent = null;
                    await cadence.WaitForNextTickAsync(token);
                    continue;
                }
                var scene = control.GetSceneFrame();
                var screen = BusyBarAnimations.Screen(scene.Phase, scene.Elapsed, timing);
                // Refresh with enough lease headroom for transient Wi-Fi delays. await_previous_end preserves native
                // playback while renewing ownership; no pixel frames are sent here. Play-once screens are
                // never refreshed: resending them would restart the clip.
                if (screen != previous || lastSent is null ||
                    (!BusyBarAnimations.PlaysOnce(screen) && clock.GetElapsedTime(lastSent.Value).TotalSeconds >= 1))
                {
                    var sent = await mode.InAppsAsync(async () =>
                    {
                        // Keep the scene alive while replacing its source. Clearing all
                        // elements first closes the firmware canvas and exposes its clock.
                        using var response = await client.PostAsJsonAsync(origin + "/display/draw", NativeDisplay(assets[screen].Name, screen, timing, preparation,
                            screen == BusyBarScreen.Countdown ? BusyBarAnimations.CountdownSection(scene.Elapsed, preparation, format) : "default"), token);
                        response.EnsureSuccessStatusCode();
                    }, token);
                    if (sent)
                    {
                        previous = screen; lastSent = clock.GetTimestamp();
                        failures = 0;
                        if (statuses.TryGetValue(bar.Id, out var status) && status.State == "Display update failed; retrying")
                            statuses.TryUpdate(bar.Id, status with { State = "APPS mode; BobFX output enabled" }, status);
                    }
                    else { previous = null; lastSent = null; }
                }
                await cadence.WaitForNextTickAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            {
                lastSent = null;
                // A missing/invalid asset may follow a reboot. Network timeouts and 5xx
                // responses do not justify re-uploading every file or dropping the scene.
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound })
                    uploaded.Clear();
                statuses[bar.Id] = statuses.GetValueOrDefault(bar.Id, new("Disconnected")) with { State = "Display update failed; retrying" };
                var retryMilliseconds = 250 * (1 << Math.Min(failures++, 3));
                try { await Task.Delay(TimeSpan.FromMilliseconds(retryMilliseconds), clock, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }
    private async Task InputAsync(BusyBarSettings bar, string secret, BusyBarModeGate mode, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                statuses[bar.Id] = statuses[bar.Id] with { State = "Waiting for selector; move away from APPS and back", Selector = BusyBarSelector.Unknown };
                long? lastPress = null;
                await foreach (var message in source.ReadAsync(bar, secret, token))
                {
                    foreach (var input in BusyBarInputDecoder.Events(message))
                    {
                        if (input.Selector is { } selector)
                        {
                            await mode.SetAsync(selector, token);
                            statuses[bar.Id] = statuses[bar.Id] with { Selector = selector, State = selector == BusyBarSelector.Apps
                                ? "APPS mode; BobFX output enabled" : "Output paused outside APPS mode" };
                            continue;
                        }
                        if (!bar.RemoteEnabled || input.Button is not { } button || mode.Selector != BusyBarSelector.Apps) continue;
                        if (lastPress is { } last && clock.GetElapsedTime(last).TotalMilliseconds < 300) continue;
                        lastPress = clock.GetTimestamp();
                        try
                        {
                            if (button == 2 && control.Phase == GamePhase.Idle) await control.StartGameAsync();
                            else if (button == 0) await control.StopGameAsync();
                        }
                        catch (InvalidOperationException) { /* Another controller owns this transition. */ }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            { statuses[bar.Id] = statuses.GetValueOrDefault(bar.Id, new("Disconnected")) with { State = "Selector stream unavailable; output paused" }; }
            finally
            {
                await mode.SetAsync(BusyBarSelector.Unknown);
                statuses[bar.Id] = statuses.GetValueOrDefault(bar.Id, new("Disconnected")) with { Selector = BusyBarSelector.Unknown, State = "Selector unknown; output paused until APPS is observed" };
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    internal static object BuildDisplay(GamePhase phase, double elapsed, double preparation, AnimationSettings animation,
        BusyBarAnimFormat format = BusyBarAnimFormat.Bicycle1)
    {
        var screen = BusyBarAnimations.Screen(phase, elapsed, animation);
        var seconds = Math.Max(1, (int)Math.Round(preparation));
        return NativeDisplay(BusyBarAnimations.Create(animation, format, seconds)[screen].Name, screen, animation, seconds,
            screen == BusyBarScreen.Countdown ? BusyBarAnimations.CountdownSection(elapsed, seconds, format) : "default");
    }
    /// <summary>One animation element and nothing else: every pixel, including the preparation seconds,
    /// comes from a file the bar plays by itself.</summary>
    private static object NativeDisplay(string asset, BusyBarScreen screen, AnimationSettings animation, int preparation, string section)
    {
        var once = BusyBarAnimations.PlaysOnce(screen);
        return new
        {
            application_name = "BobFX", priority = 50,
            elements = new object[]
            {
                new { id = "scene", type = "animation", display = "front", x = 0, y = 0,
                    path = asset, section, loop = !once,
                    // A finite clip plays exactly once. Queuing it behind itself would play it
                    // again when it ends, for example as a fourth flash.
                    await_previous_end = !once,
                    timeout = once ? (int)Math.Ceiling(BusyBarAnimations.Seconds(screen, animation, preparation)) + 1 : DisplayLeaseSeconds, z_index = 0 }
            }
        };
    }
}
/// <summary>Reads only the input subset of BSB_State.State (official busylib schema).
/// Unknown protobuf fields are skipped, never interpreted as game commands.</summary>
public static class BusyBarInputDecoder
{
    public static IReadOnlyList<int> Presses(byte[] message) => Events(message).Where(e => e.Button.HasValue).Select(e => e.Button!.Value).ToArray();
    public static IReadOnlyList<BusyBarInputEvent> Events(byte[] message)
    {
        var events = new List<BusyBarInputEvent>();
        foreach (var update in Fields(message).Where(f => f.Number == 2 && f.Wire == 2))
            foreach (var input in Fields(update.Data).Where(f => f.Number == 11 && f.Wire == 2))
            {
                // InputEvent is a protobuf oneof: the last recognized member wins.
                var selected = Fields(input.Data).LastOrDefault(f => f.Wire == 2 && f.Number is 1 or 2 or 3);
                if (selected?.Number == 2)
                {
                    var position = Fields(selected.Data).LastOrDefault(f => f.Number == 1 && f.Wire == 0)?.Value ?? 0;
                    events.Add(new(null, position <= 4 ? (BusyBarSelector)position : BusyBarSelector.Unknown));
                }
                else if (selected?.Number == 1)
                {
                    var fields = Fields(selected.Data);
                    var key = fields.LastOrDefault(f => f.Number == 1 && f.Wire == 0)?.Value ?? 0;
                    var action = fields.LastOrDefault(f => f.Number == 2 && f.Wire == 0)?.Value ?? 0;
                    // Button: OK 0 (pressing the wheel), BACK 1, START 2. BACK is left to the bar: in APPS it
                    // closes the screen and returns to the app list, so it cannot double as a game control.
                    if (action == 0 && key is 0 or 2) events.Add(new((int)key, null));
                }
            }
        return events;
    }
    private sealed record Field(int Number, int Wire, ulong Value, byte[] Data);
    private static List<Field> Fields(byte[] message)
    {
        var result = new List<Field>(); var offset = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (offset >= message.Length) throw new InvalidDataException("Truncated protobuf.");
                var b = message[offset++];
                if (shift == 63 && b > 1) throw new InvalidDataException("Invalid varint.");
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid varint.");
        }
        while (offset < message.Length)
        {
            var tag = Varint(); var number = (int)(tag >> 3); var wire = (int)(tag & 7);
            if (number < 1) throw new InvalidDataException("Invalid field.");
            if (wire == 0) { result.Add(new(number, wire, Varint(), [])); continue; }
            var length = wire switch { 1 => 8, 5 => 4, 2 => checked((int)Varint()), _ => throw new InvalidDataException("Invalid wire type.") };
            if (length < 0 || length > message.Length - offset) throw new InvalidDataException("Truncated field.");
            result.Add(new(number, wire, 0, wire == 2 ? message.AsSpan(offset, length).ToArray() : [])); offset += length;
        }
        return result;
    }
}
public sealed record BusyBarInputEvent(int? Button, BusyBarSelector? Selector);
