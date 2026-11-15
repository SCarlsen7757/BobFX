using System.Text.Json.Serialization;

namespace BobFx.Core.Models.WLED;

public record Wifi([property: JsonPropertyName("rssi")] int Rssi);
