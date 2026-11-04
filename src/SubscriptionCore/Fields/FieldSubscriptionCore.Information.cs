using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.Subscription;

public sealed partial class FieldSubscriptionCore
{
    /// <summary>Exact information fields use the original collection leases, and deliver only changed content.</summary>
    public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationChangesAsync(string id,
        ImmutableArray<FieldReference> fields, TimeSpan interval, DeviceInformationSnapshot declarations,
        CancellationToken cancellationToken = default)
    {
        Validate(id, fields, interval);
        if (fields.Distinct().Count() != fields.Length) throw new ArgumentException("Select each field once.", nameof(fields));
        foreach (var field in fields)
            if (declarations.FindField(field) is null) throw new ArgumentException("Select a declared information field.", nameof(fields));
        var selected = fields.ToHashSet();
        // Hold only the selected declarations, never the unselected devices or their sampled payloads.
        var metadata = new DeviceInformationSnapshot(null, declarations.Devices.Select(device => device with {
            Fields = device.Fields.Where(field => selected.Contains(new(device.DeviceIdentity, field.Definition.Name)))
                .Select(static field => field with { Value = null, ObservedAt = null }).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray());
        return Run(cancellationToken);

        async IAsyncEnumerable<DeviceInformationSnapshot> Run([EnumeratorCancellation] CancellationToken token)
        {
            DeviceInformationSnapshot? previous = null;
            await foreach (var readings in backend.SubscribeAsync(id, fields, interval, token))
            {
                var next = DeviceInformationContent.Project(metadata, readings);
                if (previous is not null && DeviceInformationContent.Equals(previous, next)) continue;
                previous = next;
                yield return next;
            }
        }
    }
}
