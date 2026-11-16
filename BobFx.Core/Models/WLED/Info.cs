using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED;

/// <summary>The part of WLED's <c>/json/info</c> answer that BobFX reads. A device may leave any of it out.</summary>
public record Info(
    [property: JsonPropertyName("ver")] string? Ver,
    [property: JsonPropertyName("leds")] Leds? Leds,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("udpport")] int Udpport,
    [property: JsonPropertyName("lm")] string? Lm,
    [property: JsonPropertyName("lip")] string? Lip,
    [property: JsonPropertyName("wifi")] Wifi? Wifi,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("core")] string? Core,
    [property: JsonPropertyName("freeheap")] int Freeheap,
    [property: JsonPropertyName("uptime")] int Uptime,
    [property: JsonPropertyName("brand")] string? Brand,
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("mac")] string? Mac
);
