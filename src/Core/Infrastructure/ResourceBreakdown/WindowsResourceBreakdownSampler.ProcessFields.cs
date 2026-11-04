using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private NetworkAttributionSnapshot ReadNetworkAttribution(NetworkAttributionReadRequest request)
    {
        try { return networkAttributionReader.Read(request); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ExternalException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        {
            logger?.LogError(error, "Network process attribution sampling failed.");
            return new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
                new("network-attribution", NetworkAttributionProviderStates.Failed, "The network process source is unavailable."));
        }
    }

    private static ProcessMetricFieldSnapshot CreateProcessNetworkFields(IReadOnlyList<string> selected,
        IReadOnlyList<ProcessResourceSample> samples, ProcessNetworkTrafficWindow? window)
        => ResourceManager.ReadWrite.ProcessFields.WindowsProcessFieldProjection.CreateProcessNetworkFields(selected, FieldProjectionSamples(samples), window);
}
