using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 控制面的回路：存 → 施加 → 回执。
///
/// 顺序是固定的，**先存后施加**：写成功但存失败会让下次启动悄悄回到旧值，
/// 而用户设过的东西不该自己变回去。反过来存成功但写失败只是"这次没应用上"，
/// 界面看得见，下次重新施加还有机会。
/// </summary>
public sealed class ControlPlane(
    IControlDesiredStateStore store,
    IControlWriteLayer writeLayer,
    StartupCapabilitySet? capabilities = null,
    HardwareMetricCatalogState? catalog = null,
    FanControlCoreClient? fan = null,
    HardwareCommandQueue? commands = null) : IControlPlane
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private ControlApplyReport lastApply = ControlApplyReport.Empty;
    private ControlDesiredState lastDesired = ControlDesiredState.Empty;
    private bool initialized;
    private bool appliedControlIntentsExist;
    private bool appliedFanControlExists;
    internal bool AppliedControlIntentsExist => Volatile.Read(ref appliedControlIntentsExist);
    internal bool AppliedFanControlExists => Volatile.Read(ref appliedFanControlExists);
    internal event Action? AppliedIntentsChanged;

    private bool CanMutate => capabilities is null || capabilities.Allows(
        StartupCapability.MutablePersistence | StartupCapability.RuntimeEffectOwners);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!CanMutate) return;
        await WaitForCatalogAsync(cancellationToken).ConfigureAwait(false);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            initialized = true;
            await ApplyStoredAsync(await store.LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    private async Task WaitForCatalogAsync(CancellationToken token)
    {
        if (catalog is null || catalog.Catalog is { CatalogGeneration: > 0 }) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (catalog.Catalog is { CatalogGeneration: > 0 }) ready.TrySetResult(); }
        catalog.Changed += Changed;
        try { Changed(); await ready.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { catalog.Changed -= Changed; }
    }

    private static ControlApplyReport Rejected(ControlDesiredState desired, string reason)
        => new(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
            new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Unsupported, reason))).ToArray(), DateTimeOffset.UtcNow);

    private async Task<ControlStateView?> RejectMutationAsync(ControlDesiredState request, CancellationToken token)
    {
        if (CanMutate) return null;
        var report = Rejected(request, "startup-profile-read-only");
        WriteLastApply(report);
        return new(await store.LoadAsync(token).ConfigureAwait(false), report);
    }

    public async Task<ControlStateView> ReadStateAsync(CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new ControlStateView(desired, ReadLastApply());
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>
    /// 整份替换。草稿应用和配置应用都走这里 —— 用户面对的是一整套设定，
    /// 不是一条条分别提交。
    /// </summary>
    public async Task<ControlStateView> ApplyDesiredStateAsync(
        ControlDesiredState desired,
        CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(desired);

            if (await RejectMutationAsync(desired, cancellationToken).ConfigureAwait(false) is { } rejected) return rejected;

            var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            // 空设定的对象整条去掉，不留一条空的 —— "不管这个对象"和"管它但什么都没设"
            // 在状态机里应当是同一件事的同一种写法。
            var next = new ControlDesiredState(desired.Objects
                .Where(static entry => entry.Settings.Count > 0)
                .ToArray());
            return await SaveAndApplyAsync(current, next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task<ControlStateView> SetObjectSettingsAsync(
        string objectId,
        IReadOnlyList<ControlSetting> settings,
        CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
            ArgumentNullException.ThrowIfNull(settings);

            if (await RejectMutationAsync(new([new(objectId, settings)]), cancellationToken).ConfigureAwait(false) is { } rejected) return rejected;

            var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var objects = current.Objects
                .Where(entry => !string.Equals(entry.ObjectId, objectId, StringComparison.Ordinal))
                .ToList();
            // 空设定表示"别管这个对象了"，所以整条去掉而不是留一条空的。
            if (settings.Count > 0)
            {
                objects.Add(new ControlObjectDesiredState(objectId, settings));
            }

            var next = new ControlDesiredState(objects);
            return await SaveAndApplyAsync(current, next, cancellationToken, objectId).ConfigureAwait(false);
        }
        finally
        {
            operations.Release();
        }
    }

    /// <summary>
    /// **先存后施加。** 写成功但存失败会让下次启动悄悄回到旧值 ——
    /// 用户设过的东西不该自己变回去。反过来存成功但写失败只是"这次没应用上"，
    /// 界面看得见，下次重新施加还有机会。
    /// </summary>
    private async Task<ControlStateView> SaveAndApplyAsync(
        ControlDesiredState current,
        ControlDesiredState next,
        CancellationToken cancellationToken,
        string? objectId = null)
    {
        var invalid = InvalidFanModes(next, objectId);
        if (invalid.Length > 0) return new(current, RejectFanModes(invalid, objectId));
        if (commands is not null) await WaitForCatalogAsync(cancellationToken).ConfigureAwait(false);
        using var heldCommands = HoldCommands(current.Objects.Concat(current.PendingReleases).Concat(next.Objects), objectId);
        var pending = current.PendingReleases.Concat(current.Objects)
            .GroupBy(target => target.ObjectId, StringComparer.Ordinal)
            .Select(group => new ControlObjectDesiredState(group.Key, group.SelectMany(target => target.Settings)
                .DistinctBy(setting => setting.CapabilityId)
                .Where(setting => !next.Objects.Any(target => target.ObjectId == group.Key
                    && target.Settings.Any(kept => SameOwnership(kept, setting)))).ToArray()))
            .Where(target => target.Settings.Count > 0).ToArray();
        next = next with { PendingReleases = pending };
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        UpdateRetainedIntents(next);
        return await ApplyStoredAsync(next, cancellationToken, objectId).ConfigureAwait(false);
    }

    private static ControlObjectDesiredState[] InvalidFanModes(ControlDesiredState state, string? objectId)
        => state.Objects.Where(target => (objectId is null || target.ObjectId == objectId)
            && target.ObjectId.StartsWith("fan:", StringComparison.Ordinal)
            && Writers.FanControlWriter.IsMixed(target.Settings)).ToArray();

    private ControlApplyReport RejectFanModes(ControlObjectDesiredState[] invalid, string? objectId)
    {
        var report = Rejected(new(invalid), Writers.FanControlWriter.MixedManagementModes);
        if (objectId is not null) report = report with { Outcomes = ReadLastApply().Outcomes
            .Where(outcome => outcome.ObjectId != objectId).Concat(report.Outcomes).ToArray() };
        WriteLastApply(report);
        return report;
    }

    private static bool SameOwnership(ControlSetting left, ControlSetting right)
        => left.CapabilityId == right.CapabilityId && left.CurveExecution == right.CurveExecution;

    private IDisposable HoldCommands(IEnumerable<ControlObjectDesiredState> targets, string? objectId = null)
    {
        var selected = targets.Where(target => objectId is null || target.ObjectId == objectId).ToArray();
        var fanScope = fan is not null && selected.Any(target =>
            target.ObjectId.StartsWith("fan:", StringComparison.Ordinal) && target.Settings.Count > 0)
            ? fan.AcquirePendingScope() : null;
        try { return new PendingScopes(fanScope, commands is not null && NeedsBridge(selected) ? commands.AcquirePendingScope() : null); }
        catch { fanScope?.Dispose(); throw; }
    }

    private sealed class PendingScopes(IDisposable? fanScope, IDisposable? bridgeScope) : IDisposable
    {
        public void Dispose()
        {
            try { bridgeScope?.Dispose(); }
            finally { fanScope?.Dispose(); }
        }
    }

    private bool NeedsBridge(IEnumerable<ControlObjectDesiredState> objects, string? objectId = null)
        => objects.Any(target => (objectId is null || target.ObjectId == objectId)
            && target.Settings.Any(setting => writeLayer.UsesHardwareBridge(target.ObjectId, setting.CapabilityId)));

    private async Task<ControlStateView> ApplyStoredAsync(
        ControlDesiredState next, CancellationToken cancellationToken, string? objectId = null, bool retryOnly = false)
    {
        if (commands is not null) await WaitForCatalogAsync(cancellationToken).ConfigureAwait(false);
        using var heldCommands = HoldCommands(next.Objects.Concat(next.PendingReleases), objectId);
        if (commands is null) return await ApplyStoredCoreAsync(next, cancellationToken, objectId, retryOnly).ConfigureAwait(false);
        if (objectId is not null)
            return NeedsBridge(next.Objects.Concat(next.PendingReleases), objectId)
                ? await commands.ExecuteAsync(token => ApplyStoredCoreAsync(next, token, objectId, retryOnly), cancellationToken).ConfigureAwait(false)
                : await ApplyStoredCoreAsync(next, cancellationToken, objectId, retryOnly).ConfigureAwait(false);

        // Each object is an original control command. Unrelated targets do not
        // wait for bridge startup, even when the same saved batch also contains AMD settings.
        var targets = next.Objects.Concat(next.PendingReleases).Select(target => target.ObjectId)
            .Distinct(StringComparer.Ordinal).Select(id => (Id: id, Bridge: NeedsBridge(next.Objects.Concat(next.PendingReleases), id)))
            .OrderBy(target => target.Bridge).ToArray();
        if (targets.Length == 0 || targets.All(target => !target.Bridge))
            return await ApplyStoredCoreAsync(next, cancellationToken, retryOnly: retryOnly).ConfigureAwait(false);
        if (targets.All(target => target.Bridge))
            return await commands.ExecuteAsync(token => ApplyStoredCoreAsync(next, token, retryOnly: retryOnly), cancellationToken).ConfigureAwait(false);
        var ids = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var previous = ReadLastApply();
        WriteLastApply(previous with { Outcomes = previous.Outcomes.Where(outcome => ids.Contains(outcome.ObjectId)).ToArray() }, next);
        ControlStateView? result = null;
        foreach (var target in targets)
        {
            result = target.Bridge
                ? await commands.ExecuteAsync(token => ApplyStoredCoreAsync(next, token, target.Id, retryOnly), cancellationToken).ConfigureAwait(false)
                : await ApplyStoredCoreAsync(next, cancellationToken, target.Id, retryOnly).ConfigureAwait(false);
            next = result.Desired;
        }
        return result!;
    }

    private async Task<ControlStateView> ApplyStoredCoreAsync(
        ControlDesiredState next, CancellationToken cancellationToken, string? objectId = null, bool retryOnly = false)
    {
        var invalid = InvalidFanModes(next, objectId);
        if (invalid.Length > 0) return new(next, RejectFanModes(invalid, objectId));
        var releases = next.PendingReleases.Where(target => objectId is null || target.ObjectId == objectId).ToArray();
        var releaseReport = releases.Length == 0
            ? ControlApplyReport.Empty
            : await writeLayer.ReleaseAsync(new(releases), next, cancellationToken).ConfigureAwait(false);
        var pending = next.PendingReleases.Select(target => new ControlObjectDesiredState(target.ObjectId,
            target.Settings.Where(setting => !releaseReport.Outcomes.Any(outcome =>
                outcome.ObjectId == target.ObjectId && outcome.CapabilityId == setting.CapabilityId
                && outcome.Status == ControlApplyStatuses.Applied)).ToArray()))
            .Where(target => target.Settings.Count > 0).ToArray();
        if (pending.Sum(target => target.Settings.Count) != next.PendingReleases.Sum(target => target.Settings.Count))
        {
            next = next with { PendingReleases = pending };
            await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        }
        var selected = objectId is null ? next : new ControlDesiredState(
            next.Objects.Where(target => target.ObjectId == objectId).ToArray());
        var blocked = selected.Objects.Where(target => pending.Any(release => release.ObjectId == target.ObjectId)).ToArray();
        selected = selected with { Objects = selected.Objects.Where(target =>
            !pending.Any(release => release.ObjectId == target.ObjectId)).ToArray() };
        if (retryOnly)
        {
            var failed = ReadLastApply().Outcomes.Where(outcome => outcome.ObjectId == objectId
                && outcome.Status is ControlApplyStatuses.Failed or ControlApplyStatuses.Unsupported)
                .Select(outcome => outcome.CapabilityId).ToHashSet(StringComparer.Ordinal);
            selected = new(selected.Objects.Select(target => target with
                { Settings = target.Settings.Where(setting => failed.Contains(setting.CapabilityId)).ToArray() })
                .Where(target => target.Settings.Count > 0).ToArray());
        }
        var applied = await writeLayer.WriteAsync(selected, cancellationToken).ConfigureAwait(false);
        var replaced = releaseReport.Outcomes.Concat(applied.Outcomes)
            .Select(outcome => (outcome.ObjectId, outcome.CapabilityId)).ToHashSet();
        var retained = ReadLastApply().Outcomes.Where(outcome => retryOnly
            ? !replaced.Contains((outcome.ObjectId, outcome.CapabilityId))
            : objectId is not null && outcome.ObjectId != objectId);
        var blockedOutcomes = blocked.SelectMany(target => target.Settings.Select(setting =>
            new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Failed, "control-release-failed")));
        // The current receipt for a capability is the replacement write (or its
        // failure), never a successful release of the previous ownership mode.
        var report = new ControlApplyReport(retained.Concat(releaseReport.Outcomes).Concat(applied.Outcomes).Concat(blockedOutcomes)
            .GroupBy(outcome => (outcome.ObjectId, outcome.CapabilityId)).Select(group => group.Last()).ToArray(), DateTimeOffset.UtcNow);
        WriteLastApply(report, next);
        return new ControlStateView(next, report);
    }

    public async Task<ControlApplyReport> ReassertAsync(CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanMutate) return ReadLastApply();
            if (commands is not null) await WaitForCatalogAsync(cancellationToken).ConfigureAwait(false);
            var previous = ReadLastApply();
            var saved = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var selected = new ControlDesiredState(saved.Objects.Select(target => new ControlObjectDesiredState(target.ObjectId,
                target.Settings.Where(setting => previous.Outcomes.Any(outcome => outcome.ObjectId == target.ObjectId
                    && outcome.CapabilityId == setting.CapabilityId && outcome.Status == ControlApplyStatuses.Applied)).ToArray()))
                .Where(target => target.Settings.Count > 0).ToArray());
            if (selected.Objects.Count == 0) return previous;
            using var heldCommands = HoldCommands(selected.Objects);
            async Task<ControlApplyReport> Apply(ControlDesiredState target, CancellationToken token)
            {
                var report = await writeLayer.WriteAsync(target, token).ConfigureAwait(false);
                var updated = new ControlApplyReport(ReadLastApply().Outcomes.Where(old => !report.Outcomes.Any(next =>
                    next.ObjectId == old.ObjectId && next.CapabilityId == old.CapabilityId)).Concat(report.Outcomes).ToArray(), DateTimeOffset.UtcNow);
                WriteLastApply(updated, saved);
                return updated;
            }
            if (commands is null || selected.Objects.All(target => !NeedsBridge([target])))
                return await Apply(selected, cancellationToken).ConfigureAwait(false);
            if (selected.Objects.All(target => NeedsBridge([target])))
                return await commands.ExecuteAsync(token => Apply(selected, token), cancellationToken).ConfigureAwait(false);
            foreach (var target in selected.Objects.OrderBy(target => NeedsBridge([target])))
            {
                var one = new ControlDesiredState([target]);
                if (commands is not null && NeedsBridge(one.Objects))
                    await commands.ExecuteAsync(token => Apply(one, token), cancellationToken).ConfigureAwait(false);
                else await Apply(one, cancellationToken).ConfigureAwait(false);
            }
            return ReadLastApply();
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task ForgetInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanMutate) throw new InvalidOperationException("startup-profile-read-only");
            var saved = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(saved with
            {
                Objects = saved.Objects.Where(target => target.ObjectId != instanceId).ToArray(),
                PendingReleases = saved.PendingReleases.Where(target => target.ObjectId != instanceId).ToArray()
            }, cancellationToken).ConfigureAwait(false);
            var previous = ReadLastApply();
            WriteLastApply(previous with { Outcomes = previous.Outcomes.Where(outcome => outcome.ObjectId != instanceId).ToArray() },
                saved with { Objects = saved.Objects.Where(target => target.ObjectId != instanceId).ToArray() });
        }
        finally { operations.Release(); }
    }

    public async Task<ControlStateView> RetryObjectAsync(string objectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (await RejectMutationAsync(saved, cancellationToken).ConfigureAwait(false) is { } rejected) return rejected;
            return await ApplyStoredAsync(saved, cancellationToken, objectId, retryOnly: true).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    private ControlApplyReport ReadLastApply()
    {
        lock (gate)
        {
            return lastApply;
        }
    }

    private void UpdateRetainedIntents(ControlDesiredState desired)
    {
        bool changed;
        lock (gate)
        {
            lastDesired = desired;
            var previous = appliedControlIntentsExist;
            UpdateAppliedIntentsLocked(updateFan: false);
            changed = previous != appliedControlIntentsExist;
        }
        if (changed) AppliedIntentsChanged?.Invoke();
    }

    private void WriteLastApply(ControlApplyReport report, ControlDesiredState? desired = null)
    {
        lock (gate)
        {
            lastApply = report;
            if (desired is not null) lastDesired = desired;
            UpdateAppliedIntentsLocked(updateFan: true);
        }
        AppliedIntentsChanged?.Invoke();
    }

    private void UpdateAppliedIntentsLocked(bool updateFan)
    {
        var applied = lastApply.Outcomes.Where(outcome => outcome.Status == ControlApplyStatuses.Applied
            && lastDesired.Objects.Any(target => target.ObjectId == outcome.ObjectId
                && target.Settings.Any(setting => setting.CapabilityId == outcome.CapabilityId))).ToArray();
        Volatile.Write(ref appliedControlIntentsExist, applied.Length != 0);
        // A fan's successful release is published with its outcome, while the
        // shared pending scope owns the entire intervening native operation.
        if (updateFan) Volatile.Write(ref appliedFanControlExists,
            applied.Any(outcome => outcome.ObjectId.StartsWith("fan:", StringComparison.Ordinal)));
    }

}
