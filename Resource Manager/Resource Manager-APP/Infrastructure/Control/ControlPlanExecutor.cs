using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 按对象把期望状态分派给对应的写入器。
///
/// 现在一个写入器都还没有，所以每一项都回报 <c>unsupported</c>，
/// 并且**带上目录里那条真实原因**（缺哪个组件），而不是笼统一句"不支持"。
/// 接进一个写入器就少一条 unsupported —— 这个类的形状不用改。
///
/// 对象已经不在了（拔掉的显卡、认不出来的风扇）也如实回报，
/// 不静默跳过：用户设过的东西现在生效不了，他应该知道。
/// </summary>
public sealed class ControlPlanExecutor(IControlObjectCatalog catalog) : IControlPlanExecutor
{
    private const string ObjectMissing = "这个设备现在不在了，设定暂时生效不了。";
    private const string CapabilityMissing = "这个设备上没有这一项。";

    public Task<ControlApplyReport> ApplyAsync(
        ControlDesiredState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();

        var objects = catalog.ReadObjects().Objects
            .ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var outcomes = new List<ControlApplyOutcome>();

        foreach (var target in desired.Objects)
        {
            if (!objects.TryGetValue(target.ObjectId, out var controlObject))
            {
                foreach (var setting in target.Settings)
                {
                    outcomes.Add(new ControlApplyOutcome(
                        target.ObjectId,
                        setting.CapabilityId,
                        ControlApplyStatuses.Unsupported,
                        ObjectMissing));
                }
                continue;
            }

            foreach (var setting in target.Settings)
            {
                var capability = controlObject.Capabilities.FirstOrDefault(
                    entry => string.Equals(
                        entry.Id,
                        setting.CapabilityId,
                        StringComparison.Ordinal));
                outcomes.Add(new ControlApplyOutcome(
                    target.ObjectId,
                    setting.CapabilityId,
                    ControlApplyStatuses.Unsupported,
                    capability is null
                        ? CapabilityMissing
                        // 目录已经算好了为什么不能用，这里原样转达。
                        : capability.UnavailableReason ?? CapabilityMissing));
            }
        }

        return Task.FromResult(new ControlApplyReport(outcomes, DateTimeOffset.UtcNow));
    }
}
