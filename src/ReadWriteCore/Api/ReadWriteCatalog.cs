using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ResourceManager.ReadWrite;

/// <summary>Produced by explicit device/provider bindings. Names are lookup keys,
/// never a parser for resource kind, vendor or driver selection.</summary>
public sealed class ReadWriteCatalog
{
    public ImmutableArray<DeviceDescriptor> Devices { get; }
    public ImmutableArray<FieldCapability> Fields { get; }
    private readonly FrozenDictionary<FieldReference, FieldCapability> byField;
    private readonly FrozenDictionary<string, ImmutableArray<FieldCapability>> byDevice;

    public ReadWriteCatalog(IEnumerable<DeviceDescriptor> devices, IEnumerable<FieldCapability> fields)
    {
        Devices = [.. devices];
        Fields = [.. fields];
        var identities = Devices.Select(static item => item.Identity).ToHashSet(StringComparer.Ordinal);
        if (identities.Count != Devices.Length || identities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Duplicate or empty device identity.", nameof(devices));
        foreach (var item in Fields)
        {
            if (!identities.Contains(item.Field.DeviceIdentity) || string.IsNullOrWhiteSpace(item.Definition.Name)
                || item.Field.FieldName != item.Definition.Name)
                throw new ArgumentException("Field binding has no matching device or name.", nameof(fields));
            Validate(item.Read);
            Validate(item.Write);
        }
        byField = Fields.ToFrozenDictionary(static item => item.Field);
        byDevice = Fields.GroupBy(static item => item.Field.DeviceIdentity, StringComparer.Ordinal)
            .ToFrozenDictionary(static group => group.Key, static group => group.ToImmutableArray(), StringComparer.Ordinal);
    }

    public FieldCapability Require(FieldReference field) => byField.TryGetValue(field, out var binding)
        ? binding : throw new ArgumentException("Unknown device field.", nameof(field));
    public FieldCapability? Find(FieldReference field) => byField.GetValueOrDefault(field);
    public ImmutableArray<FieldCapability> GetFields(string deviceIdentity) => byDevice.GetValueOrDefault(deviceIdentity, []);

    private static void Validate(OperationSupport operation)
    {
        if (operation.Dependencies.IsDefault || operation.Dependencies.Any(string.IsNullOrWhiteSpace)
            || !operation.Supported && string.IsNullOrWhiteSpace(operation.Reason))
            throw new ArgumentException("Operation support and dependencies are incomplete.");
    }
}
