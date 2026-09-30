using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivityConfiguration
{
    public uint AbiVersion, StructSize;
    public ulong Generation;
    public uint CoreCapacity, CcdCapacity, SoftwareCapacity, ReservationCapacity;
    public double CoreEnter, CoreExit, CcdEnter, CcdExit;
    public uint QualificationRounds, Enabled;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivityFrame
{
    public uint AbiVersion, StructSize;
    public ulong Generation, TopologyKey, ObservationSource, ObservedThrough;
    public uint CoreCount, CcdCount, SoftwareCount, UsageCount, ManualCount, OutputCapacity, OutputCount, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivityCore { public uint CcdIndex, Reserved; }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivitySoftware
{
    public ulong Key, InstanceDigest;
    public double Priority;
    public uint Automatic, Observed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivityUsage { public uint SoftwareIndex, CoreIndex; public double Percent; }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCpuExclusivityReservation { public uint SoftwareIndex, CoreIndex; }
