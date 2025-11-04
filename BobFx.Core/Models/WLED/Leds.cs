using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED
{
    public record Leds(
        [property: JsonPropertyName("count")] int Count
    );
}
