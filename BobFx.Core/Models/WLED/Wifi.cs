using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED
{
    public record Wifi(
        [property: JsonPropertyName("bssid")] string BssId,
        [property: JsonPropertyName("rssi")] int Rssi,
        [property: JsonPropertyName("signal")] int Signal,
        [property: JsonPropertyName("channel")] int Channel,
        [property: JsonPropertyName("ap")] bool Ap
    );
}
