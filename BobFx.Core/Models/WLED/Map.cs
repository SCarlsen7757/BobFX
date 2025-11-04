using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED
{
    public record Map(
        [property: JsonPropertyName("id")] int Id
    );
}
