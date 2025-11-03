using BobFx.Core.Components;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;

var builder = WebApplication.CreateBuilder(args);

// Ensure console logging is enabled
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

// Read configuration from environment variables and appsettings.json
// Priority: Environment Variables (LED_COUNT, TARGET_ADDRESS, etc.) > appsettings.json > Defaults
var ledCount = builder.Configuration.GetValue<int?>("LED_COUNT")
    ?? builder.Configuration.GetValue<int?>("LedConfiguration:LedCount")
    ?? 30;

var udpTargetAddress = builder.Configuration.GetValue<string?>("TARGET_ADDRESS")
    ?? builder.Configuration.GetValue<string?>("UdpConfiguration:TargetAddress")
    ?? "255.255.255.255";

var udpTargetPort = builder.Configuration.GetValue<int?>("TARGET_PORT")
    ?? builder.Configuration.GetValue<int?>("UdpConfiguration:TargetPort")
    ?? 21324;

var updateIntervalMs = builder.Configuration.GetValue<int?>("UPDATE_INTERVAL_MS")
    ?? builder.Configuration.GetValue<int?>("UdpConfiguration:UpdateIntervalMs")
    ?? 16; // Default 16ms = 60 FPS

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add controllers for API endpoints
builder.Services.AddControllers();

// Register RGB Effect Factory
builder.Services.AddSingleton<IRgbEffectFactory, RgbEffectFactory>();

// Core Services
builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<DRgbService>(sp => new(ledCount, sp.GetRequiredService<ILogger<DRgbService>>(), sp.GetRequiredService<IRgbEffectFactory>()));

// UDP Client for WLED communication
builder.Services.AddSingleton<UdpClientService>(sp => new(
    udpTargetAddress,
    udpTargetPort,
    sp.GetRequiredService<ILogger<UdpClientService>>()
));

// Logic/Orchestration Layer
builder.Services.AddSingleton<RgbControlService>();

// Background service that broadcasts to WLED controllers
// Pass the update interval to the service
builder.Services.AddSingleton<WledBroadcastService>(sp => new(
    sp.GetRequiredService<DRgbService>(),
    sp.GetRequiredService<UdpClientService>(),
    sp.GetRequiredService<ILogger<WledBroadcastService>>(),
    TimeSpan.FromMilliseconds(updateIntervalMs)
));
builder.Services.AddHostedService(sp => sp.GetRequiredService<WledBroadcastService>());

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
});

var app = builder.Build();

// Initialize RgbControlService to ensure it subscribes to countdown events
// This is important because the service needs to be created to wire up event handlers
_ = app.Services.GetRequiredService<RgbControlService>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers(); // Add this to enable API controllers
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
