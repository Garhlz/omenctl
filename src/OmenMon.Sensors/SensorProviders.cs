using OmenMon.Core;

namespace OmenMon.Sensors;

public interface ISensorProvider
{
    string Name { get; }
    Task<IReadOnlyCollection<SensorSample>> ReadAsync(CancellationToken cancellationToken);
}

public sealed record SensorSample(
    string Key,
    double? Value,
    string Unit,
    string Source,
    bool Trusted,
    bool Suspect = false);

public sealed class LibreHardwareMonitorProvider : ISensorProvider
{
    public string Name => "LibreHardwareMonitor";

    public Task<IReadOnlyCollection<SensorSample>> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<SensorSample>>([]);
}

public sealed class NvmlProvider : ISensorProvider
{
    public string Name => "NVML";

    public Task<IReadOnlyCollection<SensorSample>> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<SensorSample>>([]);
}

public sealed class SensorFusionService
{
    private readonly IReadOnlyCollection<ISensorProvider> providers;

    public SensorFusionService(IReadOnlyCollection<ISensorProvider> providers)
    {
        this.providers = providers;
    }

    public async Task MergeIntoAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach(ISensorProvider provider in providers)
        {
            foreach(SensorSample sample in await provider.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                SensorValue value = new(sample.Value, sample.Unit, sample.Source, sample.Trusted, sample.Suspect);
                if(sample.Unit == "%")
                    snapshot.Loads[sample.Key] = value;
                else
                    snapshot.Temps[sample.Key] = value;
            }
        }
    }
}

