namespace BobFx.Core.Services;

/// <summary>One board in a preview, with what the browser needs to place its LEDs. <see cref="Assigned"/> is
/// false for a board drawn as an example because it has no controller yet.</summary>
public sealed record LightShowPreviewBoard(string Id, string Name, int Rotation, List<EdgeSettings> Edges, int LedCount, bool Assigned);

/// <summary>Frames of one test segment as the boards would receive them. Each frame is the RGB bytes of every
/// board in row order, base64 encoded. <see cref="ExampleBoards"/> is true when the layout has no boards at all.</summary>
public sealed record LightShowPreview(int FramesPerSecond, double Seconds, bool ExampleBoards, List<LightShowPreviewBoard> Boards, List<string> Frames);

/// <summary>Renders a light show segment for the browser preview through the same renderer as the boards,
/// so what the page shows cannot drift from what WLED receives.</summary>
public static class LightShowPreviewRenderer
{
    internal const int FramesPerSecond = 20;
    // Keeps a response a few megabytes even for large installations.
    private const long MaxBytes = 8_000_000;
    internal const int ExampleLeds = 60;

    public static LightShowPreview Render(InstallationSettings saved, LightShowTestRequest request)
    {
        if (request.LightShow is null || request.Animation is null) throw new ArgumentException("Light show and animation settings are required.");
        var draft = saved with { LightShow = request.LightShow, Animation = request.Animation };
        InstallationSettingsStore.Validate(draft);
        var seconds = BoardLightShow.TestSeconds(request.Segment ?? "", draft.Animation)
            ?? throw new ArgumentException("Choose countdown, playing or gameOver to preview.");
        var example = draft.Boards.Count == 0;
        var (row, assigned) = PreviewRow(example ? ExampleLayout() : draft.Boards, draft.Devices);
        draft = draft with { Devices = row.Devices, Boards = row.Boards };
        var boards = BoardSceneRenderer.AssignedBoards(draft);
        var counts = boards.Select(b => draft.Devices.First(d => d.Mac == b.DeviceMac).LedCount).ToList();
        var frameCount = (int)Math.Ceiling(seconds * FramesPerSecond);
        var bytes = counts.Sum() * 3;
        if ((long)bytes * frameCount > MaxBytes) throw new ArgumentException("Too many LEDs to preview this part of the show.");

        var preparation = CountdownService.PreCountdownDuration.TotalSeconds;
        var frames = new List<string>(frameCount);
        var buffer = new byte[bytes];
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (BoardLightShow.TestScene(request.Segment!, frame / (double)FramesPerSecond, draft.Animation) is not { } moment) break;
            var offset = 0;
            for (var i = 0; i < boards.Count; i++)
            {
                var pixels = BoardSceneRenderer.Render(draft, boards[i], counts[i], moment.Phase, moment.Elapsed, preparation);
                pixels.CopyTo(buffer, offset);
                offset += pixels.Length;
            }
            frames.Add(Convert.ToBase64String(buffer));
        }
        return new(FramesPerSecond, seconds, example,
            boards.Select((b, i) => new LightShowPreviewBoard(b.Id, b.Name, b.Rotation, b.Edges, counts[i], !example && assigned.Contains(b.Id))).ToList(), frames);
    }

    /// <summary>The whole row as the layout lists it. Real games skip boards without a controller; the preview
    /// stands in an example controller for each, so the show can be judged across the full row before the
    /// hardware is there. Assigned boards keep their real controller and mapping. A board without one copies the
    /// LED count and edge mapping of the first board that has one, keeping its own rotation, so it looks like the
    /// hardware already in use; with no controller anywhere it gets 60 LEDs in the two-strip mapping.</summary>
    private static (InstallationSettings Row, HashSet<string> Assigned) PreviewRow(IReadOnlyList<BoardSettings> layout, List<DeviceSettings> devices)
    {
        var real = devices.Where(d => d.Selected).ToDictionary(d => d.Mac);
        const int edge = ExampleLeds / 4;
        var template = layout.Where(b => b.DeviceMac is { } mac && real.ContainsKey(mac))
            .Select(b => (Leds: real[b.DeviceMac!].LedCount, b.Edges)).FirstOrDefault();
        if (template.Edges is null)
            template = (ExampleLeds, [new(0, edge), new(edge, edge), new(edge * 3, edge, true), new(edge * 2, edge, true)]);
        var assigned = new HashSet<string>();
        var rowDevices = new List<DeviceSettings>();
        var rowBoards = new List<BoardSettings>();
        for (var i = 0; i < layout.Count; i++)
        {
            var board = layout[i];
            if (board.DeviceMac is { } mac && real.TryGetValue(mac, out var device))
            {
                assigned.Add(board.Id);
                rowDevices.Add(device);
                rowBoards.Add(board);
                continue;
            }
            // A made-up controller identity, skipping any MAC a registered device already uses.
            var number = i + 1;
            while (devices.Any(d => d.Mac == $"f{number:x11}")) number += layout.Count;
            var example = $"f{number:x11}";
            rowDevices.Add(new(example, $"Example {i + 1}", "192.168.0.1", 80, template.Leds, 21324));
            rowBoards.Add(board with { DeviceMac = example, Edges = template.Edges.ToList() });
        }
        return (new InstallationSettings { Devices = rowDevices, Boards = rowBoards }, assigned);
    }

    /// <summary>Two boards for a layout without any.</summary>
    private static List<BoardSettings> ExampleLayout() =>
        [new() { Id = "example-1", Name = "Example 1" }, new() { Id = "example-2", Name = "Example 2" }];
}
