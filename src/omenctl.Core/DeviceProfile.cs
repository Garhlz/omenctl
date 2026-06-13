using System.Text.Json.Serialization;

namespace OmenCtl.Core;

public sealed record DeviceProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fanLevelReliable")] bool FanLevelReliable,
    [property: JsonPropertyName("fanControlReliable")] bool FanControlReliable,
    [property: JsonPropertyName("fanRpmReliable")] bool FanRpmReliable,
    [property: JsonPropertyName("omenGpuTempReliable")] bool OmenGpuTempReliable,
    [property: JsonPropertyName("omenFanRateInterpretation")] string OmenFanRateInterpretation,
    [property: JsonPropertyName("rawFanMode")] string? RawFanMode);

public static class DeviceProfiles
{
    public static DeviceProfile ForProduct(string? product) =>
        string.Equals(product, "8BAB", StringComparison.OrdinalIgnoreCase)
            ? EightBab
            : Unknown(product);

    public static readonly DeviceProfile EightBab = new(
        Id: "8BAB",
        FanLevelReliable: true,
        FanControlReliable: true,
        FanRpmReliable: false,
        OmenGpuTempReliable: false,
        OmenFanRateInterpretation: "raw",
        RawFanMode: "0x44");

    private static DeviceProfile Unknown(string? product) => new(
        Id: string.IsNullOrWhiteSpace(product) ? "unknown" : product,
        FanLevelReliable: false,
        FanControlReliable: false,
        FanRpmReliable: false,
        OmenGpuTempReliable: false,
        OmenFanRateInterpretation: "unknown",
        RawFanMode: null);
}
