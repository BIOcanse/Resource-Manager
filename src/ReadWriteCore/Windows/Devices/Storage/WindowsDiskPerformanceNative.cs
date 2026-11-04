using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed record WindowsDiskPerformanceNativeRead(string IdentityKey, WindowsDiskPerformanceMetrics? Metrics);

/// <summary>Per-capture reader over the existing validated disk-interface API. It owns no current state or handles.</summary>
internal sealed class WindowsDiskPerformanceCaptureReader
{
    internal static readonly TimeSpan MaximumSampleWindow = TimeSpan.FromSeconds(5);
    private readonly WindowsDiskLayoutInformationReader diskReader;
    private readonly TimeProvider clock;
    private readonly TimeSpan sampleWindow;

    internal WindowsDiskPerformanceCaptureReader(WindowsDiskLayoutInformationReader diskReader, TimeProvider clock, TimeSpan sampleWindow)
    {
        this.diskReader = diskReader ?? throw new ArgumentNullException(nameof(diskReader));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (sampleWindow <= TimeSpan.Zero || sampleWindow > MaximumSampleWindow)
            throw new ArgumentOutOfRangeException(nameof(sampleWindow));
        this.sampleWindow = sampleWindow;
    }

    internal async Task<WindowsDiskPerformanceCaptureResult> ReadAsync(
        IReadOnlyList<WindowsDiskPerformanceTarget> selectedTargets,
        IReadOnlyList<DeviceTopologyStorageInformation>? currentIdentities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedTargets);
        var readings = selectedTargets.Distinct().ToDictionary(static target => target,
            static _ => (WindowsDiskPerformanceMetrics?)null);
        if (selectedTargets.Count == 0 || currentIdentities is null)
            return new(readings);

        var candidates = WindowsInformationFieldProjection.PhysicalDiskRelationCandidates(currentIdentities);
        if (candidates is null) return new(readings);

        var selectedByIdentity = selectedTargets
            .Where(static target => target.SupportsExactAtaNvmeIdentity)
            .GroupBy(static target => target.IdentityKey, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.Ordinal);
        if (selectedByIdentity.Count == 0) return new(readings);
        var selectedIdentityKeys = selectedByIdentity.Keys.ToHashSet(StringComparer.Ordinal);

        var interfaces = diskReader.EnumerateInterfaces();
        if (interfaces is null) return new(readings);
        var matches = new Dictionary<string, (int Count, WindowsDiskPerformanceMetrics? Metrics)>(StringComparer.Ordinal);
        foreach (var device in interfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsDiskPerformanceNativeRead? read;
            try
            {
                read = await diskReader.ReadPerformanceAsync(device, candidates, selectedIdentityKeys, sampleWindow, clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { continue; }

            if (read is null || !selectedByIdentity.ContainsKey(read.IdentityKey)) continue;
            if (matches.TryGetValue(read.IdentityKey, out var prior))
                matches[read.IdentityKey] = (prior.Count + 1, null);
            else
                matches.Add(read.IdentityKey, (1, read.Metrics));
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var target in selectedTargets)
        {
            if (!target.SupportsExactAtaNvmeIdentity || !matches.TryGetValue(target.IdentityKey, out var match) || match.Count != 1)
                continue;
            readings[target] = match.Metrics;
        }
        return new(readings);
    }
}
