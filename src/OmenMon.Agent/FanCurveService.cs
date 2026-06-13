using OmenMon.Core;

namespace OmenMon.Agent;

internal sealed class FanCurveService
{
    private readonly IHardwareController controller;
    private readonly Func<CancellationToken, Task<HardwareSnapshot>> snapshot;
    private readonly object gate = new();

    private CancellationTokenSource? cts;
    private Task? worker;
    private FanCurveSettings? settings;
    private FanCurveStatus status = FanCurveStatus.Stopped();
    private (int Cpu, int Gpu)? lastApplied;

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
            IntervalSeconds: Math.Clamp(intervalSeconds ?? 5, 1, 60));

        Stop();

        CancellationTokenSource source = new();
        lock(gate)
        {
            settings = next;
            status = FanCurveStatus.CreateRunning(next, null, null, null, null);
            lastApplied = null;
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
            lastApplied = null;
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
                HardwareSnapshot currentSnapshot = await snapshot(cancellationToken).ConfigureAwait(false);
                SensorValue? sensor = SelectTemperature(currentSnapshot);
                if(sensor?.Value is null)
                {
                    UpdateStatus(current, null, null, null, "No trusted temperature source is available.");
                }
                else
                {
                    FanCurvePoint target = SelectPoint(current.Points, sensor.Value.Value);
                    if(lastApplied is null || lastApplied.Value.Cpu != target.CpuLevel || lastApplied.Value.Gpu != target.GpuLevel)
                    {
                        await controller.SetManualAsync(target.CpuLevel, target.GpuLevel, cancellationToken).ConfigureAwait(false);
                        lastApplied = (target.CpuLevel, target.GpuLevel);
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
            status = FanCurveStatus.CreateRunning(settings, temperature, source, applied, error);
        }
    }

    private static SensorValue? SelectTemperature(HardwareSnapshot snapshot)
    {
        return snapshot.Temps.Values
            .Where(value => value.Trusted && !value.Suspect && string.Equals(value.Unit, "C", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Value)
            .FirstOrDefault();
    }

    private static FanCurvePoint SelectPoint(IReadOnlyList<FanCurvePoint> points, double temperature)
    {
        FanCurvePoint selected = points[0];
        foreach(FanCurvePoint point in points)
        {
            if(temperature >= point.Temperature)
                selected = point;
            else
                break;
        }

        return selected;
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
    int IntervalSeconds);

internal sealed record FanCurveStatus(
    bool Running,
    IReadOnlyList<FanCurvePoint> Points,
    int IntervalSeconds,
    double? LastTemperature,
    string? LastTemperatureSource,
    FanCurvePoint? LastApplied,
    string? LastError,
    DateTimeOffset Timestamp)
{
    public static FanCurveStatus Stopped() => new(false, [], 0, null, null, null, null, DateTimeOffset.Now);

    public static FanCurveStatus CreateRunning(
        FanCurveSettings settings,
        double? temperature,
        string? source,
        FanCurvePoint? applied,
        string? error) =>
        new(true, settings.Points, settings.IntervalSeconds, temperature, source, applied, error, DateTimeOffset.Now);
}
