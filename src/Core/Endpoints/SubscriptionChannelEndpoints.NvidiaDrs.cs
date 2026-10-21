using System.Globalization;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.Subscription;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static CompiledFrontendSubscription CompileNvidiaDrsSubscription(ParsedFrontendSubscription subscription,
        IServiceProvider services, TimeSpan interval)
    {
        string Single(string name)
        {
            var values = subscription.Query[name];
            if (values.Count != 1 || string.IsNullOrEmpty(values[0])) throw new ArgumentException("One explicit " + name + " is required.");
            return values[0]!;
        }
        uint U32(string name) => uint.TryParse(Single(name), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : throw new ArgumentException("Invalid " + name + ".");
        var name = NvidiaDrsSubscriptionPath.DecodeProfileName(Single("profile"));
        var ids = Single("settings").Split(',').Select(text => text.Length == 8
            && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
                ? id : throw new ArgumentException("setting IDs must be eight hexadecimal characters.")).ToArray();
        if (!ulong.TryParse(Single("maxPayloadBytes"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            throw new ArgumentException("Invalid maxPayloadBytes.");
        var scope = new NvidiaDrsInformationRequest([new(name, ids)],
            new(U32("maxProfiles"), U32("maxPerProfile"), U32("maxTotalSettings"), U32("pageSize"), bytes));
        var provider = services.GetRequiredService<DeviceTopologySnapshotProvider>();
        return subscription.Compile(async (writer, token) =>
        {
            await foreach (var values in provider.SubscribeNvidiaDrsFieldsAsync("frontend-channel:" + subscription.Id,
                scope, interval, FieldSubscriptionOptions.Default, token))
                await writer.WriteAsync(subscription.Id, values, token);
        });
    }
}
