using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OmenMon.Core.Hardware;

internal enum OmenFanMode : byte
{
    LegacyDefault = 0x00,
    LegacyPerformance = 0x01,
    LegacyCool = 0x02,
    LegacyQuiet = 0x03,
    LegacyExtreme = 0x04,
    Default = 0x30,
    Performance = 0x31,
    Cool = 0x50
}

internal enum OmenFanType : byte
{
    Unsupported = 0x00,
    Cpu = 0x01,
    Gpu = 0x02,
    Exhaust = 0x03,
    Pump = 0x04,
    Intake = 0x05
}

internal sealed class OmenBiosException(string message) : Exception(message);

internal sealed class OmenBiosClient : IDisposable
{
    private static readonly byte[] Sign = [0x53, 0x45, 0x43, 0x55];
    private const string NamespaceName = "root\\wmi";
    private const string CimV2NamespaceName = "root\\cimv2";
    private const string DataClassName = "hpqBDataIn";
    private const string MethodClassName = "hpqBIntM";
    private const string DataFieldName = "hpqBData";
    private const string ReturnCodeFieldName = "rwReturnCode";

    private readonly object locator;
    private readonly object services;
    private readonly object methodInstance;

    public OmenBiosClient()
    {
        Type locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
            ?? throw new OmenBiosException("WMI scripting locator is not available.");
        locator = Activator.CreateInstance(locatorType)
            ?? throw new OmenBiosException("Could not create WMI scripting locator.");
        services = Connect(locator, NamespaceName);
        methodInstance = First(ExecQuery(services, $"SELECT * FROM {MethodClassName}"))
            ?? throw new OmenBiosException($"WMI class {MethodClassName} did not return an instance.");
    }

    public void Dispose()
    {
        Release(methodInstance);
        Release(services);
        Release(locator);
    }

    public byte GetFanCount() => SendRead(0x20008, 0x10, [0, 0, 0, 0], 4)[0];

    public byte GetFanType() => SendRead(0x20008, 0x2C, [0, 0, 0, 0], 128)[0];

    public byte[] GetFanLevel()
    {
        byte[] data = SendRead(0x20008, 0x2D, [0, 0, 0, 0], 128);
        return [data[0], data[1]];
    }

    public void SetFanLevel(byte cpuLevel, byte gpuLevel) =>
        SendWrite(0x20008, 0x2E, [cpuLevel, gpuLevel, 0, 0], forceCheck: true);

    public void SetFanMode(OmenFanMode mode) =>
        SendWrite(0x20008, 0x1A, [0xFF, (byte)mode, 0, 0]);

    public bool GetMaxFan() => (SendRead(0x20008, 0x26, [0, 0, 0, 0], 4)[0] & 0x01) != 0;

    public void SetMaxFan(bool enabled) =>
        SendWrite(0x20008, 0x27, [enabled ? (byte)0x01 : (byte)0x00, 0, 0, 0]);

    public byte GetTemperature() => SendRead(0x20008, 0x23, [1, 0, 0, 0], 4)[0];

    public string GetProduct()
    {
        return ReadProduct(locator);
    }

    public static string ReadProduct()
    {
        string registryProduct = ReadProductFromRegistry();
        if(!string.Equals(registryProduct, "unknown", StringComparison.OrdinalIgnoreCase))
            return registryProduct;

        Type locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
            ?? throw new OmenBiosException("WMI scripting locator is not available.");
        object locator = Activator.CreateInstance(locatorType)
            ?? throw new OmenBiosException("Could not create WMI scripting locator.");
        try
        {
            return ReadProduct(locator);
        }
        finally
        {
            Release(locator);
        }
    }

    private static string ReadProduct(object locator)
    {
        object? cimV2 = null;
        try
        {
            cimV2 = Connect(locator, CimV2NamespaceName);
            object? board = First(ExecQuery(cimV2, "SELECT Product FROM Win32_BaseBoard"));
            if(board is not null)
                return Convert.ToString(GetProperty(board, "Product")) ?? "unknown";
        }
        catch
        {
        }
        finally
        {
            if(cimV2 is not null)
                Release(cimV2);
        }

        return "unknown";
    }

    private static string ReadProductFromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string? product = Convert.ToString(key?.GetValue("BaseBoardProduct"));
            if(!string.IsNullOrWhiteSpace(product))
                return product;
        }
        catch
        {
        }

        return "unknown";
    }

    private byte[] SendRead(uint command, uint commandType, byte[]? inputData, byte outputSize)
    {
        int code = Send(command, commandType, inputData, outputSize, out byte[] outputData);
        Check(code, force: false);
        return outputData;
    }

    private void SendWrite(uint command, uint commandType, byte[] inputData, bool forceCheck = false)
    {
        int code = Send(command, commandType, inputData, 0, out _);
        Check(code, forceCheck);
    }

    private int Send(uint command, uint commandType, byte[]? inputData, byte outputSize, out byte[] outputData)
    {
        outputData = new byte[outputSize];

        try
        {
            object dataClass = Invoke(services, "Get", DataClassName);
            object input = Invoke(dataClass, "SpawnInstance_");
            SetProperty(input, "Sign", Sign);
            SetProperty(input, "Command", command);
            SetProperty(input, "CommandType", commandType);
            SetProperty(input, "Size", inputData?.Length ?? 0);
            if(inputData is not null)
                SetProperty(input, DataFieldName, inputData);

            object methods = GetProperty(methodInstance, "Methods_")
                ?? throw new OmenBiosException("BIOS WMI methods collection is unavailable.");
            object method = Invoke(methods, "Item", $"hpqBIOSInt{outputSize}");
            object inParameters = GetProperty(method, "InParameters")
                ?? throw new OmenBiosException("BIOS WMI method input parameters are unavailable.");
            object parameters = Invoke(inParameters, "SpawnInstance_");
            SetProperty(parameters, "InData", input);

            object result = Invoke(methodInstance, "ExecMethod_", $"hpqBIOSInt{outputSize}", parameters);
            object resultData = GetProperty(result, "OutData")
                ?? throw new OmenBiosException("BIOS WMI method did not return OutData.");

            if(outputSize != 0)
                outputData = ToByteArray(GetProperty(resultData, "Data")!, outputSize);

            return Convert.ToInt32(GetProperty(resultData, ReturnCodeFieldName));
        }
        catch(Exception ex)
        {
            throw new OmenBiosException($"BIOS WMI call failed: {ex.Message}");
        }
    }

    private static void Check(int code, bool force)
    {
        if(!force && code is not (-1 or 3 or 5))
            return;

        switch(code)
        {
            case 0:
                return;
            case -1:
                throw new OmenBiosException("BIOS call failed on the client side.");
            case 3:
                throw new OmenBiosException("BIOS command is not available on this device.");
            case 5:
                throw new OmenBiosException("BIOS call used an insufficient input or output buffer.");
            default:
                if(force)
                    throw new OmenBiosException($"BIOS call returned status {code}.");
                return;
        }
    }

    private static byte[] ToByteArray(object value, int minimumLength)
    {
        if(value is byte[] bytes)
            return bytes;

        if(value is object[] objects)
        {
            byte[] converted = new byte[Math.Max(objects.Length, minimumLength)];
            for(int i = 0; i < objects.Length; i++)
                converted[i] = Convert.ToByte(objects[i]);
            return converted;
        }

        if(value is IEnumerable enumerable)
        {
            List<byte> converted = [];
            foreach(object item in enumerable)
                converted.Add(Convert.ToByte(item));
            while(converted.Count < minimumLength)
                converted.Add(0);
            return converted.ToArray();
        }

        throw new OmenBiosException($"BIOS WMI returned unsupported data array type {value.GetType().FullName}.");
    }

    private static object Connect(object locator, string namespaceName)
    {
        object services = Invoke(locator, "ConnectServer", ".", namespaceName);
        try
        {
            object? security = GetProperty(services, "Security_");
            if(security is not null)
                SetProperty(security, "ImpersonationLevel", 3);
        }
        catch
        {
        }

        return services;
    }

    private static object ExecQuery(object services, string query) => Invoke(services, "ExecQuery", query);

    private static object? First(object collection)
    {
        foreach(object item in (IEnumerable)collection)
            return item;
        return null;
    }

    private static object Invoke(object target, string name, params object?[] args)
    {
        try
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.InvokeMethod,
                null,
                target,
                args)!;
        }
        catch(TargetInvocationException ex) when(ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty,
                null,
                target,
                null);
        }
        catch(TargetInvocationException ex) when(ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void SetProperty(object target, string name, object? value)
    {
        try
        {
            target.GetType().InvokeMember(
                name,
                BindingFlags.SetProperty,
                null,
                target,
                [value]);
        }
        catch(TargetInvocationException ex) when(ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void Release(object target)
    {
        if(Marshal.IsComObject(target))
            Marshal.FinalReleaseComObject(target);
    }
}
