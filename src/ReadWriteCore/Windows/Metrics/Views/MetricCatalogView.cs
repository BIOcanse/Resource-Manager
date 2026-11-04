using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

/// <summary>Borrowed selected order and cold descriptors. Native decides membership, aliases and sources.</summary>
public readonly ref struct MetricCatalogView
{
    public MetricCatalogView(ReadOnlySpan<ulong> identities, ReadOnlySpan<HardwareMetricItemDescriptor> items,
        ReadOnlySpan<MetricDeviceFieldBinding?> bindings, HardwareMetricCpuDescriptor cpu,
        ReadOnlySpan<HardwareMetricGpuDescriptor> gpus)
    {
        if (items.Length != identities.Length || bindings.Length != identities.Length)
            throw new ArgumentException("Selected catalog column lengths differ.");
        Identities = identities; Items = items; Bindings = bindings; Cpu = cpu; Gpus = gpus;
    }
    public ReadOnlySpan<ulong> Identities { get; }
    public ReadOnlySpan<HardwareMetricItemDescriptor> Items { get; }
    public ReadOnlySpan<MetricDeviceFieldBinding?> Bindings { get; }
    public HardwareMetricCpuDescriptor Cpu { get; }
    public ReadOnlySpan<HardwareMetricGpuDescriptor> Gpus { get; }
    public int Count => Identities.Length;
}
