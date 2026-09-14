using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class NativeDisplayCoordinatorReadProjection
{
    internal static IReadOnlyList<DeviceTopologyResolvedDisplayPath> CreateDisplayPaths(
        IReadOnlyList<NativeDisplayNodeOutput> nodes,
        NativeDisplayCoordinatorPayloadCatalog payloads)
    {
        var output = new List<DeviceTopologyResolvedDisplayPath>();
        foreach (var node in nodes)
        {
            if (node.Kind != (uint)NativeDisplayNodeKind.Connector)
            {
                continue;
            }

            var path = payloads.ResolveDisplayPath(node.PrimaryPayloadHandle);
            var profile = payloads.ResolveOemProfile(node.OemProfilePayloadHandle);
            if (path is null && profile is not null)
            {
                path = CreateSyntheticPath(profile);
            }

            if (path is not null)
            {
                output.Add(new DeviceTopologyResolvedDisplayPath(path, profile));
            }
        }

        return output;
    }

    private static DeviceTopologyDisplayPath CreateSyntheticPath(
        DeviceTopologyOemDisplayConnectorProfile profile)
        => new(
            0,
            0,
            0,
            0,
            profile.OutputTechnology,
            0,
            null,
            null,
            null,
            null,
            0,
            0,
            false,
            false,
            string.Empty);
}
