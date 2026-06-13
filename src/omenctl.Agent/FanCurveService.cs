using System.Text.Json.Serialization;
using OmenCtl.Core;

namespace OmenCtl.Agent;

internal sealed class FanCurveService
{
    private readonly IHardwareController controller;
    private readonly Func<CancellationToken, Task<HardwareSnapshot>> snapshot;
    private readonly object gate = new();

    private CancellationTokenSource? cts;
    private Task? worker;
    private FanCurveSettings? settings;
    private FanCurveStatus status = FanCurveStatus.Stopped();
    private FanCurvePoint? lastAppliedPoint;
    private int tickCount;
    private int applyCount;

    public FanCurveService(
        IHardwareController controller,
        Func<CancellationToken, Task<HardwareSnapshot>> snapshot)
    {
        this.controller = controller;
        this.snapshot = snapshot;
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
            lastAppliedPoint = null;
            tickCount = 0;
            applyCount = 0;
            cts = source;
            worker = Task.Run(() => RunAsync(source.Token));
            return status;
        }
    }

    public FanCurveStatus Stop()
    {
        CancellationTokenSource? source;
        Task? task;
        lock(gate)
        {
            source = cts;
            task = worker;
            cts = null;
            worker = null;
            lastAppliedPoint = null;
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
                SensorValue? sensor = SelectTemperature(currentSnapshot);
                if(sensor?.Value is null)
                {
                    UpdateStatus(current, null, null, lastAppliedPoint, "No trusted temperature source is available.");
                }
                else
                {
                    FanCurvePoint target = SelectPoint(current.Points, sensor.Value.Value, current.HysteresisC, lastAppliedPoint);
                    if(lastAppliedPoint is null
                        || lastAppliedPoint.CpuLevel != target.CpuLevel
                        || lastAppliedPoint.GpuLevel != target.GpuLevel
                        || lastAppliedPoint.Temperature != target.Temperature)
                    {
                        await controller.SetManualAsync(target.CpuLevel, target.GpuLevel, cancellationToken).ConfigureAwait(false);
                        lastAppliedPoint = target;
                        applyCount++;
                    }

                    UpdateStatus(current, sensor.Value.Value, sensor.Source, target, null);
                }
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch(Exception ex)
            {
                UpdateStatus(current, null, null, null, ex.Message);
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

    private static SensorValue? SelectTemperature(HardwareSnapshot snapshot)
    {
        SensorValue? cpu = TryGetTrustedTemperature(snapshot, "cpu");
        SensorValue? gpu = TryGetTrustedTemperature(snapshot, "gpu");

        return (cpu?.Value, gpu?.Value) switch
        {
            (double cpuTemp, double gpuTemp) => cpuTemp >= gpuTemp ? cpu : gpu,
            (double, null) => cpu,
            (null, double) => gpu,
            _ => null
        };
    }

    private static SensorValue? TryGetTrustedTemperature(HardwareSnapshot snapshot, string key)
    {
        if(!snapshot.Temps.TryGetValue(key, out SensorValue? value))
            return null;
        if(!value.Trusted || value.Suspect || !string.Equals(value.Unit, "C", StringComparison.OrdinalIgnoreCase))
            return null;
        return value;
    }

    private static FanCurvePoint SelectPoint(
        IReadOnlyList<FanCurvePoint> points,
        double temperature,
        int hysteresisC,
        FanCurvePoint? currentApplied)
    {
        int baseIndex = 0;
        for(int index = 0; index < points.Count; index++)
        {
            if(temperature >= points[index].Temperature)
                baseIndex = index;
            else
                break;
        }

        if(currentApplied is null)
            return points[baseIndex];

        int currentIndex = Array.FindIndex(points.ToArray(), point =>
            point.Temperature == currentApplied.Temperature
            && point.CpuLevel == currentApplied.CpuLevel
            && point.GpuLevel == currentApplied.GpuLevel);

        if(currentIndex < 0 || baseIndex >= currentIndex)
            return points[baseIndex];

        double downshiftThreshold = points[currentIndex].Temperature - hysteresisC;
        return temperature >= downshiftThreshold ? points[currentIndex] : points[baseIndex];
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

    private static readonly FanCurvePoint[] DefaultPoints =
    [
        new(45, 35, 35),
        new(55, 45, 45),
        new(65, 50, 50),
        new(75, 55, 58),
        new(85, 60, 63)
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
