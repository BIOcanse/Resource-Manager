using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>The Windows SDK DISK_SPACE_INFORMATION layout; counts are allocation units.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsVolumeSpaceNativeData
{
    public ulong ActualTotalAllocationUnits;
    public ulong ActualAvailableAllocationUnits;
    public ulong ActualPoolUnavailableAllocationUnits;
    public ulong CallerTotalAllocationUnits;
    public ulong CallerAvailableAllocationUnits;
    public ulong CallerPoolUnavailableAllocationUnits;
    public ulong UsedAllocationUnits;
    public ulong TotalReservedAllocationUnits;
    public ulong VolumeStorageReserveAllocationUnits;
    public ulong AvailableCommittedAllocationUnits;
    public ulong PoolAvailableAllocationUnits;
    public uint SectorsPerAllocationUnit;
    public uint BytesPerSector;
}
