using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

// https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntexapi.h
// Consumed prefix of SYSTEM_PERFORMANCE_INFORMATION; SYSTEM_POOLTAG_INFORMATION / SYSTEM_POOLTAG.
[StructLayout(LayoutKind.Explicit, Size = 176)]
internal struct SystemPerformanceLayout
{
    [FieldOffset(112)] internal uint PagedPoolPages;
    [FieldOffset(116)] internal uint NonPagedPoolPages;
    [FieldOffset(140)] internal uint ResidentSystemCodePage;
    [FieldOffset(164)] internal uint ResidentSystemCachePage;
    [FieldOffset(168)] internal uint ResidentPagedPoolPage;
    [FieldOffset(172)] internal uint ResidentSystemDriverPage;
    internal const int RequiredBytes = 176;
}
[StructLayout(LayoutKind.Sequential)]
internal struct PoolTagHeader { internal uint Count, Padding; }
[StructLayout(LayoutKind.Sequential)]
internal struct PoolTagLayout
{
    internal uint Tag, PagedAllocs, PagedFrees, Padding;
    internal ulong PagedUsed;
    internal uint NonPagedAllocs, NonPagedFrees;
    internal ulong NonPagedUsed;
}
// https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntpfapi.h
// SUPERFETCH_INFORMATION, PF_PHYSICAL_MEMORY_RANGE_INFO_V2,
// PF_PFN_PRIO_REQUEST (PageData offset 0xc0). MMPFN_IDENTITY (24 bytes):
// https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntmmapi.h
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SuperfetchInformationLayout
{
    internal const uint CurrentVersion = 45, Signature = 0x6b756843;
    internal uint Version, Magic, InformationClass;
    internal void* Data;
    internal uint Length;
    internal readonly bool IsValid => sizeof(SuperfetchInformationLayout) == 32
        && Version == CurrentVersion && Magic == Signature && Data != null && Length > 0;
}
[StructLayout(LayoutKind.Sequential)]
internal struct PhysicalRangeHeader { internal uint Version, Flags, RangeCount, Reserved; }
[StructLayout(LayoutKind.Sequential)]
internal struct PhysicalRangeLayout { internal ulong BasePfn, PageCount; }
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MemoryListLayout
{
    internal ulong Zero, Free, Modified, ModifiedNoWrite, Bad;
    internal fixed ulong Standby[8];
    internal fixed ulong Repurposed[8];
    internal ulong ModifiedPageFile;
    internal ulong AvailablePages()
    {
        var count = checked(Zero + Free);
        for (var i = 0; i < 8; i++) count = checked(count + Standby[i]);
        return count;
    }
}
[StructLayout(LayoutKind.Sequential)]
internal struct PfnRequestHeader
{
    internal uint Version, Flags;
    internal ulong PfnCount;
    internal MemoryListLayout Memory;
}
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PfnIdentityLayout(ulong flags, ulong pfn = 0, ulong address = 0)
{
    internal readonly ulong Flags = flags, Pfn = pfn, Address = address;
    internal int Use => (int)(Flags & 15);
    internal int List => (int)((Flags >> 4) & 7);
}
