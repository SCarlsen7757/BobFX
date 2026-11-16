using BobFx.Core;
using BobFx.Core.Controllers;
using BobFx.Core.Hubs;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InstallationSettingsStore>();
builder.Services.AddSingleton<ControllerCredentials>();
builder.Services.AddOptions<WLedOptions>().BindConfiguration("WLed")
    .Validate(options => options.IsValid(), "WLED requires 1–490 LEDs and a 5–3600s discovery interval.").ValidateOnStart();
builder.Services.AddOptions<CountdownOptions>().BindConfiguration("Countdown")
    .Validate(options => options.IsValid(), "Countdown requires 1–86400s duration, and deviation smaller than duration.").ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security")
    .Validate(options => builder.Environment.IsDevelopment() || options.IsValid(), "Production requires explicit Security:AllowedClientNetworks CIDRs.").ValidateOnStart();
builder.Services.AddSingleton<IRgbEffectFactory, RgbEffectFactory>();
builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<SoundService>();
builder.Services.AddSingleton<BoardFrameMonitor>();
builder.Services.AddSingleton<WLedRegistryService>();
builder.Services.AddSingleton<WLedIdleSync>();
builder.Services.AddSingleton<IWLedFrameTransport, WLedFrameTransport>();
builder.Services.AddSingleton<IWLedDiscoverySource, MdnsWLedDiscoverySource>();
builder.Services.AddSingleton<IBusyBarStatusSource, BusyBarStatusSource>();
builder.Services.AddDeviceHttpClient("wled", TimeSpan.FromSeconds(2));
builder.Services.AddDeviceHttpClient("busybar", TimeSpan.FromSeconds(2));
builder.Services.AddDeviceHttpClient("busybar-assets", TimeSpan.FromSeconds(30));
// Background services start in this order.
builder.Services.AddHostedSingleton<DRgbService>();
builder.Services.AddHostedSingleton<RgbAndSoundControlService>();
builder.Services.AddHostedSingleton<BoardMonitorService>();
builder.Services.AddHostedSingleton<InstallationOutputService>();
builder.Services.AddHostedSingleton<WLedDiscoveryService>();
builder.Services.AddHostedSingleton<UiStateService>();
builder.Services.AddHostedSingleton<BusyBarService>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(options => options.Filters.Add<ApiExceptionFilter>());

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    var hosts = app.Configuration["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (hosts is null || hosts.Length == 0 || hosts.Any(host => host.Contains('*')))
        throw new InvalidOperationException("Production requires explicit AllowedHosts; wildcard hosts are forbidden.");
}

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler();
app.UseMiddleware<ClientPolicyMiddleware>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Contains('-') && context.Context.Request.Path.StartsWithSegments("/ui/assets"))
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    }
});
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
app.MapControllers();
app.MapHub<UiHub>("/hubs/ui");
foreach (var route in AppRoutes.Pages)
    app.MapGet(route, async (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "ui", "index.html"));
    });
app.Run();
public partial class Program;

public static class AppRoutes
{
    /// <summary>The paths that serve the app. They are listed, not matched by a fallback, so that any other path
    /// is a real 404. The frontend has the same list in frontend/src/lib/routes.ts.</summary>
    public static readonly IReadOnlyList<string> Pages = ["/", "/game/fullscreen", "/countdown", "/lights", "/drgb", "/wled", "/layout", "/boards", "/controllers"];
}
