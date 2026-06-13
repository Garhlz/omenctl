using System.Text.Json;
using OmenCtl.Agent;
using OmenCtl.Core;
using OmenCtl.Sensors;

JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = false
};

SemaphoreSlim hardwareGate = new(1, 1);
IHardwareController controller = new OmenFanController();
SensorFusionService sensors = new([
    new LibreHardwareMonitorProvider(),
    new NvmlProvider()
]);
FanCurveService fanCurve = new(controller, SnapshotAsync);

while(await Console.In.ReadLineAsync() is { } line)
{
    if(string.IsNullOrWhiteSpace(line))
        continue;

    AgentResponse response;
    try
    {
        AgentCommand? command = JsonSerializer.Deserialize<AgentCommand>(line, jsonOptions);
        response = command is null
            ? AgentResponse.Failure("invalid_json", "Command JSON was empty.")
            : await DispatchAsync(command, CancellationToken.None).ConfigureAwait(false);
    }
    catch(JsonException ex)
    {
        response = AgentResponse.Failure("invalid_json", ex.Message);
    }
    catch(Exception ex)
    {
        response = AgentResponse.Failure("unexpected_error", ex.Message);
    }

    Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
    await Console.Out.FlushAsync().ConfigureAwait(false);
}

async Task<AgentResponse> DispatchAsync(AgentCommand command, CancellationToken cancellationToken)
{
    await hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        return command.Cmd switch
        {
            "snapshot" => AgentResponse.Success(await SnapshotAsync(cancellationToken).ConfigureAwait(false)),
            "setAuto" => AgentResponse.Success(await ApplyAndSnapshotAsync(
                token => controller.SetAutoAsync(command.BiosMode, token),
                cancellationToken).ConfigureAwait(false)),
            "setMax" => AgentResponse.Success(await ApplyAndSnapshotAsync(
                controller.SetMaxAsync,
                cancellationToken).ConfigureAwait(false)),
            "setManual" => AgentResponse.Success(await ApplyAndSnapshotAsync(
                token => SetManualAsync(command, token),
                cancellationToken).ConfigureAwait(false)),
            "setProgram" => AgentResponse.Success(await ApplyAndSnapshotAsync(
                token => SetProgramAsync(command, token),
                cancellationToken).ConfigureAwait(false)),
            "applyAndReadback" => AgentResponse.Success(await ApplyAndReadbackAsync(command, cancellationToken).ConfigureAwait(false)),
            "startCurve" => AgentResponse.Success(fanCurve.Start(command.Points, command.IntervalSeconds)),
            "stopCurve" => AgentResponse.Success(fanCurve.Stop()),
            "curveStatus" => AgentResponse.Success(fanCurve.Status()),
            _ => AgentResponse.Failure("unknown_command", $"Unknown command: {command.Cmd}")
        };
    }
    catch(NotImplementedException ex)
    {
        return AgentResponse.Failure("not_implemented", ex.Message);
    }
    catch(HardwareControllerException ex)
    {
        return AgentResponse.Failure(ex.Code, ex.Message);
    }
    catch(ArgumentException ex)
    {
        return AgentResponse.Failure("invalid_argument", ex.Message);
    }
    finally
    {
        hardwareGate.Release();
    }
}

async Task<HardwareSnapshot> SnapshotAsync(CancellationToken cancellationToken)
{
    HardwareSnapshot snapshot = await controller.SnapshotAsync(cancellationToken).ConfigureAwait(false);
    await sensors.MergeIntoAsync(snapshot, cancellationToken).ConfigureAwait(false);
    return snapshot;
}

async Task<HardwareSnapshot> ApplyAndSnapshotAsync(
    Func<CancellationToken, Task<HardwareSnapshot>> apply,
    CancellationToken cancellationToken)
{
    await apply(cancellationToken).ConfigureAwait(false);
    return await SnapshotAsync(cancellationToken).ConfigureAwait(false);
}

Task<HardwareSnapshot> SetManualAsync(AgentCommand command, CancellationToken cancellationToken)
{
    if(command.CpuLevel is null || command.GpuLevel is null)
        throw new ArgumentException("setManual requires cpuLevel and gpuLevel.");
    return controller.SetManualAsync(command.CpuLevel.Value, command.GpuLevel.Value, cancellationToken);
}

Task<HardwareSnapshot> SetProgramAsync(AgentCommand command, CancellationToken cancellationToken)
{
    if(string.IsNullOrWhiteSpace(command.Name))
        throw new ArgumentException("setProgram requires name.");
    return controller.SetProgramAsync(command.Name, cancellationToken);
}

async Task<IReadOnlyList<object>> ApplyAndReadbackAsync(AgentCommand command, CancellationToken cancellationToken)
{
    if(command.Command is null)
        throw new ArgumentException("applyAndReadback requires command.");

    int[] delays = command.Delays is { Length: > 0 } ? command.Delays : [0, 1, 3, 5, 15];
    AgentCommand inner = command.Command.Value.Deserialize<AgentCommand>(jsonOptions)
        ?? throw new ArgumentException("applyAndReadback command could not be parsed.");

    AgentResponse applyResponse = await ApplyInnerAsync(inner, cancellationToken).ConfigureAwait(false);
    List<object> rows = [];
    int previousDelay = 0;
    foreach(int delay in delays)
    {
        int wait = delay - previousDelay;
        if(wait > 0)
            await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);

        rows.Add(new
        {
            phase = "readback",
            delaySeconds = delay,
            requestedCommand = inner.Cmd,
            apply = applyResponse,
            resultSnapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false)
        });
        previousDelay = delay;
    }
    return rows;
}

async Task<AgentResponse> ApplyInnerAsync(AgentCommand command, CancellationToken cancellationToken) =>
    command.Cmd switch
    {
        "setAuto" => AgentResponse.Success(await ApplyAndSnapshotAsync(
            token => controller.SetAutoAsync(command.BiosMode, token),
            cancellationToken).ConfigureAwait(false)),
        "setMax" => AgentResponse.Success(await ApplyAndSnapshotAsync(
            controller.SetMaxAsync,
            cancellationToken).ConfigureAwait(false)),
        "setManual" => AgentResponse.Success(await ApplyAndSnapshotAsync(
            token => SetManualAsync(command, token),
            cancellationToken).ConfigureAwait(false)),
        "setProgram" => AgentResponse.Success(await ApplyAndSnapshotAsync(
            token => SetProgramAsync(command, token),
            cancellationToken).ConfigureAwait(false)),
        _ => AgentResponse.Failure("invalid_apply_command", $"applyAndReadback cannot apply command: {command.Cmd}")
    };
