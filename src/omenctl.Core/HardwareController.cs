namespace OmenCtl.Core;

public interface IHardwareController
{
    Task<HardwareSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<HardwareSnapshot> SetAutoAsync(string? biosMode, CancellationToken cancellationToken);
    Task<HardwareSnapshot> SetMaxAsync(CancellationToken cancellationToken);
    Task<HardwareSnapshot> SetManualAsync(int cpuLevel, int gpuLevel, CancellationToken cancellationToken);
    Task<HardwareSnapshot> SetProgramAsync(string name, CancellationToken cancellationToken);
}

public sealed class HardwareControllerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class PlaceholderHardwareController : IHardwareController
{
    public Task<HardwareSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        HardwareSnapshot snapshot = new()
        {
            Product = "unknown",
            DeviceProfile = DeviceProfiles.ForProduct(null),
            Warnings = ["hardware_controller_not_wired"],
            Raw = new Dictionary<string, object?>
            {
                ["state"] = "placeholder",
                ["message"] = "Omen fan controller extraction is pending."
            }
        };
        return Task.FromResult(snapshot);
    }

    public Task<HardwareSnapshot> SetAutoAsync(string? biosMode, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Omen fan controller extraction is pending.");

    public Task<HardwareSnapshot> SetMaxAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("Omen fan controller extraction is pending.");

    public Task<HardwareSnapshot> SetManualAsync(int cpuLevel, int gpuLevel, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Omen fan controller extraction is pending.");

    public Task<HardwareSnapshot> SetProgramAsync(string name, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Omen fan controller extraction is pending.");
}
