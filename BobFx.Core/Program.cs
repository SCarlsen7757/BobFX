using BobFx.Core.Components;
using BobFx.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Ensure console logging is enabled
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var ledCount = builder.Configuration.GetValue<int?>("LedConfiguration:LedCount") ?? 30;
var udpTargetAddress = builder.Configuration.GetValue<string?>("UdpConfiguration:TargetAddress") ?? "255.255.255.255";
var udpTargetPort = builder.Configuration.GetValue<int?>("UdpConfiguration:TargetPort") ?? 21324;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Core Services
builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<DRgbService>(sp => new(ledCount));

// UDP Client for WLED communication
builder.Services.AddSingleton<UdpClientService>(sp => new(
    udpTargetAddress,
    udpTargetPort,
    sp.GetRequiredService<ILogger<UdpClientService>>()
));

// Logic/Orchestration Layer
builder.Services.AddSingleton<RgbControlService>();

// Background service that broadcasts to WLED controllers
builder.Services.AddHostedService<WledBroadcastService>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
