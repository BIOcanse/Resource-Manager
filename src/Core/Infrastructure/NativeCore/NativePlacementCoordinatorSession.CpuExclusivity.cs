using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePlacementCoordinatorSession
{
    public NativePlacementCoordinatorStatus ResetCpuExclusivity()
    {
        lock (sync) return (NativePlacementCoordinatorStatus)NativeMethods.ResetCpuExclusivity(RequireHandle());
    }
    public NativePlacementCoordinatorStatus ConfigureCpuExclusivity(in NativeCpuExclusivityConfiguration configuration)
    {
        lock (sync) return (NativePlacementCoordinatorStatus)NativeMethods.ConfigureCpuExclusivity(RequireHandle(), in configuration);
    }

    public unsafe NativePlacementCoordinatorStatus PlanCpuExclusivity(ref NativeCpuExclusivityFrame frame,
        ReadOnlySpan<NativeCpuExclusivityCore> cores, ReadOnlySpan<NativeCpuExclusivitySoftware> software,
        ReadOnlySpan<NativeCpuExclusivityUsage> usage, ReadOnlySpan<NativeCpuExclusivityReservation> manual,
        Span<NativeCpuExclusivityReservation> output)
    {
        if (frame.CoreCount != cores.Length || frame.SoftwareCount != software.Length || frame.UsageCount != usage.Length
            || frame.ManualCount != manual.Length || frame.OutputCapacity > output.Length)
            return NativePlacementCoordinatorStatus.InvalidArgument;
        lock (sync)
        {
            fixed (NativeCpuExclusivityCore* c = cores)
            fixed (NativeCpuExclusivitySoftware* s = software)
            fixed (NativeCpuExclusivityUsage* u = usage)
            fixed (NativeCpuExclusivityReservation* m = manual)
            fixed (NativeCpuExclusivityReservation* o = output)
                return (NativePlacementCoordinatorStatus)NativeMethods.PlanCpuExclusivity(RequireHandle(), ref frame, c, s, u, m, o);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_cpu_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ResetCpuExclusivity(IntPtr session);
        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_cpu_configure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ConfigureCpuExclusivity(IntPtr session, in NativeCpuExclusivityConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_cpu_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int PlanCpuExclusivity(IntPtr session, ref NativeCpuExclusivityFrame frame,
            NativeCpuExclusivityCore* cores, NativeCpuExclusivitySoftware* software, NativeCpuExclusivityUsage* usage,
            NativeCpuExclusivityReservation* manual, NativeCpuExclusivityReservation* output);
    }
}
