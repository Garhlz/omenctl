using System.Text.Json.Serialization;

namespace OmenCtl.Core;

public sealed record HardwareSnapshot
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("product")]
    public string Product { get; init; } = "unknown";

    [JsonPropertyName("deviceProfile")]
    public DeviceProfile DeviceProfile { get; init; } = DeviceProfiles.ForProduct(null);

    [JsonPropertyName("temps")]
    public Dictionary<string, SensorValue> Temps { get; init; } = [];

    [JsonPropertyName("loads")]
    public Dictionary<string, SensorValue> Loads { get; init; } = [];

    [JsonPropertyName("fans")]
    public Dictionary<string, FanSnapshot> Fans { get; init; } = [];

    [JsonPropertyName("biosFan")]
    public Dictionary<string, object?> BiosFan { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];

    [JsonPropertyName("raw")]
    public Dictionary<string, object?> Raw { get; init; } = [];
}

public sealed record SensorValue(
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("trusted")] bool Trusted,
    [property: JsonPropertyName("suspect")] bool Suspect = false);

public sealed record FanSnapshot(
    [property: JsonPropertyName("rpm")] int? Rpm,
    [property: JsonPropertyName("rate")] int? Rate,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("rpmTrusted")] bool RpmTrusted,
    [property: JsonPropertyName("levelTrusted")] bool LevelTrusted);
