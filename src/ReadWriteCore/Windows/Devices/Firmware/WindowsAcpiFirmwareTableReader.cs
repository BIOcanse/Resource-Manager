using ResourceManager.ReadWrite.Information;
using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>Explicit read-only firmware capture. Null enumeration and per-table failures retain their original meanings.</summary>
public sealed class WindowsAcpiFirmwareTableReader : IAcpiFirmwareTableReader
{
    internal const uint ProviderSignature = 0x41435049;
    internal const int MaximumTableIds = 1024;
    internal const int MaximumTableBytes = 4 * 1024 * 1024;
    internal const int MaximumCaptureBytes = 16 * 1024 * 1024;
    private readonly IWindowsAcpiFirmwareApi api = new WindowsAcpiFirmwareApi();
    private readonly Action<string, object?[]>? logDebug;

    public WindowsAcpiFirmwareTableReader(Action<string, object?[]>? logDebug = null)
        => this.logDebug = logDebug;

    internal WindowsAcpiFirmwareTableReader(
        IWindowsAcpiFirmwareApi api,
        Action<string, object?[]>? logDebug = null) : this(logDebug) => this.api = api;

    private void LogDebug(string format, params object?[] arguments) => logDebug?.Invoke(format, arguments);

    public DeviceInformationSnapshot ReadFields()
    {
        var tables = ReadTables();
        return tables is null ? DeviceInformationSnapshot.Empty
            : WindowsInformationFieldProjection.CreateAcpi(tables, DateTimeOffset.UtcNow);
    }

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public IReadOnlyList<AcpiFirmwareTable>? ReadTables()
    {
        var enumerationBudget = MaximumTableIds * sizeof(uint) * 2;
        var ids = ReadBuffer(buffer => api.Enumerate(ProviderSignature, buffer),
            MaximumTableIds * sizeof(uint), ref enumerationBudget);
        if (ids is null || ids.Value.Length % sizeof(uint) != 0)
        {
            LogDebug("ACPI table enumeration is unavailable or malformed.");
            return null;
        }

        var signatures = new Dictionary<uint, (string Signature, uint Count)>();
        for (var offset = 0; offset < ids.Value.Length; offset += sizeof(uint))
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(ids.Value.Span.Slice(offset, sizeof(uint)));
            var signature = ReadAscii(ids.Value.Span.Slice(offset, sizeof(uint)), trimPadding: false);
            if (signature is null) continue;
            signatures[id] = signatures.TryGetValue(id, out var previous)
                ? (signature, previous.Count + 1) : (signature, 1);
        }

        var budget = MaximumCaptureBytes;
        var tables = new List<AcpiFirmwareTable>(signatures.Count);
        foreach (var (id, entry) in signatures.OrderBy(static pair => pair.Key))
        {
            AcpiDescriptionTable? table = null;
            // FACS is not a common-header table; never dereference physical FADT pointers.
            if (entry.Signature != "FACS")
            {
                var raw = ReadBuffer(buffer => api.GetTable(ProviderSignature, id, buffer),
                    MaximumTableBytes, ref budget);
                if (raw is not null) table = ParseTable(id, raw.Value.Span);
            }
            if (table is null)
                LogDebug("ACPI first table for signature {Signature} has no valid current content.", entry.Signature);
            tables.Add(new(id, entry.Signature, entry.Count, table));
        }
        return tables.AsReadOnly();
    }

    private ReadOnlyMemory<byte>? ReadBuffer(
        Func<byte[]?, WindowsFirmwareCallResult> call, int maximumBytes, ref int budget)
    {
        if (budget <= 0) return null;
        var result = call(null);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (result.BytesReturned == 0 || result.BytesReturned > maximumBytes || result.BytesReturned > budget)
            {
                LogDebug("ACPI read unavailable: size {RequiredSize}, Win32 error {Error}, remaining bytes {Budget}.",
                    result.BytesReturned, result.Error, budget);
                return null;
            }
            var buffer = new byte[checked((int)result.BytesReturned)];
            budget -= buffer.Length;
            result = call(buffer);
            if (result.BytesReturned > buffer.Length) continue;
            if (result.BytesReturned == 0)
            {
                LogDebug("ACPI read failed with Win32 error {Error}.", result.Error);
                return null;
            }
            return buffer.AsMemory(0, checked((int)result.BytesReturned));
        }
        LogDebug("ACPI read size did not stabilize within the bounded attempt.");
        return null;
    }

    public static AcpiDescriptionTable? ParseTable(uint requestedId, ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 36 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != requestedId
            || requestedId == 0x53434146 || ReadAscii(raw[..4], trimPadding: false) is null) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4, 4));
        if (length < 36 || length > raw.Length) return null;
        var table = raw[..checked((int)length)];
        var checksum = 0;
        foreach (var value in table) checksum = (checksum + value) & 0xff;
        if (checksum != 0) return null;
        return new(table.ToArray(), table[8], ReadAscii(table.Slice(10, 6)),
            ReadAscii(table.Slice(16, 8)), BinaryPrimitives.ReadUInt32LittleEndian(table.Slice(24, 4)),
            ReadAscii(table.Slice(28, 4)), BinaryPrimitives.ReadUInt32LittleEndian(table.Slice(32, 4)));
    }

    private static string? ReadAscii(ReadOnlySpan<byte> value, bool trimPadding = true)
    {
        if (trimPadding)
            while (!value.IsEmpty && value[^1] is 0 or 0x20) value = value[..^1];
        if (value.IsEmpty) return null;
        foreach (var character in value)
            if (character is < 0x20 or > 0x7e) return null;
        var text = Encoding.ASCII.GetString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

internal readonly record struct WindowsFirmwareCallResult(uint BytesReturned, int Error);

internal interface IWindowsAcpiFirmwareApi
{
    WindowsFirmwareCallResult Enumerate(uint provider, byte[]? buffer);
    WindowsFirmwareCallResult GetTable(uint provider, uint tableId, byte[]? buffer);
}
