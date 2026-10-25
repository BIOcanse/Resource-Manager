using System.Collections.Frozen;
using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Battery;

public sealed partial class WindowsBatteryReader
{
    private static readonly FrozenDictionary<string, MetricFieldDefinition> FieldsByName = MetricFieldRegistry.Definitions
        .Where(static definition => definition.Resource == MetricResource.Battery)
        .ToFrozenDictionary(static definition => definition.Name, StringComparer.Ordinal);

    public DeviceInformationSnapshot ReadDeclarationFields()
    {
        var capture = ReadIdentities(includeDeclarations: true);
        return WindowsInformationFieldProjection.BatteryDeclarations(capture.Identities.Select(static identity
            => new DeviceTopologyBattery(identity.ExactIdentityKey, identity.Manufacturer, identity.SerialNumber,
                identity.Information.Chemistry)).ToArray(),
            capture.Status == NativeMetricSnapshotCatalogTopologyStatus.Complete ? DateTimeOffset.UtcNow : null);
    }

    /// <summary>Uses registered names and the original identity/unit-checked read. Refresh advice belongs to the caller's directory owner.</summary>
    public BatteryFieldRead ReadFields(BatteryCatalogIdentity identity, IReadOnlyList<string> requestedFields)
    {
        var definitions = requestedFields.Select(name => FieldsByName.GetValueOrDefault(name)).ToArray();
        var codes = definitions.Where(definition => definition is not null && AppliesTo(definition.Code, identity.Information.IsRelative))
            .Select(static definition => definition!.Code).ToHashSet();
        var capture = codes.Count == 0 ? new BatteryMetricRead(identity.ExactIdentityKey,
            new Dictionary<MetricFieldCode, double>(), null, false) : Read(identity, codes);
        var observedAt = DateTimeOffset.UtcNow;
        var values = new FieldReading[requestedFields.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var definition = definitions[index];
            var field = new FieldReference(identity.ExactIdentityKey, requestedFields[index]);
            if (definition is null || !AppliesTo(definition.Code, identity.Information.IsRelative))
            {
                values[index] = new(field, FieldObservationStatus.Unsupported, null, null, "windows.battery");
                continue;
            }
            FieldValue? value = definition.Code == MetricFieldCode.BatteryPowerState
                ? capture.PowerState is { } state ? new(Integer: new MetricInteger(state)) : null
                : capture.Values.TryGetValue(definition.Code, out var number) ? new(Number: number) : null;
            values[index] = new(field, value is null ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current,
                value, value is null ? null : observedAt, "windows.battery");
        }
        return new(identity.ExactIdentityKey, Array.AsReadOnly(values), capture.RequiresTopologyRefresh);
    }
}
