using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmenCtl.Core;

public sealed record AgentCommand(
    [property: JsonPropertyName("cmd")] string Cmd,
    [property: JsonPropertyName("biosMode")] string? BiosMode = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("cpuLevel")] int? CpuLevel = null,
    [property: JsonPropertyName("gpuLevel")] int? GpuLevel = null,
    [property: JsonPropertyName("command")] JsonElement? Command = null,
    [property: JsonPropertyName("delays")] int[]? Delays = null,
    [property: JsonPropertyName("points")] FanCurvePoint[]? Points = null,
    [property: JsonPropertyName("intervalSeconds")] int? IntervalSeconds = null,
    [property: JsonPropertyName("hysteresisC")] int? HysteresisC = null);

public sealed record FanCurvePoint(
    [property: JsonPropertyName("temp")] int Temperature,
    [property: JsonPropertyName("cpuLevel")] int CpuLevel,
    [property: JsonPropertyName("gpuLevel")] int GpuLevel);

public sealed record AgentResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("data")] object? Data = null,
    [property: JsonPropertyName("error")] AgentError? Error = null)
{
    public const string ProtocolVersionString = "1.0";

    public static string GetAgentVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public static AgentResponse Success(object? data) =>
        new(true, ProtocolVersionString, GetAgentVersion(), data, null);

    public static AgentResponse Failure(string code, string message) =>
        new(false, ProtocolVersionString, GetAgentVersion(), null, new AgentError(code, message));
}

public sealed record AgentError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
