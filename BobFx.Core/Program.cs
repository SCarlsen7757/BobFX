using BobFx.Core.Components;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
});

StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

builder.Services.Configure<WLedOptions>(options =>
{
    options.Led.LedCount = builder.Configuration.GetValue<int?>("WLED_LED_COUNT")
                           ?? builder.Configuration.GetValue<int?>("WLed:Led:LedCount")
                           ?? 30;
    options.Udp.TargetAddress = builder.Configuration.GetValue<string?>("WLED_TARGET_ADDRESS")
                                ?? builder.Configuration.GetValue<string?>("WLed:Udp:TargetAddress")
                                ?? "255.255.255.255";
    options.Udp.TargetPort = builder.Configuration.GetValue<int?>("WLED_TARGET_PORT")
                             ?? builder.Configuration.GetValue<int?>("WLed:Udp:TargetPort")
                             ?? 21324;
    options.Udp.UpdateInterval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue<int?>("WLED_UPDATE_INTERVAL_MS")
                                                           ?? builder.Configuration.GetValue<int?>("WLed:Udp:UpdateIntervalMs")
                                                           ?? 16);
    options.Discovery.UpdateInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("WLED_DISCOVERY_INTERVAL_SECONDS")
                                                            ?? builder.Configuration.GetValue<int?>("WLed:Discovery:UpdateIntervalSeconds")
                                                            ?? 60);
});

builder.Services.Configure<WLedOptions.LedOptions>(options =>
{
    options.LedCount = builder.Configuration.GetValue<int?>("WLED_LED_COUNT")
                       ?? builder.Configuration.GetValue<int?>("WLed:Led:LedCount")
                       ?? 30;
});

builder.Services.Configure<WLedOptions.UpdOptions>(options =>
{
    options.TargetAddress = builder.Configuration.GetValue<string?>("WLED_TARGET_ADDRESS")
                            ?? builder.Configuration.GetValue<string?>("WLed:Udp:TargetAddress")
                            ?? "255.255.255.255";
    options.TargetPort = builder.Configuration.GetValue<int?>("WLED_TARGET_PORT")
                         ?? builder.Configuration.GetValue<int?>("WLed:Udp:TargetPort")
                         ?? 21324;
    options.UpdateInterval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue<int?>("WLED_UPDATE_INTERVAL_MS")
                                                       ?? builder.Configuration.GetValue<int?>("WLed:Udp:UpdateIntervalMs")
                                                       ?? 16);
});

builder.Services.Configure<WLedOptions.DiscoveryOptions>(options =>
{
    options.UpdateInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("WLED_DISCOVERY_INTERVAL_SECONDS")
                                                  ?? builder.Configuration.GetValue<int?>("WLed:Discovery:UpdateIntervalSeconds")
                                                  ?? 60);
});

builder.Services.Configure<CountdownOptions>(options =>
{
    options.PreCountdownDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("PRE_COUNTDOWN_DURATION_SECONDS")
                                                        ?? builder.Configuration.GetValue<int?>("Countdown:PreDurationSeconds")
                                                        ?? 3);
    options.CountdownDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("COUNTDOWN_DURATION_SECONDS")
                                                     ?? builder.Configuration.GetValue<int?>("Countdown:DurationSeconds")
                                                     ?? 60);
    options.CountdownDeviation = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("COUNTDOWN_DEVIATION_SECONDS")
                                                      ?? builder.Configuration.GetValue<int?>("Countdown:DeviationSeconds")
                                                      ?? 5);
});

builder.Services.AddSingleton<IRgbEffectFactory, RgbEffectFactory>();

builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<DRgbService>();
builder.Services.AddSingleton<UdpClientService>();

builder.Services.AddSingleton<RgbControlService>();

builder.Services.AddSingleton<WLedBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WLedBroadcastService>());

builder.Services.AddSingleton<WLedDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WLedDiscoveryService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();
builder.Services.AddHttpClient();

var app = builder.Build();

_ = app.Services.GetRequiredService<RgbControlService>();
_ = app.Services.GetRequiredService<WLedDiscoveryService>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
