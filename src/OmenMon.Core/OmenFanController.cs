using OmenMon.Core.Hardware;
using System.Runtime.InteropServices;

namespace OmenMon.Core;

public sealed class OmenFanController : IHardwareController, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private OmenBiosClient? bios;
    private OmenEmbeddedController? ec;
    private bool ecOpenAttempted;
    private string? product;

    public void Dispose()
    {
        bios?.Dispose();
        ec?.Dispose();
        gate.Dispose();
    }

    public Task<HardwareSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        WithGateAsync(() => BuildSnapshot(), cancellationToken);

    public Task<HardwareSnapshot> SetAutoAsync(string? biosMode, CancellationToken cancellationToken) =>
        WithGateAsync(() =>
        {
            try
            {
                OmenBiosClient client = GetBios();
                client.SetMaxFan(false);
                if (TryParseFanMode(biosMode ?? "Default", out OmenFanMode mode))
                    client.SetFanMode(mode);
                else
                    throw new ArgumentException($"Unknown BIOS fan mode: {biosMode}");

                TryEcWrite(EcRegister.OMCC, 0x00);
                return BuildSnapshot();
            }
            catch (Exception ex) when (IsHardwareAccessException(ex))
            {
                throw ToHardwareControllerException(ex);
            }
        }, cancellationToken);

    public Task<HardwareSnapshot> SetMaxAsync(CancellationToken cancellationToken) =>
        WithGateAsync(() =>
        {
            try
            {
                GetBios().SetMaxFan(true);
                return BuildSnapshot();
            }
            catch (Exception ex) when (IsHardwareAccessException(ex))
            {
                throw ToHardwareControllerException(ex);
            }
        }, cancellationToken);

    public Task<HardwareSnapshot> SetManualAsync(int cpuLevel, int gpuLevel, CancellationToken cancellationToken) =>
        WithGateAsync(() =>
        {
            byte cpu = ValidateFanLevel(cpuLevel, nameof(cpuLevel));
            byte gpu = ValidateFanLevel(gpuLevel, nameof(gpuLevel));

            try
            {
                OmenBiosClient client = GetBios();
                client.SetMaxFan(false);
                client.SetFanLevel(cpu, gpu);
                return BuildSnapshot();
            }
            catch (Exception ex) when (IsHardwareAccessException(ex))
            {
                throw ToHardwareControllerException(ex);
            }
        }, cancellationToken);

    public Task<HardwareSnapshot> SetProgramAsync(string name, CancellationToken cancellationToken) =>
        WithGateAsync(() =>
        {
            try
            {
                OmenFanMode mode = ProgramToMode(name);
                OmenBiosClient client = GetBios();
                client.SetMaxFan(false);
                client.SetFanMode(mode);
                return BuildSnapshot();
            }
            catch (Exception ex) when (IsHardwareAccessException(ex))
            {
                throw ToHardwareControllerException(ex);
            }
        }, cancellationToken);

    private async Task<HardwareSnapshot> WithGateAsync(Func<HardwareSnapshot> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private HardwareSnapshot BuildSnapshot()
    {
        List<string> warnings = [];
        Dictionary<string, object?> raw = [];
        Dictionary<string, object?> biosFan = [];
        Dictionary<string, FanSnapshot> fans = [];
        Dictionary<string, SensorValue> temps = [];

        OmenBiosClient? client = null;
        try
        {
            client = GetBios();
            product ??= client.GetProduct();
        }
        catch (Exception ex)
        {
            warnings.Add("bios_unavailable");
            warnings.Add($"bios_error:{ex.Message}");
            product ??= TryReadProduct(warnings);
        }

        DeviceProfile profile = DeviceProfiles.ForProduct(product);

        byte[]? levels = client is null ? null : TryRead(() => client.GetFanLevel(), warnings, "bios_fan_level_unavailable");
        byte? fanCount = client is null ? null : TryRead(() => client.GetFanCount(), warnings, "bios_fan_count_unavailable");
        byte? fanType = client is null ? null : TryRead(() => client.GetFanType(), warnings, "bios_fan_type_unavailable");
        bool? maxFan = client is null ? null : TryRead(() => client.GetMaxFan(), warnings, "bios_max_fan_unavailable");
        byte? biosTemperature = client is null ? null : TryRead(() => client.GetTemperature(), warnings, "bios_temperature_unavailable");

        biosFan["count"] = fanCount;
        biosFan["type"] = fanType is null ? null : FormatFanType(fanType.Value);
        biosFan["level"] = levels is null ? null : $"{levels[0]}/{levels[1]}";
        biosFan["max"] = maxFan;

        EcReadings ecReadings = ReadEc(warnings);
        raw["ec"] = ecReadings.Raw;
        raw["fanMode"] = ecReadings.Mode;
        raw["manual"] = ecReadings.Manual;
        raw["countdown"] = ecReadings.Countdown;

        int? cpuLevel = levels?[0] ?? ecReadings.CpuLevel;
        int? gpuLevel = levels?[1] ?? ecReadings.GpuLevel;

        fans["cpu"] = new FanSnapshot(
            Rpm: ecReadings.CpuRpm,
            Rate: ecReadings.CpuRate,
            Level: cpuLevel,
            RpmTrusted: profile.FanRpmReliable,
            LevelTrusted: profile.FanLevelReliable && cpuLevel is not null);

        fans["gpu"] = new FanSnapshot(
            Rpm: ecReadings.GpuRpm,
            Rate: ecReadings.GpuRate,
            Level: gpuLevel,
            RpmTrusted: profile.FanRpmReliable,
            LevelTrusted: profile.FanLevelReliable && gpuLevel is not null);

        if (!profile.FanRpmReliable && ((ecReadings.CpuRpm == 0 && cpuLevel > 0) || (ecReadings.GpuRpm == 0 && gpuLevel > 0)))
            warnings.Add("rpm_unavailable");

        if (ecReadings.CpuTemp > 0)
        {
            AddOmenTemperature(temps, warnings, "cpu", ecReadings.CpuTemp, "EC:CPUT", trusted: true);
        }
        else if (biosTemperature is not null)
        {
            temps["cpu"] = new SensorValue(biosTemperature, "C", "BIOS", Trusted: true);
            warnings.Add("cpu_temp_bios_fallback");
        }
        else
        {
            AddOmenTemperature(temps, warnings, "cpu", ecReadings.CpuTemp, "EC:CPUT", trusted: false);
        }

        AddOmenTemperature(temps, warnings, "gpu", ecReadings.GpuTemp, "EC:GPTM", trusted: profile.OmenGpuTempReliable);
        if (ecReadings.GpuTemp is <= 5)
            warnings.Add("gpu_temp_suspect");

        return new HardwareSnapshot
        {
            Product = string.IsNullOrWhiteSpace(product) ? "unknown" : product,
            DeviceProfile = profile,
            Temps = temps,
            Fans = fans,
            BiosFan = biosFan,
            Warnings = warnings.Distinct().ToList(),
            Raw = raw
        };
    }

    private OmenBiosClient GetBios() => bios ??= new OmenBiosClient();

    private static string TryReadProduct(List<string> warnings)
    {
        try
        {
            return OmenBiosClient.ReadProduct();
        }
        catch (Exception ex)
        {
            warnings.Add("product_unavailable");
            warnings.Add($"product_error:{ex.Message}");
            return "unknown";
        }
    }

    private OmenEmbeddedController? GetEc(List<string>? warnings = null)
    {
        if (ec is not null)
            return ec;
        if (ecOpenAttempted)
            return null;

        ecOpenAttempted = true;
        try
        {
            ec = new OmenEmbeddedController();
            ec.Open();
            return ec;
        }
        catch (Exception ex)
        {
            warnings?.Add("ec_unavailable");
            warnings?.Add($"ec_error:{ex.Message}");
            ec?.Dispose();
            ec = null;
            return null;
        }
    }

    private EcReadings ReadEc(List<string> warnings)
    {
        OmenEmbeddedController? controller = GetEc(warnings);
        if (controller is null)
            return new EcReadings(new Dictionary<string, object?>());

        Dictionary<string, object?> raw = [];
        byte? ReadByte(EcRegister register)
        {
            try
            {
                byte value = controller.ReadByte(register);
                raw[register.ToString()] = value;
                return value;
            }
            catch (Exception ex)
            {
                warnings.Add($"ec_read_error:{register}:{ex.Message}");
                return null;
            }
        }

        ushort? ReadWord(EcRegister register)
        {
            try
            {
                ushort value = controller.ReadWord(register);
                raw[register.ToString()] = value;
                return value;
            }
            catch (Exception ex)
            {
                warnings.Add($"ec_read_error:{register}:{ex.Message}");
                return null;
            }
        }

        return new EcReadings(raw)
        {
            CpuRate = ReadByte(EcRegister.XGS1),
            GpuRate = ReadByte(EcRegister.XGS2),
            CpuLevel = ReadByte(EcRegister.SRP1),
            GpuLevel = ReadByte(EcRegister.SRP2),
            CpuRpm = ReadWord(EcRegister.RPM1),
            GpuRpm = ReadWord(EcRegister.RPM3),
            CpuTemp = ReadByte(EcRegister.CPUT),
            GpuTemp = ReadByte(EcRegister.GPTM),
            Mode = ReadByte(EcRegister.HPCM),
            Manual = ReadByte(EcRegister.OMCC),
            Countdown = ReadByte(EcRegister.XFCD)
        };
    }

    private void TryEcWrite(EcRegister register, byte value)
    {
        OmenEmbeddedController? controller = GetEc();
        try
        {
            controller?.WriteByte(register, value);
        }
        catch
        {
        }
    }

    private static T? TryRead<T>(Func<T> read, List<string> warnings, string warning)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            warnings.Add(warning);
            warnings.Add($"{warning}:{ex.Message}");
            return default;
        }
    }

    private static void AddOmenTemperature(
        Dictionary<string, SensorValue> temps,
        List<string> warnings,
        string name,
        int? value,
        string source,
        bool trusted)
    {
        if (value is null)
            return;

        bool suspect = !trusted || value <= 5;
        temps[name] = new SensorValue(value, "C", source, trusted && !suspect, suspect);
        if (suspect)
            warnings.Add($"{name}_temp_suspect");
    }

    private static byte ValidateFanLevel(int level, string paramName)
    {
        if (level is < 0 or > 100)
            throw new ArgumentOutOfRangeException(paramName, "Fan level must be between 0 and 100.");
        return (byte)level;
    }

    private static OmenFanMode ProgramToMode(string name)
    {
        if (string.Equals(name, "Power", StringComparison.OrdinalIgnoreCase))
            return OmenFanMode.Performance;
        if (string.Equals(name, "Silent", StringComparison.OrdinalIgnoreCase))
            return OmenFanMode.Default;
        if (TryParseFanMode(name, out OmenFanMode mode))
            return mode;

        throw new ArgumentException($"Unknown fan program: {name}");
    }

    private static bool TryParseFanMode(string name, out OmenFanMode mode) =>
        Enum.TryParse(name, ignoreCase: true, out mode);

    private static bool IsHardwareAccessException(Exception ex) =>
        ex is OmenBiosException or OmenDriverException or COMException or UnauthorizedAccessException;

    private static HardwareControllerException ToHardwareControllerException(Exception ex)
    {
        int hresult = Marshal.GetHRForException(ex);
        string message = ex.Message;
        bool denied = hresult == unchecked((int)0x80070005)
            || message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Run the agent as administrator", StringComparison.OrdinalIgnoreCase);

        return denied
            ? new HardwareControllerException("hardware_access_denied", "Hardware access was denied. Run the agent as administrator.")
            : new HardwareControllerException("hardware_error", message);
    }

    private static string FormatFanType(byte raw)
    {
        OmenFanType first = (OmenFanType)(raw & 0x0F);
        OmenFanType second = (OmenFanType)((raw >> 4) & 0x0F);
        return $"{first}/{second}";
    }

    private sealed record EcReadings(Dictionary<string, object?> Raw)
    {
        public int? CpuRate { get; init; }
        public int? GpuRate { get; init; }
        public int? CpuLevel { get; init; }
        public int? GpuLevel { get; init; }
        public int? CpuRpm { get; init; }
        public int? GpuRpm { get; init; }
        public int? CpuTemp { get; init; }
        public int? GpuTemp { get; init; }
        public int? Mode { get; init; }
        public int? Manual { get; init; }
        public int? Countdown { get; init; }
    }
}
