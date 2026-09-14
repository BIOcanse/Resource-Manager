using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    private static readonly int MemoryPriorityInformationSize = Marshal.SizeOf<MemoryPriorityInformation>();
    private static readonly int PowerThrottlingInformationSize = Marshal.SizeOf<ProcessPowerThrottlingState>();

    private static NativePolicySnapshot TryReadNativePolicySnapshot(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            return new NativePolicySnapshot(
                TryReadMemoryPriority(handle, processId),
                TryReadPowerThrottling(handle, processId));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(
        IntPtr processHandle,
        int processId)
    {
        var state = MemoryPriorityInformation.Create(NativeMethods.MemoryPriorityNormal);
        if (!NativeMethods.GetProcessInformation(
                processHandle,
                NativeMethods.ProcessMemoryPriorityInformationClass,
                ref state,
                MemoryPriorityInformationSize))
        {
            return null;
        }

        return new ProcessMemoryPrioritySnapshot(
            processId,
            state.MemoryPriority,
            state.MemoryPriority.ToString(CultureInfo.InvariantCulture),
            DescribeMemoryPriority(state.MemoryPriority));
    }

    private static ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(
        IntPtr processHandle,
        int processId)
    {
        var state = ProcessPowerThrottlingState.Create(0, 0);
        if (!NativeMethods.GetProcessInformation(
                processHandle,
                NativeMethods.ProcessPowerThrottlingInformationClass,
                ref state,
                PowerThrottlingInformationSize))
        {
            return null;
        }

        return new ProcessPowerThrottlingSnapshot(
            processId,
            state.Version,
            state.ControlMask,
            state.StateMask,
            FormatPowerThrottlingRaw(state.ControlMask, state.StateMask),
            DescribePowerThrottling(state.ControlMask, state.StateMask));
    }

    private readonly record struct NativePolicySnapshot(
        ProcessMemoryPrioritySnapshot? MemoryPriority,
        ProcessPowerThrottlingSnapshot? PowerThrottling);
}
