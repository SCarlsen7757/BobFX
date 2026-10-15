using BobFx.Core.Components;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InstallationSettingsStore>();
builder.Services.AddSingleton<ControllerCredentials>();
builder.Services.AddOptions<WLedOptions>().BindConfiguration("WLed")
    .Validate(options => options.IsValid(), "WLED requires 1–490 LEDs, IPv4 target, valid port, 10–500ms UDP interval and 5–3600s discovery interval.").ValidateOnStart();
builder.Services.AddOptions<CountdownOptions>().BindConfiguration("Countdown")
    .Validate(options => options.IsValid(), "Countdown requires 0–60s pre-duration, 1–86400s duration, and deviation smaller than duration.").ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security")
    .Validate(options => builder.Environment.IsDevelopment() || options.IsValid(), "Production requires explicit Security:AllowedClientNetworks CIDRs.").ValidateOnStart();
builder.Services.AddSingleton<IRgbEffectFactory, RgbEffectFactory>();
builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<DRgbService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DRgbService>());
builder.Services.AddSingleton<SoundService>();
builder.Services.AddSingleton<RgbAndSoundControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RgbAndSoundControlService>());
// Legacy broadcaster types are retained for protocol regression tests only.
builder.Services.AddSingleton<InstallationOutputService>();
builder.Services.AddSingleton<IWLedFrameTransport, WLedFrameTransport>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InstallationOutputService>());
builder.Services.AddSingleton<WLedDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WLedDiscoveryService>());
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddControllers();
builder.Services.AddHttpClient("wled", client => { client.Timeout = TimeSpan.FromSeconds(2); client.MaxResponseContentBufferSize = 64 * 1024; })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<WLedRegistryService>();
builder.Services.AddHttpClient("busybar", client => { client.Timeout = TimeSpan.FromSeconds(2); client.MaxResponseContentBufferSize = 64 * 1024; })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<BusyBarService>();
builder.Services.AddSingleton<IBusyBarStatusSource, BusyBarStatusSource>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BusyBarService>());

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    var hosts = app.Configuration["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (hosts is null || hosts.Length == 0 || hosts.Any(host => host.Contains('*')))
        throw new InvalidOperationException("Production requires explicit AllowedHosts; wildcard hosts are forbidden.");
}

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.Use(async (context, next) =>
{
    if (!app.Environment.IsDevelopment() &&
        !context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.Allows(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden; return;
    }
    if (!SecurityOptions.IsSameOrigin(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden; return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.UseAntiforgery();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (RgbAndSoundControlService control, DRgbService rgb, InstallationOutputService output) =>
    control.IsReady && rgb.IsReady && output.IsReady && rgb.LastError is null && output.LastError is null ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGet("/health/status", (RgbAndSoundControlService control, DRgbService rgb, InstallationOutputService output, WLedDiscoveryService discovery) => Results.Ok(new
{
    phase = control.Phase.ToString(),
    renderer = new { ready = rgb.IsReady, error = rgb.LastError },
    udp = new { active = rgb.IsUdpActive, lastSendUtc = output.LastSendUtc, error = output.LastError, devices = output.Errors },
    discovery = new { lastScanUtc = discovery.LastScanUtc, error = discovery.LastError, devices = discovery.Devices.Count }
}));
app.MapGet("/api/installation/status", (InstallationOutputService output) => Results.Ok(new { output.LastSendUtc, output.Errors }));
app.MapGet("/api/installation/controllers/status", (BusyBarService busy) => Results.Ok(busy.Statuses));
app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
public partial class Program;
