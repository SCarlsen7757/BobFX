using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BobFx.Core.Tests;

/// <summary>
/// The application's services wired as Program.cs wires them, on a clock the test controls, a data folder of its
/// own and stand-ins for everything that would touch the network. Services are created when first asked for, so
/// a test only pays for what it uses and can save settings before a service reads them. Nothing is started: a
/// test starts the hosted service it is about, and disposing the harness stops and disposes whatever was created.
/// </summary>
internal sealed class TestHarness(TimeProvider? time = null, WLedOptions? wled = null, CountdownOptions? countdown = null) : IAsyncDisposable
{
    private readonly List<object> created = [];
    private readonly IOptions<WLedOptions> wledOptions = Options.Create(wled ?? new WLedOptions());

    public string DataPath { get; } = TestPaths.NewDataPath();
    /// <summary>The clock every service uses. The fake one unless the test asked for real time.</summary>
    public TimeProvider Time { get; } = time ?? new FakeTimeProvider();
    public FakeTimeProvider Clock => Time as FakeTimeProvider ?? throw new InvalidOperationException("This harness runs on real time.");

    public FakeHttp Http { get; } = new();
    public FakeDiscovery DiscoverySource { get; } = new();
    public RecordingTransport Transport { get; } = new();
    public FakeStatusSource BusyBarStatus { get; } = new();

    public IConfiguration Configuration => field ??= TestData.Configuration(DataPath);
    public InstallationSettingsStore Store => field ??= new(Configuration);
    public ControllerCredentials Credentials => field ??= new(Store);
    public SoundService Sounds => field ??= new(NullLogger<SoundService>.Instance);
    public BoardFrameMonitor Monitor => field ??= new(Time);
    public ManualEffectRenderer Rgb => field ??= Keep(new ManualEffectRenderer(NullLogger<ManualEffectRenderer>.Instance, wledOptions, new RgbEffectFactory(), Time));
    public CountdownService Countdown => field ??= new(NullLogger<CountdownService>.Instance, Options.Create(countdown ?? new CountdownOptions()), Time, Store);
    public GameCoordinator Control => field ??= Keep(new GameCoordinator(Countdown, Rgb, Sounds, Time,
        NullLogger<GameCoordinator>.Instance, Store));
    public WLedDiscoveryService Discovery => field ??= Keep(new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance, wledOptions, Http,
        DiscoverySource, Store, Time));
    public InstallationOutputService Output => field ??= Keep(new InstallationOutputService(Store, Discovery, Control, Rgb, new WLedIdleSync(Store, Discovery, Http), Time,
        NullLogger<InstallationOutputService>.Instance, Transport, Monitor));
    public WLedRegistryService Registry => field ??= new(Store, Control, Http);
    public BusyBarService BusyBars => field ??= Keep(new BusyBarService(Store, Control, Http, Configuration, Time, Credentials, BusyBarStatus));

    private T Keep<T>(T service) where T : notnull { created.Add(service); return service; }

    public async ValueTask DisposeAsync()
    {
        // Last created first: a service is stopped before the services it depends on.
        foreach (var service in Enumerable.Reverse(created))
        {
            if (service is Microsoft.Extensions.Hosting.IHostedService hosted) await hosted.StopAsync(CancellationToken.None);
            (service as IDisposable)?.Dispose();
        }
    }
}
