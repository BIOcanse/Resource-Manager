using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvapiReader
{
    private const int NvapiMaxPhysicalGpus = 64;
    private const int NvapiShortStringMax = 64;
    private const uint NvapiInitializeId = 0x0150E828;
    private const uint NvapiEnumPhysicalGpusId = 0xE5AC921F;
    private const uint NvapiGpuGetFullNameId = 0xCEEE8E9F;
    private const uint NvapiGpuGetPciIdentifiersId = 0x2DDFB66E;
    private const uint NvapiGpuGetBusIdId = 0x1BE0B8E5;
    private const uint NvapiGpuGetBusSlotIdId = 0x2A0A350F;
    private const uint NvapiGpuGetTachReadingId = 0x5F608315;
    private const uint NvapiGpuGetCurrentPstateId = 0x927DA4F6;
    private const uint NvapiGpuGetPstates20Id = 0x6FF81213;
    private const uint NvapiGpuClientFanCoolersGetStatusId = 0x35AED5E8;
    private const uint NvapiGpuClientVoltRailsGetStatusId = 0x465F9BCF;

    private static bool TryLoadBindings(out NativeBindings result)
    {
        foreach (var candidate in CandidateNvapiLibraries())
        {
            if (!TryLoadLibrary(candidate, out var libraryHandle))
            {
                continue;
            }

            if (!NativeLibrary.TryGetExport(libraryHandle, "nvapi_QueryInterface", out var queryInterfaceAddress))
            {
                NativeLibrary.Free(libraryHandle);
                continue;
            }

            var queryInterface = Marshal.GetDelegateForFunctionPointer<NvapiQueryInterfaceDelegate>(queryInterfaceAddress);
            result = new NativeBindings(
                libraryHandle,
                GetDelegate<NvapiInitializeDelegate>(queryInterface, NvapiInitializeId),
                GetDelegate<NvapiEnumPhysicalGpusDelegate>(queryInterface, NvapiEnumPhysicalGpusId),
                GetDelegate<NvapiGpuGetFullNameDelegate>(queryInterface, NvapiGpuGetFullNameId),
                GetDelegate<NvapiGpuGetPciIdentifiersDelegate>(queryInterface, NvapiGpuGetPciIdentifiersId),
                GetDelegate<NvapiGpuGetBusIdDelegate>(queryInterface, NvapiGpuGetBusIdId),
                GetDelegate<NvapiGpuGetBusSlotIdDelegate>(queryInterface, NvapiGpuGetBusSlotIdId),
                GetDelegate<NvapiGpuGetTachReadingDelegate>(queryInterface, NvapiGpuGetTachReadingId),
                GetDelegate<NvapiGpuGetCurrentPstateDelegate>(queryInterface, NvapiGpuGetCurrentPstateId),
                GetDelegate<NvapiGpuGetPstates20Delegate>(queryInterface, NvapiGpuGetPstates20Id),
                GetDelegate<NvapiGpuClientFanCoolersGetStatusDelegate>(queryInterface, NvapiGpuClientFanCoolersGetStatusId),
                GetDelegate<NvapiGpuClientVoltRailsGetStatusDelegate>(queryInterface, NvapiGpuClientVoltRailsGetStatusId));
            return true;
        }

        result = NativeBindings.Unavailable;
        return false;
    }

    private static TDelegate? GetDelegate<TDelegate>(
        NvapiQueryInterfaceDelegate queryInterface,
        uint functionId)
        where TDelegate : Delegate
    {
        var address = queryInterface(functionId);
        return address == IntPtr.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    private static IEnumerable<string> CandidateNvapiLibraries()
    {
        var fileName = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        yield return fileName;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            yield return Path.Combine(systemDirectory, fileName);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "SysWOW64", "nvapi.dll");
        }
    }

    private static bool TryLoadLibrary(string candidate, out IntPtr handle)
    {
        try
        {
            return NativeLibrary.TryLoad(candidate, out handle);
        }
        catch (BadImageFormatException)
        {
            handle = IntPtr.Zero;
            return false;
        }
        catch (DllNotFoundException)
        {
            handle = IntPtr.Zero;
            return false;
        }
    }

    private string ReadNvapiShortString(IntPtr gpuHandle)
    {
        if (bindings.GetFullName is null)
        {
            return "NVIDIA GPU";
        }

        var bytes = new byte[NvapiShortStringMax];
        return bindings.GetFullName(gpuHandle, bytes) == NvapiOk
            ? Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim()
            : "NVIDIA GPU";
    }

    private readonly record struct NativeBindings(
        IntPtr LibraryHandle,
        NvapiInitializeDelegate? Initialize,
        NvapiEnumPhysicalGpusDelegate? EnumPhysicalGpus,
        NvapiGpuGetFullNameDelegate? GetFullName,
        NvapiGpuGetPciIdentifiersDelegate? GetPciIdentifiers,
        NvapiGpuGetBusIdDelegate? GetBusId,
        NvapiGpuGetBusSlotIdDelegate? GetBusSlotId,
        NvapiGpuGetTachReadingDelegate? GetTachReading,
        NvapiGpuGetCurrentPstateDelegate? GetCurrentPstate,
        NvapiGpuGetPstates20Delegate? GetPstates20,
        NvapiGpuClientFanCoolersGetStatusDelegate? ClientFanCoolersGetStatus,
        NvapiGpuClientVoltRailsGetStatusDelegate? ClientVoltRailsGetStatus)
    {
        public static NativeBindings Unavailable { get; } = new(
            IntPtr.Zero,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvapiQueryInterfaceDelegate(uint interfaceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiEnumPhysicalGpusDelegate(
        [Out] IntPtr[] gpuHandles,
        out uint gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetFullNameDelegate(
        IntPtr gpuHandle,
        [Out] byte[] name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetPciIdentifiersDelegate(
        IntPtr gpuHandle,
        out uint deviceId,
        out uint subSystemId,
        out uint revisionId,
        out uint externalDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetBusIdDelegate(
        IntPtr gpuHandle,
        out uint busId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetBusSlotIdDelegate(
        IntPtr gpuHandle,
        out uint busSlotId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetTachReadingDelegate(
        IntPtr gpuHandle,
        out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetCurrentPstateDelegate(
        IntPtr gpuHandle,
        out uint currentPstate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuGetPstates20Delegate(
        IntPtr gpuHandle,
        IntPtr pstatesInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuClientFanCoolersGetStatusDelegate(
        IntPtr gpuHandle,
        IntPtr fanCoolersStatus);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiGpuClientVoltRailsGetStatusDelegate(
        IntPtr gpuHandle,
        IntPtr voltRailsStatus);
}
