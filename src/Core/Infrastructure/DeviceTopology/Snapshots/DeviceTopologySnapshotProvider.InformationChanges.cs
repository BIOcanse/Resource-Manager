using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationChangesAsync(
        string subscriptionId, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DeviceInformationSnapshot? previous = null;
        await foreach (var state in SubscribeAsync(subscriptionId, interval, cancellationToken))
        {
            if (previous is not null && DeviceInformationContent.Equals(previous, state.InformationFields)) continue;
            previous = state.InformationFields;
            yield return previous;
        }
    }
}
