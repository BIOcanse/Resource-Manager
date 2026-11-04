using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class JsonGpuPlacementProcessHistoryStore
{
    public async Task SaveNoPresentAsync(string softwareId, string softwareName,
        GpuPlacementProcessInstance process, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var id = RequireText(softwareId, "Software identity is required.");
        var key = BuildProcessKey(process.ProcessName, process.ExecutablePath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var current = document.SoftwareHistories.FirstOrDefault(item => item.SoftwareId.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? GpuPlacementSoftwareProcessHistory.Empty(id, softwareName);
            var existing = current.Processes.FirstOrDefault(item => item.ProcessKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing?.GraphicsApi is not null) return;
            var now = DateTimeOffset.UtcNow;
            var negative = (existing ?? CreateObservedProcess(new(process.ProcessName, process.ExecutablePath,
                null, process.ProcessId, ["etw-present-empty-window"]), now)) with
            { NoPresentUntil = expiresAt, LastObservedAt = now, LastProcessId = process.ProcessId };
            await SaveHistoryCoreAsync(document, current with
            {
                Processes = RetainIdentifiedProcesses(current.Processes.Where(item =>
                    !item.ProcessKey.Equals(key, StringComparison.OrdinalIgnoreCase)).Append(negative)).ToArray(),
                UpdatedAt = now
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }
}
