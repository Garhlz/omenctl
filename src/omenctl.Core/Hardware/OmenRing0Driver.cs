using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OmenCtl.Core.Hardware;

internal sealed class OmenDriverException(string message) : Exception(message);

internal sealed class OmenRing0Driver : IDisposable
{
    private const string DriverId = "WinRing0_1_2_0";
    private SafeFileHandle? handle;
    private string? serviceName;
    private string? driverFilePath;

    public bool IsOpen => handle is not null;

    public void Open()
    {
        if(handle is not null)
            return;

        serviceName = GetServiceName();
        TryOpenDevice();
        if(handle is not null)
            return;

        driverFilePath = GetDriverFilePath();
        ExtractDriver(driverFilePath);

        if(!InstallService(driverFilePath, out string error))
        {
            DeleteService();
            Thread.Sleep(2000);
            if(!InstallService(driverFilePath, out error))
                throw new OmenDriverException(error);
        }

        TryOpenDevice();
        if(handle is null)
        {
            DeleteService();
            throw new OmenDriverException("Driver service installed, but device could not be opened.");
        }
    }

    public void Dispose() => Close();

    public void Close()
    {
        if(handle is not null)
        {
            uint refCount = 0;
            DeviceIoControl(NativeMethods.IoctlGetRefcount, null, ref refCount);
            handle.Close();
            handle.Dispose();
            handle = null;

            if(refCount <= 1)
                DeleteService();
        }

        try
        {
            if(driverFilePath is not null && File.Exists(driverFilePath))
                File.Delete(driverFilePath);
        }
        catch
        {
        }
    }

    public byte ReadIoPort(uint port)
    {
        uint value = 0;
        DeviceIoControl(NativeMethods.IoctlReadIoPortByte, port, ref value);
        return (byte)(value & 0xFF);
    }

    public void WriteIoPort(uint port, byte value) =>
        DeviceIoControl(NativeMethods.IoctlWriteIoPortByte, new WriteIoPortInput { PortNumber = port, Value = value });

    private void TryOpenDevice()
    {
        IntPtr fileHandle = NativeMethods.CreateFile(
            @"\\.\" + DriverId,
            0xC0000000,
            FileShare.None,
            IntPtr.Zero,
            FileMode.Open,
            FileAttributes.Normal,
            IntPtr.Zero);

        SafeFileHandle candidate = new(fileHandle, true);
        if(candidate.IsInvalid)
        {
            candidate.Dispose();
            return;
        }

        handle = candidate;
    }

    private bool InstallService(string path, out string errorMessage)
    {
        IntPtr manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerAccess.CreateService);
        if(manager == IntPtr.Zero)
        {
            errorMessage = $"OpenSCManager failed: 0x{Marshal.GetLastWin32Error():X8}. Run the agent as administrator.";
            return false;
        }

        IntPtr service = NativeMethods.CreateService(
            manager,
            serviceName!,
            serviceName!,
            NativeMethods.ServiceAccess.AllAccess,
            NativeMethods.ServiceType.KernelDriver,
            NativeMethods.ServiceStart.DemandStart,
            NativeMethods.ServiceError.Normal,
            path,
            null,
            null,
            null,
            null,
            null);

        if(service == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            NativeMethods.CloseServiceHandle(manager);
            errorMessage = errorCode == NativeMethods.ErrorServiceExists
                ? "Driver service already exists."
                : $"CreateService failed: 0x{errorCode:X8}.";
            return false;
        }

        if(!NativeMethods.StartService(service, 0, null))
        {
            int errorCode = Marshal.GetLastWin32Error();
            if(errorCode != NativeMethods.ErrorServiceAlreadyRunning)
            {
                NativeMethods.CloseServiceHandle(service);
                NativeMethods.CloseServiceHandle(manager);
                errorMessage = $"StartService failed: 0x{errorCode:X8}.";
                return false;
            }
        }

        NativeMethods.CloseServiceHandle(service);
        NativeMethods.CloseServiceHandle(manager);
        errorMessage = "";
        return true;
    }

    private void DeleteService()
    {
        if(serviceName is null)
            return;

        IntPtr manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerAccess.Connect);
        if(manager == IntPtr.Zero)
            return;

        IntPtr service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceAccess.AllAccess);
        if(service == IntPtr.Zero)
        {
            NativeMethods.CloseServiceHandle(manager);
            return;
        }

        NativeMethods.ServiceStatus status = new();
        NativeMethods.ControlService(service, NativeMethods.ServiceControl.Stop, ref status);
        NativeMethods.DeleteService(service);
        NativeMethods.CloseServiceHandle(service);
        NativeMethods.CloseServiceHandle(manager);
    }

    private bool DeviceIoControl(NativeMethods.IoControlCode ioControlCode, object? inBuffer)
    {
        if(handle is null)
            return false;

        IntPtr inputPointer = IntPtr.Zero;
        try
        {
            uint inputSize = 0;
            if(inBuffer is not null)
            {
                inputSize = (uint)Marshal.SizeOf(inBuffer);
                inputPointer = Marshal.AllocHGlobal((int)inputSize);
                Marshal.StructureToPtr(inBuffer, inputPointer, false);
            }

            return NativeMethods.DeviceIoControl(
                handle,
                ioControlCode,
                inputPointer,
                inputSize,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        finally
        {
            if(inputPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(inputPointer);
        }
    }

    private bool DeviceIoControl<T>(NativeMethods.IoControlCode ioControlCode, object? inBuffer, ref T outBuffer)
    {
        if(handle is null)
            return false;

        IntPtr inputPointer = IntPtr.Zero;
        IntPtr outputPointer = IntPtr.Zero;
        try
        {
            uint inputSize = 0;
            if(inBuffer is not null)
            {
                inputSize = (uint)Marshal.SizeOf(inBuffer);
                inputPointer = Marshal.AllocHGlobal((int)inputSize);
                Marshal.StructureToPtr(inBuffer, inputPointer, false);
            }

            int outputSize = Marshal.SizeOf<T>();
            outputPointer = Marshal.AllocHGlobal(outputSize);
            Marshal.StructureToPtr(outBuffer!, outputPointer, false);

            bool ok = NativeMethods.DeviceIoControl(
                handle,
                ioControlCode,
                inputPointer,
                inputSize,
                outputPointer,
                (uint)outputSize,
                out _,
                IntPtr.Zero);

            outBuffer = Marshal.PtrToStructure<T>(outputPointer)!;
            return ok;
        }
        finally
        {
            if(inputPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(inputPointer);
            if(outputPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(outputPointer);
        }
    }

    private static void ExtractDriver(string filePath)
    {
        Assembly assembly = typeof(OmenRing0Driver).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("omenctl.Driver.sys.gz")
            ?? throw new OmenDriverException("Embedded driver resource omenctl.Driver.sys.gz was not found.");
        using FileStream target = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Position = 1;
        using GZipStream gzipStream = new(stream, CompressionMode.Decompress);
        gzipStream.CopyTo(target);
    }

    private static string GetDriverFilePath()
    {
        string? filePath = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".sys");
        if(!string.IsNullOrWhiteSpace(filePath) && CanCreate(filePath))
            return filePath;

        filePath = Path.ChangeExtension(Environment.ProcessPath, ".sys");
        if(!string.IsNullOrWhiteSpace(filePath) && CanCreate(filePath))
            return filePath;

        filePath = Path.ChangeExtension(Path.GetTempFileName(), ".sys");
        if(!string.IsNullOrWhiteSpace(filePath) && CanCreate(filePath))
            return filePath;

        throw new OmenDriverException("Could not find a writable path for the kernel driver.");

        static bool CanCreate(string path)
        {
            try
            {
                using(File.Create(path, 1, FileOptions.DeleteOnClose))
                    return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetServiceName()
    {
        string? name = Assembly.GetExecutingAssembly().GetName().Name;
        if(string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "omenctl";
        return $"R0{name}".Replace(" ", string.Empty).Replace(".", "_");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WriteIoPortInput
    {
        public uint PortNumber;
        public byte Value;
    }
}
