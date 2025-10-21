using BobFx.Core.Components;
using BobFx.Core.Services;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

// Ensure console logging is enabled
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<CountdownService>();
builder.Services.AddSingleton<UdpClientService>(sp => new(
    "255.255.255.255",
    21324,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UdpClientService>>()
));

builder.Services.AddSingleton<DRgbService>(sp => new(30));

// Create a bounded channel of frames with capacity 1 and DropOldest policy for coalescing
builder.Services.AddSingleton(_ =>
    Channel.CreateBounded<(byte[] Buffer, int Length)>(new BoundedChannelOptions(1)
    {
        SingleWriter = true,
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    }));

// Producer writes frames to the channel
builder.Services.AddHostedService<FrameSender>();

// Consumer reads frames from the channel and sends them via UDP
builder.Services.AddHostedService<ChannelFrameSender>();

var app = builder.Build();

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
