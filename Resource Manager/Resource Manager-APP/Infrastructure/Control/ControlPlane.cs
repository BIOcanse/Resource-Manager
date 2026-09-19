using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

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
    IControlWriteLayer writeLayer) : IControlPlane
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private ControlApplyReport lastApply = ControlApplyReport.Empty;

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
        var pending = current.PendingReleases.Concat(current.Objects)
            .GroupBy(target => target.ObjectId, StringComparer.Ordinal)
            .Select(group => new ControlObjectDesiredState(group.Key, group.SelectMany(target => target.Settings)
                .DistinctBy(setting => setting.CapabilityId)
                .Where(setting => !next.Objects.Any(target => target.ObjectId == group.Key
                    && target.Settings.Any(kept => kept.CapabilityId == setting.CapabilityId))).ToArray()))
            .Where(target => target.Settings.Count > 0).ToArray();
        next = next with { PendingReleases = pending };
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return await ApplyStoredAsync(next, cancellationToken, objectId).ConfigureAwait(false);
    }

    private async Task<ControlStateView> ApplyStoredAsync(
        ControlDesiredState next, CancellationToken cancellationToken, string? objectId = null)
    {
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
        var applied = await writeLayer.WriteAsync(selected, cancellationToken).ConfigureAwait(false);
        var retained = ReadLastApply().Outcomes.Where(outcome => objectId is not null && outcome.ObjectId != objectId);
        var report = new ControlApplyReport(retained.Concat(releaseReport.Outcomes).Concat(applied.Outcomes).ToArray(), DateTimeOffset.UtcNow);
        WriteLastApply(report);
        return new ControlStateView(next, report);
    }

    public async Task<ControlApplyReport> ReassertAsync(CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (desired.Objects.Count == 0 && desired.PendingReleases.Count == 0)
            {
                // 没设过就什么都不做 —— 不去把机器"重置"成我们以为的默认值。
                return ReadLastApply();
            }
            return (await ApplyStoredAsync(desired, cancellationToken).ConfigureAwait(false)).LastApply;
        }
        finally
        {
            operations.Release();
        }
    }

    private ControlApplyReport ReadLastApply()
    {
        lock (gate)
        {
            return lastApply;
        }
    }

    private void WriteLastApply(ControlApplyReport report)
    {
        lock (gate)
        {
            lastApply = report;
        }
    }
}
