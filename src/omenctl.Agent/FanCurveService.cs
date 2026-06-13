using System.Text.Json.Serialization;
using OmenCtl.Core;

namespace OmenCtl.Agent;

internal sealed class FanCurveService
{
    private readonly IHardwareController controller;
    private readonly Func<CancellationToken, Task<HardwareSnapshot>> snapshot;
    private readonly SemaphoreSlim? writeGate;
    private readonly object gate = new();

    private CancellationTokenSource? cts;
    private Task? worker;
    private FanCurveSettings? settings;
    private FanCurveStatus status = FanCurveStatus.Stopped();
    private double? lastCpuTemp;
    private double? lastGpuTemp;
    private double? lastCpuLevel;
    private double? lastGpuLevel;
    private int tickCount;
    private int applyCount;

    public FanCurveService(
        IHardwareController controller,
        Func<CancellationToken, Task<HardwareSnapshot>> snapshot,
        SemaphoreSlim? writeGate = null)
    {
        this.controller = controller;
        this.snapshot = snapshot;
        this.writeGate = writeGate;
    }

    public FanCurveStatus Start(IReadOnlyList<FanCurvePoint>? points, int? intervalSeconds)
    {
        FanCurveSettings next = new(
            Points: NormalizePoints(points),
            IntervalSeconds: Math.Clamp(intervalSeconds ?? 5, 1, 60),
            HysteresisC: 2);

        return Start(next);
    }

    public FanCurveStatus Start(IReadOnlyList<FanCurvePoint>? points, int? intervalSeconds, int? hysteresisC)
    {
        FanCurveSettings next = new(
            Points: NormalizePoints(points),
            IntervalSeconds: Math.Clamp(intervalSeconds ?? 5, 1, 60),
            HysteresisC: Math.Clamp(hysteresisC ?? 2, 0, 10));

        return Start(next);
    }

    private FanCurveStatus Start(FanCurveSettings next)
    {
        Stop();

        CancellationTokenSource source = new();
        lock(gate)
        {
            settings = next;
            status = FanCurveStatus.CreateRunning(next, null, null, null, null, 0, 0);
            lastCpuTemp = null; lastGpuTemp = null;
            lastCpuLevel = null; lastGpuLevel = null;
            tickCount = 0;
            applyCount = 0;
            cts = source;
            worker = Task.Run(() => RunAsync(source.Token));
            return status;
        }
    }

    public async Task<FanCurveStatus> StopAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        lock(gate)
        {
            source = cts;
            task = worker;
            cts = null;
            worker = null;
            lastCpuTemp = null; lastGpuTemp = null;
            lastCpuLevel = null; lastGpuLevel = null;
        }

        if(source is not null)
        {
            source.Cancel();
            source.Dispose();
        }

        // Wait for the worker to fully exit before returning
        if(task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch(OperationCanceledException) { }
            catch(Exception) { }
        }

        lock(gate)
        {
            status = status with { Running = false, LastError = null };
            return status;
        }
    }

    // Synchronous stop for internal use (Start calls this before launching new worker)
    private FanCurveStatus Stop()
    {
        CancellationTokenSource? source;
        Task? task;
        lock(gate)
        {
            source = cts;
            task = worker;
            cts = null;
            worker = null;
            lastCpuTemp = null; lastGpuTemp = null;
            lastCpuLevel = null; lastGpuLevel = null;
        }

        if(source is not null)
        {
            source.Cancel();
            source.Dispose();
        }

        lock(gate)
        {
            status = status with { Running = false, LastError = null };
            return status;
        }
    }

    public FanCurveStatus Status()
    {
        lock(gate)
            return status;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while(!cancellationToken.IsCancellationRequested)
            {
                FanCurveSettings? current;
                lock(gate)
                    current = settings;

                if(current is null)
                    return;

                try
                {
                    tickCount++;
                    HardwareSnapshot currentSnapshot = await snapshot(cancellationToken).ConfigureAwait(false);

                    SensorValue? cpuSensor = TryGetTrustedTemperature(currentSnapshot, "cpu");
                    SensorValue? gpuSensor = TryGetTrustedTemperature(currentSnapshot, "gpu");
                    double? cpuTemp = cpuSensor?.Value;
                    double? gpuTemp = gpuSensor?.Value;

                    bool cpuOk = cpuTemp.HasValue;
                    bool gpuOk = gpuTemp.HasValue;

                    if(!cpuOk && !gpuOk)
                    {
                        // Both sources failed — retry once after a short delay
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        currentSnapshot = await snapshot(cancellationToken).ConfigureAwait(false);
                        cpuSensor = TryGetTrustedTemperature(currentSnapshot, "cpu");
                        gpuSensor = TryGetTrustedTemperature(currentSnapshot, "gpu");
                        cpuTemp = cpuSensor?.Value;
                        gpuTemp = gpuSensor?.Value;
                        cpuOk = cpuTemp.HasValue;
                        gpuOk = gpuTemp.HasValue;
                    }

                    if(!cpuOk && !gpuOk)
                    {
                        UpdateStatus(current, null, null, "No trusted temperature source is available.");
                        goto AfterWrite;
                    }

                    // PID-style interpolated fan levels with hysteresis
                    int cpuLevel = InterpolateLevel(current.Points, cpuTemp ?? gpuTemp!.Value, current.HysteresisC,
                        ref lastCpuTemp, ref lastCpuLevel, p => p.CpuLevel);
                    int gpuLevel = InterpolateLevel(current.Points, gpuTemp ?? cpuTemp!.Value, current.HysteresisC,
                        ref lastGpuTemp, ref lastGpuLevel, p => p.GpuLevel);

                    // Always write every tick — EC countdown resets fan to auto if we don't keep pushing
                    if(writeGate is not null)
                        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await controller.SetManualAsync(cpuLevel, gpuLevel, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeGate?.Release();
                    }
                    applyCount++;

                    double displayTemp = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);
                    string displaySource = FormatSource(cpuSensor, gpuSensor, cpuLevel, gpuLevel);
                    FanCurvePoint displayApplied = new(Temperature: (int)displayTemp, CpuLevel: cpuLevel, GpuLevel: gpuLevel);
                    UpdateStatus(current, displayTemp, displaySource, displayApplied, null);
                AfterWrite: ;
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch(HardwareControllerException hwEx) when(hwEx.Code == "hardware_access_denied")
                {
                    lock(gate)
                        status = FanCurveStatus.CreateRunning(current!, null, null, null, "hardware_access_denied: lost permission", tickCount, applyCount);
                    return;
                }
                catch(Exception ex)
                {
                    UpdateStatus(current, null, null, ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(current.IntervalSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch(Exception ex)
        {
            lock(gate)
                status = status with { Running = false, LastError = $"curve worker crashed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Interpolate fan level between curve anchor points for smooth, PID-style proportional control.
    /// Hysteresis applies only when level would decrease — prevents oscillation from small temp drops.
    /// </summary>
    private static int InterpolateLevel(
        IReadOnlyList<FanCurvePoint> points,
        double temperature,
        int hysteresisC,
        ref double? lastTemp,
        ref double? lastLevel,
        Func<FanCurvePoint, int> getLevel)
    {
        double target = Interpolate(points, temperature, getLevel);

        // Hysteresis: hold current level unless temp has dropped enough to warrant reducing
        if(lastTemp.HasValue && lastLevel.HasValue && target < lastLevel.Value)
        {
            if(temperature >= lastTemp.Value - hysteresisC)
                return (int)Math.Round(lastLevel.Value);
        }

        lastTemp = temperature;
        lastLevel = target;
        return (int)Math.Round(target);
    }

    /// <summary>Linear interpolation between curve anchor points.</summary>
    private static double Interpolate(IReadOnlyList<FanCurvePoint> points, double temperature, Func<FanCurvePoint, int> getLevel)
    {
        if(points.Count == 0) return 50;

        // Below first point: floor
        if(temperature <= points[0].Temperature)
            return getLevel(points[0]);

        // Above last point: cap
        if(temperature >= points[^1].Temperature)
            return getLevel(points[^1]);

        // Linear interpolation between bracketing points
        for(int i = 1; i < points.Count; i++)
        {
            if(temperature <= points[i].Temperature)
            {
                FanCurvePoint lo = points[i - 1];
                FanCurvePoint hi = points[i];
                double fraction = (temperature - lo.Temperature) / (hi.Temperature - lo.Temperature);
                return getLevel(lo) + fraction * (getLevel(hi) - getLevel(lo));
            }
        }

        return getLevel(points[^1]);
    }

    private void UpdateStatus(
        FanCurveSettings settings,
        double? temperature,
        string? source,
        string? error)
    {
        lock(gate)
        {
            status = FanCurveStatus.CreateRunning(settings, temperature, source, null, error, tickCount, applyCount);
        }
    }

    private void UpdateStatus(
        FanCurveSettings settings,
        double? temperature,
        string? source,
        FanCurvePoint? applied,
        string? error)
    {
        lock(gate)
        {
            status = FanCurveStatus.CreateRunning(settings, temperature, source, applied, error, tickCount, applyCount);
        }
    }

    private static SensorValue? TryGetTrustedTemperature(HardwareSnapshot snapshot, string key)
    {
        if(!snapshot.Temps.TryGetValue(key, out SensorValue? value))
            return null;
        if(!value.Trusted || value.Suspect || !string.Equals(value.Unit, "C", StringComparison.OrdinalIgnoreCase))
            return null;
        return value;
    }

    private static string FormatSource(SensorValue? cpu, SensorValue? gpu, int cpuLevel, int gpuLevel)
    {
        string cpuPart = cpu is not null ? $"CPU: {cpu.Source} ({cpu.Value:F0}°C → {cpuLevel})" : "CPU: unavailable";
        string gpuPart = gpu is not null ? $"GPU: {gpu.Source} ({gpu.Value:F0}°C → {gpuLevel})" : "GPU: unavailable";
        return $"{cpuPart}, {gpuPart}";
    }

    private static IReadOnlyList<FanCurvePoint> NormalizePoints(IReadOnlyList<FanCurvePoint>? points)
    {
        FanCurvePoint[] normalized = (points is { Count: > 0 } ? points : DefaultPoints)
            .OrderBy(point => point.Temperature)
            .ToArray();

        foreach(FanCurvePoint point in normalized)
        {
            ValidateLevel(point.CpuLevel, nameof(point.CpuLevel));
            ValidateLevel(point.GpuLevel, nameof(point.GpuLevel));
        }

        return normalized;
    }

    private static void ValidateLevel(int level, string name)
    {
        if(level is < 0 or > 100)
            throw new ArgumentOutOfRangeException(name, "Fan level must be between 0 and 100.");
    }

    // 8BAB BIOS fan level hardware cap is 63 (0x3F); values above are silently clamped
    private static readonly FanCurvePoint[] DefaultPoints =
    [
        new(45, 35, 35),
        new(55, 44, 44),
        new(65, 52, 52),
        new(75, 58, 58),
        new(85, 64, 64)
    ];
}

internal sealed record FanCurveSettings(
    IReadOnlyList<FanCurvePoint> Points,
    int IntervalSeconds,
    int HysteresisC);

internal sealed record FanCurveStatus(
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("points")] IReadOnlyList<FanCurvePoint> Points,
    [property: JsonPropertyName("intervalSeconds")] int IntervalSeconds,
    [property: JsonPropertyName("hysteresisC")] int HysteresisC,
    [property: JsonPropertyName("lastTemperature")] double? LastTemperature,
    [property: JsonPropertyName("lastTemperatureSource")] string? LastTemperatureSource,
    [property: JsonPropertyName("lastApplied")] FanCurvePoint? LastApplied,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("tickCount")] int TickCount,
    [property: JsonPropertyName("applyCount")] int ApplyCount,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp)
{
    public static FanCurveStatus Stopped() => new(false, [], 0, 0, null, null, null, null, 0, 0, DateTimeOffset.Now);

    public static FanCurveStatus CreateRunning(
        FanCurveSettings settings,
        double? temperature,
        string? source,
        FanCurvePoint? applied,
        string? error,
        int tickCount,
        int applyCount) =>
        new(true, settings.Points, settings.IntervalSeconds, settings.HysteresisC, temperature, source, applied, error, tickCount, applyCount, DateTimeOffset.Now);
}
