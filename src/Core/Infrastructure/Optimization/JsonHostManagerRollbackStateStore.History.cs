using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class JsonHostManagerRollbackStateStore
{
    private SqliteHostManagerSmartRecordHistoryStore? historyStore;
    private IAppSettingsStore? historySettings;
    private bool historyReady;
    private bool retainHistory;

    public JsonHostManagerRollbackStateStore(IHostEnvironment environment,
        SqliteHostManagerSmartRecordHistoryStore historyStore, IAppSettingsStore settings)
        : this(environment)
        => ConfigureHistory(historyStore, settings);

    internal void ConfigureHistory(SqliteHostManagerSmartRecordHistoryStore store, IAppSettingsStore settings)
    {
        historyStore = store;
        historySettings = settings;
    }

    private async Task EnsureHistoryReadyAsync(CancellationToken token)
    {
        if (historyStore is null || historyReady) return;
        // Event-backed settings; the ordinary settings commit boundary refreshes later changes.
        retainHistory = (await historySettings!.LoadAsync(token)).Settings.Performance.KeepSmartRecordHistory;
        await Task.Run(() => historyStore.ReconcileAsync(cachedDocument!, timeProvider.GetUtcNow(), token), token);
        historyReady = true;
        if (retainHistory)
        {
            var prepared = await PrepareHistoryAsync(cachedDocument!, timeProvider.GetUtcNow(), token);
            if (!AppliedPlacementsEqual(cachedDocument!.AppliedPlacements, prepared.AppliedPlacements))
                await PersistAndPublishAsync(prepared, timeProvider.GetUtcNow(), token);
        }
    }

    private async Task RefreshHistoryRetentionAsync(CancellationToken token)
    {
        if (historySettings is null) return;
        retainHistory = (await historySettings.LoadAsync(token)).Settings.Performance.KeepSmartRecordHistory;
        if (!retainHistory || cachedDocument!.AppliedPlacements.All(item => item.Records.All(record => record.Sequence.HasValue))) return;
        var now = timeProvider.GetUtcNow();
        var prepared = await PrepareHistoryAsync(cachedDocument, now, token);
        await PersistAndPublishAsync(prepared, now, token);
    }

    private Task<HostManagerRollbackStateDocument> PrepareHistoryAsync(
        HostManagerRollbackStateDocument next, DateTimeOffset now, CancellationToken token)
        => historyStore is null ? Task.FromResult(next)
            : Task.Run(() => historyStore.PrepareAsync(cachedDocument!, next, retainHistory, now, token), token);

    private Task CompleteHistoryAsync(HostManagerRollbackStateDocument previous,
        HostManagerRollbackStateDocument next, DateTimeOffset now, CancellationToken token)
        => historyStore is null ? Task.CompletedTask
            : Task.Run(() => historyStore.CompleteAsync(previous, next, now, token,
                ResolveHistoryRestoreResults(previous, next)), token);

    private Task StageHistoryRemovalsAsync(HostManagerRollbackStateDocument previous,
        HostManagerRollbackStateDocument next, DateTimeOffset now, CancellationToken token)
        => historyStore is null ? Task.CompletedTask
            : Task.Run(() => historyStore.StageRemovalsAsync(previous, next, now, token,
                ResolveHistoryRestoreResults(previous, next)), token);

    private static IReadOnlyDictionary<long, string> ResolveHistoryRestoreResults(
        HostManagerRollbackStateDocument previous, HostManagerRollbackStateDocument next)
    {
        var results = next.RestoreResults is null ? new Dictionary<long, string>()
            : new Dictionary<long, string>(next.RestoreResults);
        if (next.RecordRestoreResults is not null)
            foreach (var placement in previous.AppliedPlacements)
                foreach (var record in placement.Records)
                    if (record.Sequence is { } sequence && next.RecordRestoreResults.TryGetValue(
                        HostManagerRecordHistoryKey.Create(placement, record), out var result))
                        results[sequence] = result;
        return results;
    }
}
