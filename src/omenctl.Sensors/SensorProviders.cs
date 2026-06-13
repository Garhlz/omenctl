using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;
using OmenCtl.Core;

namespace OmenCtl.Sensors;

public interface ISensorProvider
{
    string Name { get; }
    Task<SensorReadResult> ReadAsync(CancellationToken cancellationToken);
}

public enum SensorSampleKind
{
    Temperature,
    Load,
    Metric
}

public sealed record SensorSample(
    string Key,
    double? Value,
    string Unit,
    string Source,
    bool Trusted,
    bool Suspect = false,
    SensorSampleKind Kind = SensorSampleKind.Temperature);

public sealed record SensorReadResult(
    IReadOnlyCollection<SensorSample> Samples,
    IReadOnlyCollection<string> Warnings,
    IReadOnlyDictionary<string, object?> Raw)
{
    public static SensorReadResult Empty { get; } = new([], [], new Dictionary<string, object?>());
}

public sealed class LibreHardwareMonitorProvider : ISensorProvider
{
    public string Name => "LibreHardwareMonitor";

    public Task<SensorReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Computer computer = new()
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true
            };
            try
            {
                computer.Open();
                List<IHardware> hardware = EnumerateHardware(computer).ToList();
                List<SensorSample> samples = [];

                AddCpuSamples(samples, hardware);
                AddGpuSamples(samples, hardware);
                AddBoardSamples(samples, hardware);
                AddStorageSamples(samples, hardware);

                return Task.FromResult(new SensorReadResult(samples, [], new Dictionary<string, object?>()));
            }
            finally
            {
                computer.Close();
            }
        }
        catch(Exception ex)
        {
            return Task.FromResult(new SensorReadResult([], ["lhm_unavailable"], new Dictionary<string, object?>()
            {
                ["error"] = ex.Message
            }));
        }
    }

    private static IEnumerable<IHardware> EnumerateHardware(Computer computer)
    {
        foreach(IHardware hardware in computer.Hardware)
        {
            UpdateRecursive(hardware);
            foreach(IHardware item in Flatten(hardware))
                yield return item;
        }
    }

    private static IEnumerable<IHardware> Flatten(IHardware hardware)
    {
        yield return hardware;
        foreach(IHardware subHardware in hardware.SubHardware)
        {
            UpdateRecursive(subHardware);
            foreach(IHardware item in Flatten(subHardware))
                yield return item;
        }
    }

    private static void UpdateRecursive(IHardware hardware)
    {
        hardware.Update();
        foreach(IHardware subHardware in hardware.SubHardware)
            UpdateRecursive(subHardware);
    }

    private static void AddCpuSamples(List<SensorSample> samples, IReadOnlyCollection<IHardware> hardware)
    {
        IHardware[] cpuHardware = hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
        if(cpuHardware.Length == 0)
            return;

        ISensor[] cpuSensors = cpuHardware.SelectMany(h => h.Sensors).ToArray();
        if(TrySelectSensor(cpuSensors, SensorType.Temperature, out ISensor? cpuTemp, "CPU Package", "Package", "Core Max"))
        {
            samples.Add(ToSample("cpu", cpuTemp!, "LHM"));
        }

        if(TrySelectSensor(cpuSensors, SensorType.Load, out ISensor? cpuLoad, "CPU Total", "Total"))
        {
            samples.Add(ToSample("cpu", cpuLoad!, "LHM", SensorSampleKind.Load));
        }
    }

    private static void AddGpuSamples(List<SensorSample> samples, IReadOnlyCollection<IHardware> hardware)
    {
        IHardware[] gpuHardware = hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .ToArray();
        if(gpuHardware.Length == 0)
            return;

        ISensor[] gpuSensors = gpuHardware.SelectMany(h => h.Sensors).ToArray();
        if(TrySelectSensor(gpuSensors, SensorType.Temperature, out ISensor? gpuTemp, "GPU Core", "Core"))
        {
            samples.Add(ToSample("gpu", gpuTemp!, "LHM"));
        }

        if(TrySelectSensor(gpuSensors, SensorType.Load, out ISensor? gpuLoad, "GPU Core", "D3D 3D", "GPU Total"))
        {
            samples.Add(ToSample("gpu", gpuLoad!, "LHM", SensorSampleKind.Load));
        }
    }

    private static void AddBoardSamples(List<SensorSample> samples, IReadOnlyCollection<IHardware> hardware)
    {
        IHardware? board = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        if(board is null)
            return;

        if(TrySelectSensor(board.Sensors, SensorType.Temperature, out ISensor? boardTemp))
        {
            samples.Add(ToSample("motherboard", boardTemp!, "LHM"));
        }
    }

    private static void AddStorageSamples(List<SensorSample> samples, IReadOnlyCollection<IHardware> hardware)
    {
        IHardware[] storage = hardware.Where(h => h.HardwareType == HardwareType.Storage).ToArray();
        if(storage.Length == 0)
            return;

        ISensor? driveTemp = storage
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is not null)
            .OrderByDescending(s => s.Value)
            .FirstOrDefault();

        if(driveTemp is not null)
            samples.Add(ToSample("ssd", driveTemp, "LHM"));
    }

    private static bool TrySelectSensor(
        IEnumerable<ISensor> sensors,
        SensorType type,
        out ISensor? sensor,
        params string[] preferredNames)
    {
        ISensor[] candidates = sensors
            .Where(s => s.SensorType == type && s.Value is not null)
            .ToArray();

        foreach(string preferred in preferredNames)
        {
            sensor = candidates.FirstOrDefault(s => s.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if(sensor is not null)
                return true;
        }

        sensor = type switch
        {
            SensorType.Temperature => candidates.OrderByDescending(s => s.Value).FirstOrDefault(),
            _ => candidates.FirstOrDefault()
        };
        return sensor is not null;
    }

    private static SensorSample ToSample(string key, ISensor sensor, string providerPrefix, SensorSampleKind kind = SensorSampleKind.Temperature)
    {
        string unit = kind switch
        {
            SensorSampleKind.Load => "%",
            _ => sensor.SensorType == SensorType.Temperature ? "C" : sensor.SensorType.ToString()
        };

        return new SensorSample(
            Key: key,
            Value: sensor.Value,
            Unit: unit,
            Source: $"{providerPrefix}:{sensor.Name}",
            Trusted: true,
            Suspect: false,
            Kind: kind);
    }
}

public sealed class NvmlProvider : ISensorProvider
{
    public string Name => "NVML";

    public Task<SensorReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            NvmlNative.Initialize();
        }
        catch(Exception ex)
        {
            return Task.FromResult(new SensorReadResult([], ["nvml_unavailable"], new Dictionary<string, object?>()
            {
                ["error"] = ex.Message
            }));
        }

        try
        {
            uint count = NvmlNative.GetDeviceCount();
            if(count == 0)
                return Task.FromResult(new SensorReadResult([], ["nvml_no_device"], new Dictionary<string, object?>()));

            NvmlDeviceSnapshot device = NvmlNative.ReadDevice(0);
            List<SensorSample> samples =
            [
                new("gpu", device.TemperatureCelsius, "C", $"NVML:{device.Name}", Trusted: true),
                new("gpu", device.GpuUtilizationPercent, "%", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Load),
                new("gpuMemory", device.MemoryUtilizationPercent, "%", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Load),
                new("gpuPowerW", device.PowerWatts, "W", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Metric),
                new("gpuCoreClockMHz", device.GraphicsClockMHz, "MHz", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Metric),
                new("gpuMemoryClockMHz", device.MemoryClockMHz, "MHz", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Metric),
                new("gpuMemoryUsedMiB", device.MemoryUsedMiB, "MiB", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Metric),
                new("gpuMemoryTotalMiB", device.MemoryTotalMiB, "MiB", $"NVML:{device.Name}", Trusted: true, Kind: SensorSampleKind.Metric)
            ];

            Dictionary<string, object?> raw = new()
            {
                ["deviceCount"] = count,
                ["deviceName"] = device.Name
            };

            return Task.FromResult(new SensorReadResult(samples, [], raw));
        }
        catch(Exception ex)
        {
            return Task.FromResult(new SensorReadResult([], ["nvml_read_failed"], new Dictionary<string, object?>()
            {
                ["error"] = ex.Message
            }));
        }
        finally
        {
            NvmlNative.TryShutdown();
        }
    }
}

public sealed class SensorFusionService
{
    private static readonly string[] DerivedWarnings =
    [
        "cpu_temp_bios_fallback",
        "cpu_temp_suspect",
        "cpu_temp_unavailable",
        "gpu_temp_suspect",
        "gpu_temp_unavailable"
    ];

    private readonly IReadOnlyCollection<ISensorProvider> providers;

    public SensorFusionService(IReadOnlyCollection<ISensorProvider> providers)
    {
        this.providers = providers;
    }

    public async Task MergeIntoAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken)
    {
        List<SensorSample> providerSamples = [];
        Dictionary<string, object?> providerRaw = [];
        HashSet<string> warnings = [.. snapshot.Warnings];

        foreach(ISensorProvider provider in providers)
        {
            SensorReadResult result = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
            providerSamples.AddRange(result.Samples);
            warnings.UnionWith(result.Warnings);
            if(result.Raw.Count > 0)
                providerRaw[provider.Name] = new Dictionary<string, object?>(result.Raw);
        }

        IReadOnlyList<SensorSample> temperatureCandidates = GetExistingSamples(snapshot.Temps, SensorSampleKind.Temperature)
            .Concat(providerSamples.Where(sample => sample.Kind == SensorSampleKind.Temperature))
            .ToList();

        IReadOnlyList<SensorSample> loadCandidates = GetExistingSamples(snapshot.Loads, SensorSampleKind.Load)
            .Concat(providerSamples.Where(sample => sample.Kind == SensorSampleKind.Load))
            .ToList();

        Dictionary<string, OmenCtl.Core.SensorValue> mergedTemps = MergeByKey(temperatureCandidates);
        Dictionary<string, OmenCtl.Core.SensorValue> mergedLoads = MergeByKey(loadCandidates);

        snapshot.Temps.Clear();
        foreach(KeyValuePair<string, OmenCtl.Core.SensorValue> item in mergedTemps)
            snapshot.Temps[item.Key] = item.Value;

        snapshot.Loads.Clear();
        foreach(KeyValuePair<string, OmenCtl.Core.SensorValue> item in mergedLoads)
            snapshot.Loads[item.Key] = item.Value;

        MergeMetricSamples(snapshot, providerSamples.Where(sample => sample.Kind == SensorSampleKind.Metric));
        if(providerRaw.Count > 0)
            snapshot.Raw["sensorProviders"] = providerRaw;

        warnings.RemoveWhere(warning => DerivedWarnings.Contains(warning, StringComparer.Ordinal));
        ApplyTemperatureWarnings(snapshot, warnings);

        snapshot.Warnings.Clear();
        snapshot.Warnings.AddRange(warnings.Order(StringComparer.Ordinal));
    }

    private static IReadOnlyList<SensorSample> GetExistingSamples(
        IReadOnlyDictionary<string, OmenCtl.Core.SensorValue> values,
        SensorSampleKind kind) =>
        values.Select(pair => new SensorSample(
            pair.Key,
            pair.Value.Value,
            pair.Value.Unit,
            pair.Value.Source,
            pair.Value.Trusted,
            pair.Value.Suspect,
            kind)).ToList();

    private static Dictionary<string, OmenCtl.Core.SensorValue> MergeByKey(IReadOnlyList<SensorSample> samples)
    {
        Dictionary<string, OmenCtl.Core.SensorValue> merged = [];
        foreach(IGrouping<string, SensorSample> group in samples.GroupBy(sample => sample.Key, StringComparer.OrdinalIgnoreCase))
        {
            SensorSample best = group
                .OrderByDescending(sample => GetSampleScore(group.Key, sample))
                .ThenByDescending(sample => sample.Value)
                .First();

            merged[group.Key] = new OmenCtl.Core.SensorValue(best.Value, best.Unit, best.Source, best.Trusted, best.Suspect);
        }

        return merged;
    }

    private static int GetSampleScore(string key, SensorSample sample)
    {
        int score = GetSourceRank(key, sample.Source) * 100;
        if(sample.Trusted)
            score += 10;
        if(!sample.Suspect)
            score += 5;
        if(sample.Value is not null)
            score += 1;
        return score;
    }

    private static int GetSourceRank(string key, string source)
    {
        if(key.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            if(source.StartsWith("LHM:", StringComparison.OrdinalIgnoreCase))
                return 4;
            if(source.Equals("BIOS", StringComparison.OrdinalIgnoreCase))
                return 3;
            if(source.StartsWith("EC:", StringComparison.OrdinalIgnoreCase))
                return 2;
        }

        if(key.Equals("gpu", StringComparison.OrdinalIgnoreCase))
        {
            if(source.StartsWith("NVML:", StringComparison.OrdinalIgnoreCase))
                return 5;
            if(source.StartsWith("LHM:", StringComparison.OrdinalIgnoreCase))
                return 4;
            if(source.StartsWith("EC:", StringComparison.OrdinalIgnoreCase))
                return 1;
        }

        if(source.StartsWith("NVML:", StringComparison.OrdinalIgnoreCase))
            return 4;
        if(source.StartsWith("LHM:", StringComparison.OrdinalIgnoreCase))
            return 3;
        if(source.Equals("BIOS", StringComparison.OrdinalIgnoreCase))
            return 2;
        if(source.StartsWith("EC:", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }

    private static void ApplyTemperatureWarnings(HardwareSnapshot snapshot, ISet<string> warnings)
    {
        if(!snapshot.Temps.TryGetValue("cpu", out OmenCtl.Core.SensorValue? cpu) || cpu.Value is null)
        {
            warnings.Add("cpu_temp_unavailable");
        }
        else
        {
            if(cpu.Source.Equals("BIOS", StringComparison.OrdinalIgnoreCase))
                warnings.Add("cpu_temp_bios_fallback");
            if(!cpu.Trusted || cpu.Suspect)
                warnings.Add("cpu_temp_suspect");
        }

        if(!snapshot.Temps.TryGetValue("gpu", out OmenCtl.Core.SensorValue? gpu) || gpu.Value is null)
        {
            warnings.Add("gpu_temp_unavailable");
        }
        else if(!gpu.Trusted || gpu.Suspect)
        {
            warnings.Add("gpu_temp_suspect");
        }
    }

    private static void MergeMetricSamples(HardwareSnapshot snapshot, IEnumerable<SensorSample> metrics)
    {
        Dictionary<string, object?> bucket = snapshot.Raw.TryGetValue("sensors", out object? existing)
            && existing is Dictionary<string, object?> dictionary
            ? dictionary
            : [];

        foreach(SensorSample metric in metrics)
        {
            bucket[metric.Key] = new Dictionary<string, object?>
            {
                ["value"] = metric.Value,
                ["unit"] = metric.Unit,
                ["source"] = metric.Source,
                ["trusted"] = metric.Trusted,
                ["suspect"] = metric.Suspect
            };
        }

        if(bucket.Count > 0)
            snapshot.Raw["sensors"] = bucket;
    }
}

internal readonly record struct NvmlDeviceSnapshot(
    string Name,
    double TemperatureCelsius,
    double GpuUtilizationPercent,
    double MemoryUtilizationPercent,
    double PowerWatts,
    double GraphicsClockMHz,
    double MemoryClockMHz,
    double MemoryUsedMiB,
    double MemoryTotalMiB);

internal static class NvmlNative
{
    private const uint NvmlTemperatureGpu = 0;
    private const uint NvmlClockGraphics = 0;
    private const uint NvmlClockMemory = 2;

    private static bool initialized;

    public static void Initialize()
    {
        if(initialized)
            return;

        ThrowIfFailed(nvmlInit_v2(), "nvmlInit_v2");
        initialized = true;
    }

    public static void TryShutdown()
    {
        if(!initialized)
            return;

        nvmlShutdown();
        initialized = false;
    }

    public static uint GetDeviceCount()
    {
        ThrowIfFailed(nvmlDeviceGetCount_v2(out uint count), "nvmlDeviceGetCount_v2");
        return count;
    }

    public static NvmlDeviceSnapshot ReadDevice(uint index)
    {
        ThrowIfFailed(nvmlDeviceGetHandleByIndex_v2(index, out IntPtr device), "nvmlDeviceGetHandleByIndex_v2");

        StringBuilder name = new(96);
        ThrowIfFailed(nvmlDeviceGetName(device, name, (uint)name.Capacity), "nvmlDeviceGetName");

        ThrowIfFailed(nvmlDeviceGetTemperature(device, NvmlTemperatureGpu, out uint temperature), "nvmlDeviceGetTemperature");
        ThrowIfFailed(nvmlDeviceGetUtilizationRates(device, out NvmlUtilization utilization), "nvmlDeviceGetUtilizationRates");
        ThrowIfFailed(nvmlDeviceGetMemoryInfo(device, out NvmlMemory memory), "nvmlDeviceGetMemoryInfo");
        ThrowIfFailed(nvmlDeviceGetPowerUsage(device, out uint powerMilliwatts), "nvmlDeviceGetPowerUsage");
        ThrowIfFailed(nvmlDeviceGetClockInfo(device, NvmlClockGraphics, out uint graphicsClock), "nvmlDeviceGetClockInfo(graphics)");
        ThrowIfFailed(nvmlDeviceGetClockInfo(device, NvmlClockMemory, out uint memoryClock), "nvmlDeviceGetClockInfo(memory)");

        double memoryPercent = memory.Total == 0 ? 0 : (double)memory.Used / memory.Total * 100.0;

        return new NvmlDeviceSnapshot(
            Name: name.ToString(),
            TemperatureCelsius: temperature,
            GpuUtilizationPercent: utilization.Gpu,
            MemoryUtilizationPercent: memoryPercent,
            PowerWatts: powerMilliwatts / 1000.0,
            GraphicsClockMHz: graphicsClock,
            MemoryClockMHz: memoryClock,
            MemoryUsedMiB: memory.Used / 1024.0 / 1024.0,
            MemoryTotalMiB: memory.Total / 1024.0 / 1024.0);
    }

    private static void ThrowIfFailed(NvmlReturn code, string operation)
    {
        if(code == NvmlReturn.Success)
            return;

        throw new InvalidOperationException($"{operation} failed: {code}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private enum NvmlReturn
    {
        Success = 0
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlInit_v2")]
    private static extern NvmlReturn nvmlInit_v2();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlShutdown")]
    private static extern NvmlReturn nvmlShutdown();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern NvmlReturn nvmlDeviceGetCount_v2(out uint count);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern NvmlReturn nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern NvmlReturn nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clock);
}
