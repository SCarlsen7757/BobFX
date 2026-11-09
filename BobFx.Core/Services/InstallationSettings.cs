using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;

namespace BobFx.Core.Services;

public sealed record InstallationSettings
{
    public int Version { get; init; } = 1;
    public List<DeviceSettings> Devices { get; init; } = [];
    public List<BoardSettings> Boards { get; init; } = [];
    public IdleSyncSettings IdleSync { get; init; } = new();
    public AnimationSettings Animation { get; init; } = new();
    public List<BusyBarSettings> Controllers { get; init; } = [];
}
public sealed record DeviceSettings(string Mac, string Name, string Address, int HttpPort, int LedCount, int UdpPort, bool Selected = true);
public sealed record EdgeSettings(int Start, int Count, bool Reversed = false);
public sealed record BoardSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Board";
    public string DeviceMac { get; init; } = "";
    public int Rotation { get; init; }
    // Physical edges clockwise before rotation. Top/bottom in the preview face the players.
    public List<EdgeSettings> Edges { get; init; } = [];
}
public sealed record IdleSyncSettings
{
    public bool Enabled { get; init; }
    public string? MasterMac { get; init; }
    public List<string> Followers { get; init; } = [];
    public int Group { get; init; } = 1;
}
public sealed record AnimationSettings
{
    public int FramesPerSecond { get; init; } = 30;
    public double FlashSeconds { get; init; } = 2;
    public double DotSeconds { get; init; } = 3;
    public double DotLapsPerSecond { get; init; } = 4;
}
public sealed record BusyBarSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "BUSY Bar";
    public string Address { get; init; } = "";
    public int Port { get; init; } = 80;
    public bool Enabled { get; init; }
    public bool ShowTimer { get; init; }
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
        DirectoryPath = Path.GetFullPath(configuration["Settings:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        current = File.Exists(path) ? JsonSerializer.Deserialize<InstallationSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Settings file is empty.") : new();
        Validate(current);
    }
    public InstallationSettings Snapshot => Clone(Volatile.Read(ref current));
    internal InstallationSettings Read() => Volatile.Read(ref current);
    private static InstallationSettings Clone(InstallationSettings value) => JsonSerializer.Deserialize<InstallationSettings>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
    public Task SaveAsync(InstallationSettings value, CancellationToken token = default) => UpdateAsync(_ => value, token);
    public async Task UpdateAsync(Func<InstallationSettings, InstallationSettings> update, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        var temporary = Path.Combine(DirectoryPath, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var value = Clone(update(Snapshot));
            Validate(value);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(DirectoryPath, "settings.json"), true);
            Volatile.Write(ref current, value);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    public static void Validate(InstallationSettings value)
    {
        if (value.Version != 1) throw new ArgumentException("Unsupported settings version; restore a compatible backup.");
        if (value.Devices is null || value.Boards is null || value.Controllers is null || value.Animation is null || value.IdleSync is null)
            throw new ArgumentException("Settings sections cannot be null.");
        if (value.Devices.Any(d => d is null) || value.Boards.Any(b => b is null) || value.Controllers.Any(c => c is null))
            throw new ArgumentException("Settings entries cannot be null.");
        var devices = new Dictionary<string, DeviceSettings>();
        foreach (var d in value.Devices)
        {
            if (!ValidMac(d.Mac) || !devices.TryAdd(d.Mac, d) || !LocalAddress(d.Address) || d.HttpPort is < 1 or > 65535 || d.UdpPort is < 1 or > 65535 || d.LedCount is < 1 or > 65535 || string.IsNullOrWhiteSpace(d.Name))
                throw new ArgumentException("Devices require unique lowercase MAC identities, a private IPv4 address, valid ports and 1–65535 LEDs.");
        }
        if (value.Boards.Select(b => b.Id).Distinct().Count() != value.Boards.Count || value.Boards.Select(b => b.DeviceMac).Distinct().Count() != value.Boards.Count)
            throw new ArgumentException("Each board needs a unique ID and WLED device.");
        foreach (var b in value.Boards)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.Name) || !devices.TryGetValue(b.DeviceMac, out var d) || !d.Selected || b.Rotation is not (0 or 90 or 180 or 270) || b.Edges is null || b.Edges.Count != 4)
                throw new ArgumentException("Boards require a selected device, four edges and rotation 0/90/180/270.");
            var used = new HashSet<int>();
            foreach (var edge in b.Edges)
            {
                if (edge is null || edge.Start < 0 || edge.Count < 1 || (long)edge.Start + edge.Count > d.LedCount)
                    throw new ArgumentException($"{b.Name}: edge lies outside the device LED count.");
                for (var i = edge.Start; i < edge.Start + edge.Count; i++)
                    if (!used.Add(i)) throw new ArgumentException($"{b.Name}: edge mappings overlap.");
            }
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
        if (value.Controllers.Select(c => c.Id).Distinct().Count() != value.Controllers.Count) throw new ArgumentException("Controller IDs must be unique.");
        foreach (var c in value.Controllers)
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name) || !LocalAddress(c.Address) || c.Port is < 1 or > 65535 || !Regex.IsMatch(c.SecretName ?? "", "^[a-zA-Z0-9_-]{1,64}$"))
                throw new ArgumentException("Controllers require a private IPv4 address, valid port and a Docker secret name.");
    }
    public static bool LocalAddress(string address) => IPAddress.TryParse(address, out var ip) && WLedDiscoveryService.IsLocalDeviceAddress(ip);
    public static bool ValidMac(string mac) => mac is { Length: 12 } && mac.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
}
