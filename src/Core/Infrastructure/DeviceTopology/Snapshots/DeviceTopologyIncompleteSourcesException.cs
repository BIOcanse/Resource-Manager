using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

internal sealed class DeviceTopologyIncompleteSourcesException : Exception
{
    public DeviceTopologyIncompleteSourcesException(
        IEnumerable<DeviceTopologySourceDiagnostic> diagnostics)
        : this(Materialize(diagnostics))
    {
    }

    private DeviceTopologyIncompleteSourcesException(
        IReadOnlyList<DeviceTopologySourceDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<DeviceTopologySourceDiagnostic> Diagnostics { get; }

    public string FirstIncompleteSourceId => Diagnostics[0].SourceId;

    private static IReadOnlyList<DeviceTopologySourceDiagnostic> Materialize(
        IEnumerable<DeviceTopologySourceDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var materialized = diagnostics.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException(
                "At least one incomplete device-topology source is required.",
                nameof(diagnostics));
        }

        return materialized;
    }

    private static string CreateMessage(
        IReadOnlyList<DeviceTopologySourceDiagnostic> diagnostics)
    {
        return $"Device topology source '{diagnostics[0].SourceId}' is incomplete; "
            + "the last-good snapshot must be retained.";
    }
}
