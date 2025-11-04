using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED
{
    public record Info(
        [property: JsonPropertyName("ver")] string Ver,
        [property: JsonPropertyName("vid")] int Vid,
        [property: JsonPropertyName("cn")] string Cn,
        [property: JsonPropertyName("release")] string Release,
        [property: JsonPropertyName("leds")] Leds Leds,
        [property: JsonPropertyName("str")] bool Str,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("udpport")] int Udpport,
        [property: JsonPropertyName("simplifiedui")] bool Simplifiedui,
        [property: JsonPropertyName("live")] bool Live,
        [property: JsonPropertyName("liveseg")] int Liveseg,
        [property: JsonPropertyName("lm")] string Lm,
        [property: JsonPropertyName("lip")] string Lip,
        [property: JsonPropertyName("ws")] int Ws,
        [property: JsonPropertyName("fxcount")] int Fxcount,
        [property: JsonPropertyName("palcount")] int Palcount,
        [property: JsonPropertyName("cpalcount")] int Cpalcount,
        [property: JsonPropertyName("maps")] IReadOnlyList<Map> Maps,
        [property: JsonPropertyName("wifi")] Wifi Wifi,
        [property: JsonPropertyName("ndc")] int Ndc,
        [property: JsonPropertyName("arch")] string Arch,
        [property: JsonPropertyName("core")] string Core,
        [property: JsonPropertyName("clock")] int Clock,
        [property: JsonPropertyName("flash")] int Flash,
        [property: JsonPropertyName("lwip")] int Lwip,
        [property: JsonPropertyName("freeheap")] int Freeheap,
        [property: JsonPropertyName("uptime")] int Uptime,
        [property: JsonPropertyName("time")] string Time,
        [property: JsonPropertyName("opt")] int Opt,
        [property: JsonPropertyName("brand")] string Brand,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("mac")] string Mac,
        [property: JsonPropertyName("ip")] string Ip
    );
}
