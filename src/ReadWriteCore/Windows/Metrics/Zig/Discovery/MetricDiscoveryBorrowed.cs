using System.Text;
using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

/// <summary>Borrowed ABI views. The caller keeps its synchronous local Ledger use alive.</summary>
public readonly unsafe ref struct MetricDiscoveryBorrowed
{
    private static readonly UTF8Encoding IdentityEncoding = new(false, true);
    private readonly MetricDiscoveryCompiled compiled;
    public MetricDiscoveryBorrowed(in MetricDiscoveryCompiled compiled) => this.compiled = compiled;
    public ReadOnlySpan<ulong> MetricIds => new(compiled.Catalog.Metrics.Ids, checked((int)compiled.Catalog.Metrics.Count));
    public ReadOnlySpan<ulong> RuleIds => new(compiled.Catalog.Rules.Ids, checked((int)compiled.Catalog.Rules.Count));
    public ReadOnlySpan<uint> NativeMetricIndices => new(compiled.Bindings.MetricIndices, checked((int)compiled.Bindings.Count));
    public ReadOnlySpan<byte> Address(uint rule) => Utf8(compiled.Text, At(compiled.Bindings.Addresses, rule));
    public ReadOnlySpan<byte> Legacy(uint rule) => Utf8(compiled.Text, At(compiled.Bindings.Legacy, rule));
    public ReadOnlySpan<byte> FieldName(uint rule) => Utf8(compiled.Text, At(compiled.Bindings.FieldNames, rule));
    private MetricDiscoveryRef At(MetricDiscoveryRef* column, uint row)
    {
        if (row >= compiled.Bindings.Count || column == null) throw new ArgumentOutOfRangeException(nameof(row));
        return column[row];
    }
    public static ReadOnlySpan<byte> Utf8(MetricDiscoveryText text, MetricDiscoveryRef value)
    {
        if (text.Reserved != 0 || (ulong)value.Offset + value.Length > text.Length || (text.Bytes == null && value.Length != 0))
            throw new ArgumentOutOfRangeException(nameof(value));
        return new ReadOnlySpan<byte>(text.Bytes + value.Offset, checked((int)value.Length));
    }
    // Encoding is boundary marshalling only; no field matching or expansion here.
    public static MetricDiscoveryRef WriteUtf8(ReadOnlySpan<char> value, Span<byte> arena, ref uint used)
    {
        int required;
        try { required = IdentityEncoding.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw new ResourceManager.ReadWrite.Windows.Metrics.Zig.MetricOwnerException(new() {
                Code = MetricOwnerCode.InvalidContract, Family = MetricOwnerFamily.Binding });
        }
        if ((ulong)used + (uint)required > (ulong)arena.Length)
            throw new ResourceManager.ReadWrite.Windows.Metrics.Zig.MetricOwnerException(new() {
                Code = MetricOwnerCode.PayloadCapacity, Family = MetricOwnerFamily.Work,
                Required = (ulong)used + (uint)required, Limit = (ulong)arena.Length });
        var written = IdentityEncoding.GetBytes(value, arena[(int)used..]);
        var result = new MetricDiscoveryRef { Offset = used, Length = checked((uint)written) };
        used = checked(used + (uint)written);
        return result;
    }
}
