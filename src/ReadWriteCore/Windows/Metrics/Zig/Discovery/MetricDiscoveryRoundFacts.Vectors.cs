using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public sealed unsafe partial class MetricDiscoveryRoundFacts
{
    private MetricDiscoveryVectorInputs vectorInputs;
    private MetricDiscoveryPdhInputView? pdh;
    private MetricDiscoveryNotebookInputView? notebook;
    internal void AttachVectorInputs(MetricDiscoveryVectorInputs inputs)
    {
        Sampling(); vectorInputs = inputs;
        if (inputs.Gpu != null) pdh = new(this, inputs.Gpu, inputs.GpuCapacity);
        if (inputs.Sensor != null) notebook = new(this, inputs.Sensor, inputs.SensorCapacity);
    }
    public MetricDiscoveryPdhInputView? PdhGpu { get { Sampling(); return pdh; } }
    public MetricDiscoveryNotebookInputView? Notebook { get { Sampling(); return notebook; } }
    public void DeclareNpuSampleGeneration(int rawRow, ulong actualSourceObservation)
    {
        Sampling();
        if ((uint)rawRow >= vectorInputs.RawCount || vectorInputs.NpuRowScopes == null || vectorInputs.NpuGenerations == null)
            throw new ArgumentOutOfRangeException(nameof(rawRow));
        var scope = vectorInputs.NpuRowScopes[rawRow];
        if (scope >= vectorInputs.ScopeCount) throw new MetricOwnerException(new() { Code = MetricOwnerCode.InvalidContract, Family = MetricOwnerFamily.Binding });
        vectorInputs.NpuGenerations[scope] = actualSourceObservation;
    }
    private void CloseVectorInputs() { pdh?.Close(); notebook?.Close(); vectorInputs = default; }
}

public delegate void MetricDiscoveryVectorPublished(in MetricDiscoveryVectorPublication publication);

/// <summary>Accepted native address/suppression metadata, borrowed through this synchronous callback only.</summary>
public readonly unsafe ref struct MetricDiscoveryVectorPublication
{
    private readonly MetricOwnerRawRound round;
    private readonly nint pointer;
    internal MetricDiscoveryVectorPublication(MetricOwnerRawRound round, nint pointer) { this.round = round; this.pointer = pointer; }
    private ref readonly MetricVectorGpuPublication Native
    {
        get
        {
            _ = round.Token;
            if (pointer == 0 || ((MetricVectorGpuPublication*)pointer)->Published != 1) throw new InvalidOperationException("No accepted vector publication.");
            return ref *(MetricVectorGpuPublication*)pointer;
        }
    }
    public uint Rows => Native.Rows;
    public ReadOnlySpan<uint> SelectedRows => new(Native.SelectedRows, checked((int)Native.SelectedCount));
    public ReadOnlySpan<uint> SuppressedMetrics => new(Native.SuppressedMetrics, checked((int)Native.SuppressedCount));
    public ReadOnlySpan<byte> Address(int selectedOrdinal)
    {
        ref readonly var data = ref Native;
        if ((uint)selectedOrdinal >= data.SelectedCount) throw new ArgumentOutOfRangeException(nameof(selectedOrdinal));
        var address = data.Addresses[data.SelectedRows[selectedOrdinal]];
        return MetricDiscoveryBorrowed.Utf8(new() { Bytes = data.Text.Bytes, Length = data.Text.Length }, new() { Offset = address.Offset, Length = address.Length });
    }
}
