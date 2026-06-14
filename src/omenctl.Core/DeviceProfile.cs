using System.Text.Json.Serialization;

namespace OmenCtl.Core;

public sealed record DeviceProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fanLevelReliable")] bool FanLevelReliable,
    [property: JsonPropertyName("fanControlReliable")] bool FanControlReliable,
    [property: JsonPropertyName("fanRpmReliable")] bool FanRpmReliable,
    [property: JsonPropertyName("omenGpuTempReliable")] bool OmenGpuTempReliable,
    [property: JsonPropertyName("omenFanRateInterpretation")] string OmenFanRateInterpretation,
    [property: JsonPropertyName("rawFanMode")] string? RawFanMode,
    [property: JsonPropertyName("manualFanLevelMin")] int ManualFanLevelMin = 0,
    [property: JsonPropertyName("manualFanLevelMax")] int ManualFanLevelMax = 100,
    [property: JsonPropertyName("recommendedCurve")] FanCurvePoint[]? RecommendedCurve = null);

public static class DeviceProfiles
{
    public static DeviceProfile ForProduct(string? product) =>
        string.Equals(product, "8BAB", StringComparison.OrdinalIgnoreCase)
            ? EightBab
            : Unknown(product);

    // 8BAB confirmed: SetManual 100/100 → BIOS readback 64/64; EC only uses 6 bits (0–64)
    public static readonly FanCurvePoint[] EightBabCurve =
    [
        new(45, 35, 35),
        new(55, 44, 44),
        new(65, 52, 52),
        new(75, 58, 58),
        new(85, 64, 64),
    ];

    public static readonly DeviceProfile EightBab = new(
        Id: "8BAB",
        FanLevelReliable: true,
        FanControlReliable: true,
        FanRpmReliable: false,
        OmenGpuTempReliable: false,
        OmenFanRateInterpretation: "raw",
        RawFanMode: "0x44",
        ManualFanLevelMin: 0,
        ManualFanLevelMax: 64,
        RecommendedCurve: EightBabCurve);

    private static DeviceProfile Unknown(string? product) => new(
        Id: string.IsNullOrWhiteSpace(product) ? "unknown" : product,
        FanLevelReliable: false,
        FanControlReliable: false,
        FanRpmReliable: false,
        OmenGpuTempReliable: false,
        OmenFanRateInterpretation: "unknown",
        RawFanMode: null);
}
