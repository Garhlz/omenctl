using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OmenCtl.Core.Hardware;

internal static class NativeMethods
{
    public const int ErrorServiceAlreadyRunning = unchecked((int)0x80070420);
    public const int ErrorServiceExists = unchecked((int)0x80070431);
    private const uint OlsType = 40000;

    public static readonly IoControlCode IoctlGetRefcount = new(OlsType, 0x801, IoControlCode.Access.Any);
    public static readonly IoControlCode IoctlReadIoPortByte = new(OlsType, 0x833, IoControlCode.Access.Read);
    public static readonly IoControlCode IoctlWriteIoPortByte = new(OlsType, 0x836, IoControlCode.Access.Write);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct IoControlCode
    {
        public uint Code { get; }

        public IoControlCode(uint deviceType, uint function, Access access)
        {
            Code = (deviceType << 16) | ((uint)access << 14) | (function << 2);
        }

        public enum Access : uint
        {
            Any = 0,
            Read = 1,
            Write = 2
        }
    }

    [Flags]
    public enum ScManagerAccess : uint
    {
        Connect = 0x00001,
        CreateService = 0x00002
    }

    [Flags]
    public enum ServiceAccess : uint
    {
        AllAccess = 0xF01FF
    }

    public enum ServiceType : uint
    {
        KernelDriver = 0x00000001
    }

    public enum ServiceStart : uint
    {
        DemandStart = 3
    }

    public enum ServiceError : uint
    {
        Normal = 1
    }

    public enum ServiceControl : uint
    {
        Stop = 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        IoControlCode ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, ScManagerAccess desiredAccess);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr OpenService(IntPtr serviceControlManager, string serviceName, ServiceAccess desiredAccess);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateService(
        IntPtr serviceControlManager,
        string serviceName,
        string displayName,
        ServiceAccess desiredAccess,
        ServiceType serviceType,
        ServiceStart startType,
        ServiceError errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        string? tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool StartService(IntPtr service, uint numServiceArgs, string[]? serviceArgVectors);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool ControlService(IntPtr service, ServiceControl control, ref ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", CallingConvention = CallingConvention.Winapi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool CloseServiceHandle(IntPtr serviceControlObject);
}
