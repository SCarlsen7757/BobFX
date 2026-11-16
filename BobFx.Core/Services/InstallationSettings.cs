using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;

namespace BobFx.Core.Services;

/// <summary>Everything the operator sets up. An instance is never changed: a save makes a new one, so whoever
/// holds one can keep reading it, and two reads that return the same instance saw the same settings.</summary>
public sealed record InstallationSettings
{
    /// <summary>Version 2 maps each board as a left and a right strip; version 1 files are migrated on load.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<DeviceSettings> Devices { get; init; } = [];
    public IReadOnlyList<BoardSettings> Boards { get; init; } = [];
    public IdleSyncSettings IdleSync { get; init; } = new();
    public AnimationSettings Animation { get; init; } = new();
    public LightShowSettings LightShow { get; init; } = new();
    public IReadOnlyList<BusyBarSettings> Controllers { get; init; } = [];
    /// <summary>Saved game length. Null until saved from the UI, so the Countdown configuration supplies the first-run values.</summary>
    public CountdownSettings? Countdown { get; init; }
}
public sealed record CountdownSettings(int DurationSeconds, int DeviationSeconds)
{
    public bool IsValid() => DurationSeconds is >= 1 and <= 86400 && DeviationSeconds >= 0 && DeviationSeconds < DurationSeconds;
}
/// <summary>A WLED controller. A simulated one has no network side: its frames go to the live view only.</summary>
public sealed record DeviceSettings(string Mac, string Name, string Address, int HttpPort, int LedCount, int UdpPort, bool Selected = true, bool Simulated = false);
/// <summary>One LED strip along a side of a board. Its first LED is at the near end unless <see cref="Reversed"/>.</summary>
public sealed record StripSettings(int Start, int Count, bool Reversed = false);
public sealed record BoardSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Board";
    public string? DeviceMac { get; init; }
    // A board has a strip down its left side and one down its right, as seen from the near end. Both are null until mapped.
    public StripSettings? Left { get; init; }
    public StripSettings? Right { get; init; }
}
/// <summary>How a controller's LEDs lie on a board: two strips of <see cref="LedsPerStrip"/>, left then right.</summary>
public static class BoardMapping
{
    public const int LedsPerStrip = 51, LedsPerBoard = LedsPerStrip * 2;
    public const int Left = 0, Right = 1;
    /// <summary>The first half of the controller's LEDs on the left strip and the second half on the right, both
    /// starting at the near end. An odd LED at the end stays dark. Null when there are too few LEDs for two strips.</summary>
    public static (StripSettings Left, StripSettings Right)? Default(int ledCount)
    {
        var half = ledCount / 2;
        return half < 1 ? null : (new(0, half), new(half, half));
    }
    public static BoardSettings WithDefault(BoardSettings board, int ledCount) =>
        Default(ledCount) is { } strips ? board with { Left = strips.Left, Right = strips.Right } : board with { Left = null, Right = null };
    public static StripSettings? Strip(BoardSettings board, int side) => side == Left ? board.Left : board.Right;
    public static bool Fits(BoardSettings board, int ledCount) =>
        new[] { board.Left, board.Right }.All(strip => strip is null || (long)strip.Start + strip.Count <= ledCount);
    /// <summary>LED indices of one strip in the order they lie on the board, from the near end to the far end.</summary>
    public static int[] StripPixels(BoardSettings board, int side)
    {
        if (Strip(board, side) is not { } strip) return [];
        var pixels = Enumerable.Range(strip.Start, strip.Count);
        return (strip.Reversed ? pixels.Reverse() : pixels).ToArray();
    }
}
public sealed record IdleSyncSettings
{
    public bool Enabled { get; init; }
    public string? MasterMac { get; init; }
    public IReadOnlyList<string> Followers { get; init; } = [];
    public int Group { get; init; } = 1;
}
public sealed record AnimationSettings
{
    public int FramesPerSecond { get; init; } = 30;
    public double FlashSeconds { get; init; } = 2;
    public double DotSeconds { get; init; } = 3;
    public double DotLapsPerSecond { get; init; } = 4;
}
/// <summary>Look of the board light show. Kept apart from <see cref="AnimationSettings"/>, whose
/// values also time the BUSY Bar clips.</summary>
public sealed record LightShowSettings
{
    public string Theme { get; init; } = "neonSunset";
    public LightShowColors Custom { get; init; } = new();
    public int Brightness { get; init; } = 100;
    /// <summary>Drift speed of the playing phase in percent; the big moments stay clock-locked.</summary>
    public int Speed { get; init; } = 100;
    public string Intensity { get; init; } = "calm";
}
public sealed record LightShowColors
{
    public string Primary { get; init; } = "#ff4fad";
    public string Secondary { get; init; } = "#ffad66";
    public string Tertiary { get; init; } = "#8c50ff";
    public string Accent { get; init; } = "#96eddf";
}
public sealed record BusyBarSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "BUSY Bar";
    public string Address { get; init; } = "";
    public int Port { get; init; } = 80;
    public bool Enabled { get; init; }
    public bool RemoteEnabled { get; init; }
    public string SecretName { get; init; } = "busybar_token";
}

public sealed class InstallationSettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private InstallationSettings current;
    public string DirectoryPath { get; }
    public InstallationSettingsStore(IConfiguration configuration)
    {
        DirectoryPath = Path.GetFullPath(configuration["Settings:DataPath"] ?? Path.Join(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Join(DirectoryPath, "settings.json");
        current = Migrate(File.Exists(path) ? JsonSerializer.Deserialize<InstallationSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Settings file is empty.") : new());
        Validate(current);
        current = Own(current);
    }
    /// <summary>The settings as last saved. The instance is replaced by a save and never changed.</summary>
    public InstallationSettings Snapshot => Volatile.Read(ref current);
    /// <summary>Copies the lists, so that the caller who built them cannot change saved settings afterwards.</summary>
    private static InstallationSettings Own(InstallationSettings value) => value with
    {
        Devices = [.. value.Devices], Boards = [.. value.Boards], Controllers = [.. value.Controllers],
        IdleSync = value.IdleSync with { Followers = [.. value.IdleSync.Followers] }
    };
    public Task SaveAsync(InstallationSettings value, CancellationToken token = default) => UpdateAsync(_ => value, token);
    public async Task UpdateAsync(Func<InstallationSettings, InstallationSettings> update, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        var temporary = Path.Join(DirectoryPath, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var value = Migrate(update(Snapshot));
            Validate(value);
            value = Own(value);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, Path.Join(DirectoryPath, "settings.json"), true);
            Volatile.Write(ref current, value);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            gate.Release();
        }
    }
    /// <summary>Brings version 1 settings up to date. They mapped a board as four edges, which cannot be translated
    /// to two side strips, so a board without strips gets the default mapping for its controller. A board whose
    /// controller is unknown or too small becomes unassigned rather than failing the load.</summary>
    public static InstallationSettings Migrate(InstallationSettings value)
    {
        if (value.Version != 1 || value.Boards is null || value.Devices is null) return value;
        return value with
        {
            Version = InstallationSettings.CurrentVersion,
            Boards = value.Boards.Select(board =>
            {
                if (board?.DeviceMac is null || (board.Left is not null && board.Right is not null)) return board!;
                var device = value.Devices.FirstOrDefault(d => d?.Mac == board.DeviceMac);
                var mapped = device is null ? board : BoardMapping.WithDefault(board, device.LedCount);
                return mapped.Left is null ? mapped with { DeviceMac = null } : mapped;
            }).ToList()
        };
    }
    public static void Validate(InstallationSettings value)
    {
        if (value.Version != InstallationSettings.CurrentVersion) throw new ArgumentException("Unsupported settings version; restore a compatible backup.");
        if (value.Devices is null || value.Boards is null || value.Controllers is null || value.Animation is null || value.IdleSync is null || value.LightShow is null)
            throw new ArgumentException("Settings sections cannot be null.");
        if (value.Devices.Any(d => d is null) || value.Boards.Any(b => b is null) || value.Controllers.Any(c => c is null))
            throw new ArgumentException("Settings entries cannot be null.");
        var devices = new Dictionary<string, DeviceSettings>();
        foreach (var d in value.Devices)
        {
            if (!ValidMac(d.Mac) || !devices.TryAdd(d.Mac, d) || !LocalAddress(d.Address) || d.HttpPort is < 1 or > 65535 || d.UdpPort is < 1 or > 65535 || d.LedCount is < 1 or > 65535 || string.IsNullOrWhiteSpace(d.Name))
                throw new ArgumentException("Devices require unique lowercase MAC identities, a private IPv4 address, valid ports and 1–65535 LEDs.");
        }
        var assigned = value.Boards.Where(b => b.DeviceMac is not null).ToList();
        if (value.Boards.Select(b => b.Id).Distinct().Count() != value.Boards.Count || assigned.Select(b => b.DeviceMac).Distinct().Count() != assigned.Count)
            throw new ArgumentException("Each board needs a unique ID and WLED device.");
        foreach (var b in value.Boards)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.Name)) throw new ArgumentException("Boards require an ID and a name.");
            DeviceSettings? d = null;
            if (b.DeviceMac is not null && (!devices.TryGetValue(b.DeviceMac, out d) || !d.Selected))
                throw new ArgumentException("Assigned boards require a selected WLED device.");
            if (b.DeviceMac is not null && (b.Left is null || b.Right is null))
                throw new ArgumentException($"{b.Name}: map both the left and the right strip.");
            foreach (var (side, strip) in new[] { ("left", b.Left), ("right", b.Right) })
            {
                if (strip is null) continue;
                if (strip.Start < 0 || strip.Count < 1 || (long)strip.Start + strip.Count > 65535)
                    throw new ArgumentException($"{b.Name}: the {side} strip needs a first LED of 0 or more and at least one LED.");
                if (d is not null && strip.Start + strip.Count > d.LedCount)
                    throw new ArgumentException($"{b.Name}: the {side} strip uses LEDs {strip.Start}–{strip.Start + strip.Count - 1}, but {d.Name} has {d.LedCount} LEDs (0–{d.LedCount - 1}).");
            }
            if (b.Left is { } left && b.Right is { } right && left.Start < right.Start + right.Count && right.Start < left.Start + left.Count)
                throw new ArgumentException($"{b.Name}: the left and right strips use the same LEDs.");
        }
        var idle = value.IdleSync;
        if (idle.Followers is null || idle.Group is < 1 or > 8 || idle.Followers.Distinct().Count() != idle.Followers.Count || idle.Followers.Contains(idle.MasterMac ?? "") ||
            (idle.Enabled && (idle.MasterMac is null || !devices.ContainsKey(idle.MasterMac))) || idle.Followers.Any(mac => !devices.ContainsKey(mac)))
            throw new ArgumentException("Idle sync requires a registered master, distinct registered followers and group 1–8.");
        if (idle.Enabled && idle.Followers.Any(mac => devices[mac].UdpPort != devices[idle.MasterMac!].UdpPort))
            throw new ArgumentException("Idle sync devices must use the same WLED UDP port. Set their ports in WLED first.");
        var a = value.Animation;
        if (a.FramesPerSecond is < 10 or > 60 || !double.IsFinite(a.FlashSeconds) || a.FlashSeconds is < 1 or > 10 || !double.IsFinite(a.DotSeconds) || a.DotSeconds is < 1 or > 30 || !double.IsFinite(a.DotLapsPerSecond) || a.DotLapsPerSecond is < 1 or > 20)
            throw new ArgumentException("Animation timings or frame rate are outside supported limits.");
        var show = value.LightShow;
        if (!BoardLightShow.KnownTheme(show.Theme) || BoardLightShow.Level(show.Intensity) is null ||
            show.Brightness is < 20 or > 100 || show.Speed is < 25 or > 200 || show.Custom is null ||
            !ValidColor(show.Custom.Primary) || !ValidColor(show.Custom.Secondary) || !ValidColor(show.Custom.Tertiary) || !ValidColor(show.Custom.Accent))
            throw new ArgumentException("Light show needs a known theme and intensity, brightness 20–100%, speed 25–200% and #RRGGBB colors that are not black.");
        if (value.Countdown is { } countdown && !countdown.IsValid())
            throw new ArgumentException("Countdown requires 1–86400s duration and a deviation smaller than the duration.");
        if (value.Controllers.Select(c => c.Id).Distinct().Count() != value.Controllers.Count) throw new ArgumentException("Controller IDs must be unique.");
        foreach (var c in value.Controllers)
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name) || !LocalAddress(c.Address) || c.Port is < 1 or > 65535 || !Regex.IsMatch(c.SecretName ?? "", "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Controllers require a private IPv4 address, valid port and a Docker secret name.");
    }
    public static bool LocalAddress(string address) => IPAddress.TryParse(address, out var ip) && WLedDiscoveryService.IsLocalDeviceAddress(ip);
    /// <summary>#RRGGBB with at least one channel bright enough to light a board.</summary>
    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit) &&
        Enumerable.Range(0, 3).Max(i => Convert.ToInt32(color.Substring(1 + i * 2, 2), 16)) >= 0x30;
    public static bool ValidMac(string mac) => mac is { Length: 12 } && mac.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
}
