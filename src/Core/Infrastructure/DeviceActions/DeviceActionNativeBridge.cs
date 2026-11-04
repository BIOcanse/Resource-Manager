using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.DeviceActions;

internal static unsafe partial class DeviceActionNativeBridge
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Text
    {
        internal fixed char Data[512];
        internal static Text Of(string? value)
        {
            Text result = default;
            if (value is null) return result;
            if (value.Length >= 512 || value.Contains('\0')) throw new ArgumentException("Invalid device action text.");
            value.AsSpan().CopyTo(new Span<char>(result.Data, 512));
            return result;
        }
        public override string ToString() { fixed (char* p = Data) return new string(p, 0, new ReadOnlySpan<char>(p, 512).IndexOf('\0')); }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Target { internal Text ReportType, Module, InstanceId; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Record
    {
        internal long Sequence, OccurredAt;
        internal Text ReportId, InstanceId;
        internal uint State, ResultCode, Detail;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Output { internal uint Code, DeviceResult, Detail, Reserved; internal long Sequence; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, Text*, Target*, uint> Lookup;
        internal delegate* unmanaged[Cdecl]<nint, Text*, Text*, uint*, uint> Resolve;
        internal delegate* unmanaged[Cdecl]<nint, Text*, uint*, uint> Restart;
        internal delegate* unmanaged[Cdecl]<nint, Record*, uint> Store;
        internal delegate* unmanaged[Cdecl]<nint, Record*, uint> Notify;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PlatformResult { internal uint Code, Detail, RestartRequired; }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_action_execute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Execute(in Callbacks callbacks, in Text reportId, uint confirmed, int capacity, uint count, long now, out Output output);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_action_validate_capacity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ValidateCapacity(int capacity);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_restart", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint RestartDevice(string instanceId, uint confirmed, out PlatformResult result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_resolve_driver", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ResolveDriver(string module, char* instanceId, uint capacity, out PlatformResult result);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_action_report_supported")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ReportSupported(in Text reportType);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_device_action_recover_record")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint RecoverRecord(ref Record record);
}

public interface IDeviceRestartPlatformBridge
{
    uint Resolve(string module, out string instanceId, out uint detail);
    uint Restart(string instanceId, out uint detail);
}
internal sealed class WindowsDeviceRestartPlatformBridge : IDeviceRestartPlatformBridge
{
    public unsafe uint Resolve(string module, out string instanceId, out uint detail)
    {
        var buffer = stackalloc char[512];
        new Span<char>(buffer, 512).Clear();
        DeviceActionNativeBridge.ResolveDriver(module, buffer, 512, out var result);
        instanceId = new string(buffer);
        detail = result.Detail;
        return result.Code;
    }
    public uint Restart(string instanceId, out uint detail)
    {
        DeviceActionNativeBridge.RestartDevice(instanceId, 1, out var result);
        detail = result.Detail;
        return result.Code;
    }
}
