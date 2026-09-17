using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 按对象把期望状态分派给对应的写入器。
///
/// 谁能写这一项，问写入器自己（目录里那个 <c>Supported</c> 也是这么来的，
/// 所以界面上能调的和这里写得下去的永远是同一批）。没人认领就如实回报
/// <c>unsupported</c>，并带上目录里那条真实原因，而不是笼统一句"不支持"。
///
/// 对象已经不在了（拔掉的显卡、认不出来的风扇）也如实回报，
/// 不静默跳过：用户设过的东西现在生效不了，他应该知道。
/// </summary>
public sealed class ControlPlanExecutor(
    IControlObjectCatalog catalog,
    IEnumerable<IControlWriter> writers) : IControlPlanExecutor
{
    private const string ObjectMissing = "这个设备现在不在了，设定暂时生效不了。";
    private const string CapabilityMissing = "这个设备上没有这一项。";

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();

    public async Task<ControlApplyReport> ApplyAsync(
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
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await ApplyOneAsync(controlObject, setting, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return new ControlApplyReport(outcomes, DateTimeOffset.UtcNow);
    }

    private async Task<ControlApplyOutcome> ApplyOneAsync(
        ControlObject controlObject,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        var capability = controlObject.Capabilities.FirstOrDefault(
            entry => string.Equals(entry.Id, setting.CapabilityId, StringComparison.Ordinal));
        if (capability is null)
        {
            return new ControlApplyOutcome(
                controlObject.Id,
                setting.CapabilityId,
                ControlApplyStatuses.Unsupported,
                CapabilityMissing);
        }

        foreach (var writer in writers)
        {
            var availability = writer.Probe(controlObject, capability);
            if (!availability.IsMine)
            {
                continue;
            }
            if (!availability.CanWrite)
            {
                return new ControlApplyOutcome(
                    controlObject.Id,
                    setting.CapabilityId,
                    ControlApplyStatuses.Unsupported,
                    availability.Reason ?? CapabilityMissing);
            }
            return await writer
                .WriteAsync(controlObject, capability, setting, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ControlApplyOutcome(
            controlObject.Id,
            setting.CapabilityId,
            ControlApplyStatuses.Unsupported,
            // 目录已经算好了为什么不能用，这里原样转达。
            capability.UnavailableReason ?? CapabilityMissing);
    }
}
