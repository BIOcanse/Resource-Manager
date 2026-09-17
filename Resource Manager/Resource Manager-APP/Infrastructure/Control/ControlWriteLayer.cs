using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 统一写入层。**只管写。**
///
/// 一条设定从状态机到硬件要过五关，全部在这里：
///
/// <list type="number">
/// <item>**认实例** —— 这个对象现在还在不在这台机器上。</item>
/// <item>**选后端** —— 问各后端适配器：这一项你管不管、现在能不能写。</item>
/// <item>**适配单位** —— 把状态机里那个带单位的值换成后端要的量纲；换不了就失败。</item>
/// <item>**下发** —— 交给后端。</item>
/// <item>**归一回执** —— 后端只交原始结果，"算不算写成功"由这一层定义。</item>
/// </list>
///
/// **读不在这里。** 这台机器现在实际是什么样，属于统一订阅源那一侧 ——
/// 两边分开之后，"用户想要什么"和"机器现在是什么样"各有各的属主和各自的节奏。
///
/// 期望状态机也不在这里：每个功能部分有自己的期望状态机，控制面这一份只是其中之一。
/// 它们都把内容交给这一层，这一层不关心它们是谁。
/// </summary>
public sealed class ControlWriteLayer(
    IControlObjectCatalog catalog,
    IEnumerable<IControlWriter> writers,
    ILogger<ControlWriteLayer>? logger = null) : IControlWriteLayer
{
    private const string ObjectMissing = "这个设备现在不在了，设定暂时生效不了。";
    private const string CapabilityMissing = "这个设备上没有这一项。";
    private const string WriterMissing = "控制写入尚未接入，当前只能读取。";

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();

    public async Task<ControlApplyReport> WriteAsync(
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
            // 一、认实例。不在了也如实回报，不静默跳过 ——
            // 用户设过的东西现在生效不了，他应该知道。
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
                outcomes.Add(await WriteOneAsync(controlObject, setting, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return new ControlApplyReport(outcomes, DateTimeOffset.UtcNow);
    }

    private async Task<ControlApplyOutcome> WriteOneAsync(
        ControlObject controlObject,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        var capability = controlObject.Capabilities.FirstOrDefault(
            entry => string.Equals(entry.Id, setting.CapabilityId, StringComparison.Ordinal));
        if (capability is null)
        {
            return Outcome(controlObject, setting, ControlApplyStatuses.Unsupported, CapabilityMissing);
        }

        // 二、选后端。
        if (FindBackend(controlObject, capability) is not { } backend)
        {
            return Outcome(
                controlObject,
                setting,
                ControlApplyStatuses.Unsupported,
                capability.UnavailableReason ?? WriterMissing);
        }
        if (!backend.Availability.CanWrite)
        {
            return Outcome(
                controlObject,
                setting,
                ControlApplyStatuses.Unsupported,
                backend.Availability.Reason ?? CapabilityMissing);
        }

        // 三、适配单位。换不了就如实失败，不静默照着数字写下去。
        var adapted = Adapt(setting, capability, backend.Availability);
        if (adapted is null)
        {
            logger?.LogWarning(
                "单位对不上：{Object} / {Capability} 存的是 {From}，后端要的是 {To}。",
                controlObject.Id,
                capability.Id,
                setting.Unit,
                backend.Availability.Range?.Unit ?? capability.Range?.Unit);
            return Outcome(
                controlObject,
                setting,
                ControlApplyStatuses.Failed,
                "这条设定的单位和这台机器上的这一项对不上。");
        }

        // 四、下发。五、归一由后端交回原始结果、这里统一成 ControlApplyOutcome。
        return await backend.Writer
            .WriteAsync(controlObject, capability, adapted, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把状态机里那个带单位的值换成后端要的量纲。
    ///
    /// 范围优先用后端报的那一份（它才知道硬件的真实范围），没有才退到目录里的形状。
    /// 旧文件里的记录没有单位，按目标单位解释 —— 它是在只有一种单位的年代写下的。
    /// </summary>
    private static ControlSetting? Adapt(
        ControlSetting setting,
        ControlCapability capability,
        ControlWriteAvailability availability)
    {
        if (setting.Number is not { } number)
        {
            // 开关和曲线没有量纲，原样传。
            return setting;
        }

        var target = (availability.Range ?? capability.Range)?.Unit ?? ControlUnits.None;
        return ControlUnitAdapter.Convert(number, setting.Unit, target) is { } converted
            ? setting with { Number = converted, Unit = target }
            : null;
    }

    private Backend? FindBackend(ControlObject controlObject, ControlCapability capability)
    {
        foreach (var writer in writers)
        {
            var availability = writer.Probe(controlObject, capability);
            if (availability.IsMine)
            {
                return new Backend(writer, availability);
            }
        }
        return null;
    }

    private static ControlApplyOutcome Outcome(
        ControlObject controlObject,
        ControlSetting setting,
        string status,
        string reason)
        => new(controlObject.Id, setting.CapabilityId, status, reason);

    private readonly record struct Backend(
        IControlWriter Writer,
        ControlWriteAvailability Availability);
}
