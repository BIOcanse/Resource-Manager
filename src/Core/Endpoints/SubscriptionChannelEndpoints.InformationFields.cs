using System.Collections.Immutable;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.Subscription;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static CompiledFrontendSubscription CompileInformationFieldsSubscription(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        var topology = services.GetRequiredService<IDeviceTopologySnapshotProvider>();
        if (subscription.Fields is null)
            return CompileDeviceTopologySubscription(subscription, topology, interval, fieldsOnly: true);
        // Validation is metadata-only and occurs before opening the transport.
        var values = services.GetRequiredService<FieldSubscriptionCore>().SubscribeInformationChangesAsync(
            $"frontend-channel:{subscription.Id}", subscription.Fields.ToImmutableArray(), interval, topology.ReadState().InformationFields);
        return subscription.Compile(async (writer, token) =>
        {
            await foreach (var value in values.WithCancellation(token))
                await writer.WriteAsync(subscription.Id, value, token);
        });
    }
}
