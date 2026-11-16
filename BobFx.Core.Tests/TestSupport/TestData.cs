using BobFx.Core.Services;
using Microsoft.Extensions.Configuration;

namespace BobFx.Core.Tests;

/// <summary>The small installation most tests start from: one 16-LED controller with one board on it.</summary>
internal static class TestData
{
    public const string Mac = "001122334455", OtherMac = "aabbccddeeff";

    public static DeviceSettings Device(string mac = Mac) => new(mac, "Board", "192.168.1.20", 80, 16, 21324);
    public static BoardSettings Board(string mac = Mac, string id = "a") => new() { Id = id, Name = id, DeviceMac = mac,
        Left = new(0, 8), Right = new(8, 8) };
    public static InstallationSettings Installation() => new() { Devices = [Device()], Boards = [Board()] };
    /// <summary>Two controllers at different addresses, each with a board, and an unassigned slot between them.</summary>
    public static InstallationSettings TwoBoardsAndAGap() => Installation() with
    {
        Devices = [Device(), Device(OtherMac) with { Address = "192.168.1.21" }],
        Boards = [Board(), new(), Board(OtherMac, "b")]
    };

    public static IConfiguration Configuration(string dataPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:DataPath"] = dataPath }).Build();
    /// <summary>A settings store on its own data folder, or on <paramref name="dataPath"/> to read what another store saved.</summary>
    public static InstallationSettingsStore Store(string? dataPath = null) => new(Configuration(dataPath ?? TestPaths.NewDataPath()));
}

/// <summary>Status messages as a BUSY Bar sends them: State.updates(2) → StateUpdate.input(11) → the event.</summary>
internal static class BusyBarFrames
{
    /// <summary>The START button, button 2, was pressed.</summary>
    public static byte[] StartPressed => [0x12, 6, 0x5a, 4, 0x0a, 2, 8, 2];
    /// <summary>BACK, button 1, which belongs to the bar itself.</summary>
    public static byte[] BackPressed => [0x12, 6, 0x5a, 4, 0x0a, 2, 8, 1];
    /// <summary>The wheel was pressed: OK, button 0, which protobuf leaves out as the default.</summary>
    public static byte[] WheelPressed => [0x12, 4, 0x5a, 2, 0x0a, 0];
    /// <summary>The mode selector was turned to a position; 3 is APPS.</summary>
    public static byte[] Selector(int position) => [0x12, 6, 0x5a, 4, 0x12, 2, 8, (byte)position];
}
