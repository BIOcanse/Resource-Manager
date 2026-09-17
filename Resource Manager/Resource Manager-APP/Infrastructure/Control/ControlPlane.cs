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
    IControlPlanExecutor executor,
    IControlObjectCatalog catalog) : IControlPlane
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
        // 撤掉一项不能只是"以后不再写它"：硬件上还留着上次写进去的值。
        // 所以这一次施加，除了新的期望，还要把撤掉的那些明确写回硬件默认。
        var plan = WithReleasedRestoredToDefault(current, next);
        var report = await executor.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 在这一次要施加的计划里，补上"撤掉的那些项恢复默认"。
    ///
    /// 存下去的期望状态里**不留**这些补充项 —— 用户撤掉了就是撤掉了，
    /// 不该在他的设定里冒出一条他没设过的"= 默认值"。它们只属于这一次写入。
    /// 默认值取自目录里那一项的范围；没有默认值的（比如曲线）没法恢复，跳过。
    /// </summary>
    private ControlDesiredState WithReleasedRestoredToDefault(
        ControlDesiredState previous,
        ControlDesiredState next)
    {
        var objects = catalog.ReadObjects().Objects
            .ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var plan = next.Objects.ToDictionary(
            static entry => entry.ObjectId,
            static entry => entry.Settings.ToList(),
            StringComparer.Ordinal);

        foreach (var before in previous.Objects)
        {
            if (!objects.TryGetValue(before.ObjectId, out var controlObject))
            {
                continue;
            }
            var stillSet = plan.TryGetValue(before.ObjectId, out var kept)
                ? kept
                : [];
            foreach (var setting in before.Settings)
            {
                if (stillSet.Any(entry => string.Equals(
                        entry.CapabilityId,
                        setting.CapabilityId,
                        StringComparison.Ordinal)))
                {
                    continue;
                }
                var capability = controlObject.Capabilities.FirstOrDefault(entry =>
                    string.Equals(entry.Id, setting.CapabilityId, StringComparison.Ordinal));
                if (capability?.Range?.DefaultValue is not { } standard)
                {
                    continue;
                }
                if (!plan.TryGetValue(before.ObjectId, out var settings))
                {
                    settings = [];
                    plan[before.ObjectId] = settings;
                }
                settings.Add(new ControlSetting(setting.CapabilityId, Number: standard));
            }
        }

        return new ControlDesiredState(plan
            .Where(static entry => entry.Value.Count > 0)
            .Select(static entry => new ControlObjectDesiredState(entry.Key, entry.Value))
            .ToArray());
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
