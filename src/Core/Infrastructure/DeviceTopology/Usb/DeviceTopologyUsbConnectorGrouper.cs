namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyUsbConnectorGrouper
{
    public static IReadOnlyList<DeviceTopologyUsbConnectorGroup> GroupUserConnectablePorts(
        IReadOnlyList<DeviceTopologyUsbPort> ports)
    {
        var portsByIdentity = ports
            .GroupBy(static port => CreatePortIdentity(port.HubDevicePath, port.PortNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var parents = portsByIdentity.Keys.ToDictionary(
            static identity => identity,
            static identity => identity,
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in portsByIdentity)
        {
            foreach (var companion in pair.Value.ConnectorProperties?.CompanionPorts ?? [])
            {
                if (string.IsNullOrWhiteSpace(companion.HubSymbolicLinkName))
                {
                    continue;
                }

                var companionIdentity = CreatePortIdentity(
                    companion.HubSymbolicLinkName,
                    companion.PortNumber);
                if (parents.ContainsKey(companionIdentity))
                {
                    Union(parents, pair.Key, companionIdentity);
                }
            }
        }

        return portsByIdentity
            .GroupBy(pair => Find(parents, pair.Key), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Select(static pair => pair.Value).ToArray())
            .Where(static group => group.Any(static port => port.ConnectorProperties?.PortIsUserConnectable == true))
            .Select(static group => CreateGroup(group))
            .OrderBy(static group => group.StableKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string CreatePortIdentity(string? hubDevicePath, uint portNumber)
    {
        return $"{NormalizeHubPath(hubDevicePath)}|{portNumber}";
    }

    private static DeviceTopologyUsbConnectorGroup CreateGroup(IReadOnlyList<DeviceTopologyUsbPort> ports)
    {
        var members = ports
            .OrderBy(static port => CreatePortIdentity(port.HubDevicePath, port.PortNumber), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var representative = members
            .OrderByDescending(static port => port.DeviceConnected)
            .ThenByDescending(static port => port.ConnectorProperties?.PortConnectorIsTypeC == true)
            .ThenByDescending(static port => port.Capability?.OperatingAtSuperSpeedPlusOrHigher == true)
            .ThenByDescending(static port => port.Capability?.OperatingAtSuperSpeedOrHigher == true)
            .ThenByDescending(static port => port.Capability?.SupportsUsb30 == true)
            .ThenBy(static port => port.PortNumber)
            .First();
        var stableKey = string.Join(
            ";",
            members.Select(static port => CreatePortIdentity(port.HubDevicePath, port.PortNumber)));
        return new DeviceTopologyUsbConnectorGroup(stableKey, representative, members);
    }

    private static string Find(IDictionary<string, string> parents, string identity)
    {
        var root = identity;
        while (!parents[root].Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            root = parents[root];
        }

        var current = identity;
        while (!parents[current].Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            var next = parents[current];
            parents[current] = root;
            current = next;
        }

        return root;
    }

    private static void Union(IDictionary<string, string> parents, string left, string right)
    {
        var leftRoot = Find(parents, left);
        var rightRoot = Find(parents, right);
        if (!leftRoot.Equals(rightRoot, StringComparison.OrdinalIgnoreCase))
        {
            parents[rightRoot] = leftRoot;
        }
    }

    private static string NormalizeHubPath(string? hubDevicePath)
    {
        var value = (hubDevicePath ?? string.Empty).Trim().Replace('/', '\\');
        if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        return value.TrimEnd('\\').ToUpperInvariant();
    }
}

internal sealed record DeviceTopologyUsbConnectorGroup(
    string StableKey,
    DeviceTopologyUsbPort Representative,
    IReadOnlyList<DeviceTopologyUsbPort> Ports);
