using System.Text.Json.Serialization;

namespace ResourceManager.App.Application.MemoryProtection;

// Immutable compiled backend freedom point memory_protection.record_capacity. A full report never evicts.
public sealed record HostMemoryProtectionRecordOptions(
    [property: JsonPropertyName("record_capacity")] int RecordCapacity)
{
    public const int MaximumRecordCapacity = 16384;

    public HostMemoryProtectionRecordOptions Validate()
    {
        if (RecordCapacity is < 1 or > MaximumRecordCapacity)
            throw new InvalidDataException("Invalid host memory protection record capacity.");
        return this;
    }
}
