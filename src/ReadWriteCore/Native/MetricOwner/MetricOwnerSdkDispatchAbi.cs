using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.MetricOwner;

/// <summary>Transient request-shape metadata in the parent's one operation work sidecar.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricSdkColumns
{
    public uint Count, Reserved;
    public uint* FieldCodes; public uint* ScopeIndices; public uint* DisplayIndices;
    public MetricDiscoveryRef* DeviceKeys; public MetricDiscoveryText Text;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MetricSdkDispatch
{
    public uint RowCount, SdkRowCount, GroupCount, SourceCount;
    public ulong Token, CatalogGeneration;
    public ulong* SourceIds; public uint* SourceGroupOffsets;
    public uint* GroupScopeIndices; public uint* GroupFirstRows;
    public uint* GroupRowOffsets; public uint* RowIndices; public uint* RawRowGroups;
    public uint* GroupFieldOffsets; public uint* FieldCodes;
    public uint* KeyOrder; public uint* DisplayOrder; public int* ResultOrdinals;
}
public static unsafe partial class MetricOwnerSdkDispatchAbi
{
    private const string Library = "ResourceManager.NativeCore";
    [LibraryImport(Library, EntryPoint = "rm_metric_sdk_dispatch_required_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint RequiredSize(uint actualRawRows, uint actualSources, out ulong bytes, out MetricOwnerFailure failure);
    [LibraryImport(Library, EntryPoint = "rm_metric_sdk_dispatch_build")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint Build(nint owner, in MetricOwnerRound raw, in MetricSdkColumns columns,
        ulong* sourceIds, uint sourceCount, uint* excludedRawRows, uint excludedCount,
        byte* work, ulong workLength, out MetricSdkDispatch dispatch, out MetricOwnerFailure failure);
}
