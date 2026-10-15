using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace BobFx.Core.Services;

public sealed record BusyBarStatus(string State, string? ApiVersion = null, bool InputSupported = false, BusyBarSelector Selector = BusyBarSelector.Unknown);

public sealed class BusyBarService(InstallationSettingsStore settings, RgbAndSoundControlService control,
    CountdownService countdown, IHttpClientFactory clients, IConfiguration configuration, TimeProvider clock, ControllerCredentials credentials,
    IBusyBarStatusSource? statusSource = null) : BackgroundService
{
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
        var value = File.ReadAllText(Path.Combine(root, bar.SecretName)).Trim();
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
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
                var supported = Version.TryParse(api, out var parsed) && parsed.Major == 27;
                statuses[bar.Id] = new(supported ? "Waiting for selector; move to APPS to allow output" : "Selector API unsupported; output paused", api, supported);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var mode = new BusyBarModeGate();
                // Always monitor the selector, even when START/BACK controls are disabled.
                await Task.WhenAll(DisplayAsync(bar, client, origin, mode, session.Token),
                    supported ? InputAsync(bar, secret, mode, session.Token) : Task.CompletedTask);
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
    internal async Task DisplayAsync(BusyBarSettings bar, HttpClient client, string origin, BusyBarModeGate mode, CancellationToken token)
    {
        GamePhase? previous = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var scene = control.GetSceneFrame();
                var phase = scene.Phase;
                if (previous != phase)
                {
                    var cleared = await mode.InAppsAsync(async () =>
                    {
                        using var response = await client.DeleteAsync(origin + "/display/draw?application_name=BobFX", token);
                        response.EnsureSuccessStatusCode();
                    }, token);
                    if (cleared) previous = phase;
                }
                var sent = await mode.InAppsAsync(async () =>
                {
                    var payload = BuildDisplay(bar.ShowTimer, phase, scene.Elapsed, scene.Preparation,
                        countdown.Remaining, settings.Read().Animation);
                    using var response = await client.PostAsJsonAsync(origin + "/display/draw", payload, token);
                    response.EnsureSuccessStatusCode();
                }, token);
                if (!sent) previous = null;
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            {
                statuses[bar.Id] = statuses.GetValueOrDefault(bar.Id, new("Disconnected")) with { State = "Display update failed; retrying" };
                try { await Task.Delay(TimeSpan.FromSeconds(5), clock, token); }
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
                            else if (button == 1) await control.StopGameAsync();
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
    public static object BuildDisplay(bool showTimer, GamePhase phase, double elapsed, double preparation, TimeSpan remaining, AnimationSettings animation)
    {
        var elements = new List<object>();
        object Rectangle(string id, int x, int y, int width, int height, string color) => new
        { id, type = "rectangle", display = "front", x, y, width, height, fill = "solid", fill_colors = new[] { color }, border_width = 0, timeout = 2 };
        elements.Add(Rectangle("background", 0, 0, 72, 16, "#000000FF"));
        if (showTimer && phase == GamePhase.Running)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            elements.Add(new { id = "timer", type = "text", display = "front", x = 36, y = 8, align = "center", width = 72,
                text = $"{seconds / 60}:{seconds % 60:00}", font = "normal", color = "#00FF00FF", timeout = 2 });
        }
        else if (phase == GamePhase.Ending)
        {
            if (elapsed < animation.FlashSeconds)
            {
                if ((int)(elapsed / animation.FlashSeconds * 6) % 2 == 0) elements.Add(Rectangle("flash", 0, 0, 72, 16, "#FF0000FF"));
            }
            else
            {
                var p = (int)((elapsed - animation.FlashSeconds) * animation.DotLapsPerSecond * 172) % 172;
                var x = p < 72 ? p : p < 86 ? 71 : p < 158 ? 157 - p : 0;
                var y = p < 72 ? 0 : p < 86 ? p - 71 : p < 158 ? 15 : 172 - p;
                elements.Add(Rectangle("dot", x, y, 1, 1, "#FF0000FF"));
            }
        }
        else if (phase == GamePhase.PreCountdown)
        {
            var progress = preparation > 0 ? elapsed / preparation : 1;
            if (progress < 0.6) elements.Add(Rectangle("scanner", Math.Clamp((int)(progress / 0.6 * 68), 0, 68), 0, 4, 16, "#0000FFFF"));
            else
            {
                elements.Add(Rectangle("top", 0, 0, 72, 2, "#0000FFFF")); elements.Add(Rectangle("bottom", 0, 14, 72, 2, "#0000FFFF"));
                if (progress >= 0.8) { elements.Add(Rectangle("left", 0, 0, 2, 16, "#0000FFFF")); elements.Add(Rectangle("right", 70, 0, 2, 16, "#0000FFFF")); }
            }
        }
        else if (phase == GamePhase.Running && elapsed < 0.75) elements.Add(Rectangle("go", 0, 0, 72, 16, "#00FF00FF"));
        else
            for (var i = 0; i < 12; i++)
            {
                var wave = (Math.Sin(i * 0.5 - elapsed * 3) + 1) / 2;
                var pulse = Math.Pow(Math.Max(0, Math.Cos(i * 0.2 - elapsed * 4)), 12);
                var r = (int)(255 * pulse); var g = (int)(255 * Math.Min(1, 0.15 + 0.6 * wave + 0.25 * pulse)); var b = (int)(255 * Math.Min(1, 0.25 + 0.6 * (1 - wave) + 0.15 * pulse));
                elements.Add(Rectangle("wave" + i, i * 6, 0, 6, 16, $"#{r:X2}{g:X2}{b:X2}FF"));
            }
        return new { application_name = "BobFX", priority = 50, elements };
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
                    if (action == 0 && key is 1 or 2) events.Add(new((int)key, null));
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
