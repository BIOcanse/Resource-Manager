using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public sealed unsafe partial class MetricDiscoveryRoundFacts
{
    private MetricSdkDispatch sdkGrouping;
    private bool hasSdkGrouping;
    public bool HasSdkGrouping { get { Sampling(); return hasSdkGrouping; } }
    public MetricSdkDispatch SdkGrouping { get { Sampling(); return sdkGrouping; } }
    internal void AttachSdkGrouping(in MetricSdkDispatch grouping)
    {
        Sampling();
        if (hasSdkGrouping || grouping.RowCount != Native.Raw.Count || grouping.SourceCount != Native.Sources.Count
            || grouping.Token != Native.Raw.Token || grouping.CatalogGeneration != Native.Raw.CatalogGeneration
            || grouping.SdkRowCount > grouping.RowCount || grouping.GroupCount > grouping.SdkRowCount)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.InvalidContract, Family = MetricOwnerFamily.Binding });
        sdkGrouping = grouping; hasSdkGrouping = true;
    }
    internal void ReleaseSdkGrouping() { sdkGrouping = default; hasSdkGrouping = false; }
    partial void OnClose() => ReleaseSdkGrouping();
}
