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
    IControlPlanExecutor executor) : IControlPlane
{
    private readonly object gate = new();
    private ControlApplyReport lastApply = ControlApplyReport.Empty;

    public async Task<ControlStateView> ReadStateAsync(CancellationToken cancellationToken)
    {
        var desired = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new ControlStateView(desired, ReadLastApply());
    }

    public async Task<ControlStateView> SetObjectSettingsAsync(
        string objectId,
        IReadOnlyList<ControlSetting> settings,
        CancellationToken cancellationToken)
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
        await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        var report = await executor.ApplyAsync(next, cancellationToken).ConfigureAwait(false);
        WriteLastApply(report);
        return new ControlStateView(next, report);
    }

    public async Task<ControlApplyReport> ReassertAsync(CancellationToken cancellationToken)
    {
        var desired = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (desired.Objects.Count == 0)
        {
            // 没设过就什么都不做 —— 不去把机器"重置"成我们以为的默认值。
            return ReadLastApply();
        }
        var report = await executor.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
        WriteLastApply(report);
        return report;
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
