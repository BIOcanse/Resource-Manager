using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 统一写入层。**只管写。**
///
/// 一条设定从状态机到硬件要过这几关，全部在这里：
///
/// <list type="number">
/// <item>**认实例** —— 这个对象现在还在不在这台机器上。</item>
/// <item>**看档位** —— 用户现在这一档够不够得着这一项。</item>
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
    IControlAccessLevel accessLevel,
    ILogger<ControlWriteLayer>? logger = null) : IControlWriteLayer
{
    private const string ObjectMissing = "这个设备现在不在了，设定暂时生效不了。";
    private const string CapabilityMissing = "这个设备上没有这一项。";
    private const string WriterMissing = "只读，写入未接入。";

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

    public async Task<ControlApplyReport> ReleaseAsync(
        ControlDesiredState removed, ControlDesiredState remaining, CancellationToken cancellationToken)
    {
        if (removed.Objects.Count == 0) return ControlApplyReport.Empty;
        var objects = catalog.ReadObjects().Objects.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var outcomes = new List<ControlApplyOutcome>();
        foreach (var removedObject in removed.Objects)
        {
            var retained = remaining.Objects.FirstOrDefault(entry => entry.ObjectId == removedObject.ObjectId)?
                .Settings.Select(setting => setting.CapabilityId).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var setting in removedObject.Settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restored = false;
                try
                {
                    if (objects.TryGetValue(removedObject.ObjectId, out var target)
                        && target.Capabilities.FirstOrDefault(entry => entry.Id == setting.CapabilityId) is { } capability
                        && FindBackend(target, capability) is { } backend)
                    {
                        restored = await backend.Writer.RestoreAsync(target,
                            capability with { Range = backend.Availability.Range ?? capability.Range },
                            retained, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger?.LogWarning(error, "Control release failed: {Capability}.", setting.CapabilityId);
                }
                outcomes.Add(new ControlApplyOutcome(removedObject.ObjectId, setting.CapabilityId,
                    restored ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,
                    restored ? null : "未能释放此项控制。"));
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

        // 二、看档位。
        //
        // **这不是一道安全闸。** 程序本来就要管理员才起得来，愿意的人两下就切到最高档，
        // 挡不住谁，也不打算挡。它是防手滑的，顺带告诉用户哪些项算危险。
        //
        // 那为什么这里还要判一次：界面上那一项是灰的、勾选框勾不上，
        // 可"这一项归哪一档"是这条数据自己的属性，不是画面的属性。
        // 只在画面上判的话，走接口、走预设、走导入的配置就各有各的答案了。
        if (!ControlAccessLevels.Allows(accessLevel.Current, capability.RequiredAccessLevel))
        {
            return Outcome(
                controlObject,
                setting,
                ControlApplyStatuses.Unsupported,
                $"这一项需要切换到{ControlAccessLevels.DisplayName(capability.RequiredAccessLevel)}档才能调。");
        }

        // 三、选后端。
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

        // 四、适配单位。换不了就如实失败，不静默照着数字写下去。
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

        // 五、边界。**这是唯一一道边界闸**：范围由目录统一给出，这里只拦范围外的值。
        // 先前是各个写入器各夹各的，有的干脆没夹 —— 同一项从不同入口进来结论就不一样。
        if (OutOfRange(adapted, capability with { Range = backend.Availability.Range ?? capability.Range }) is { } rejected)
        {
            return Outcome(controlObject, setting, ControlApplyStatuses.Failed, rejected);
        }

        // 六、下发。七、归一由后端交回原始结果、这里统一成 ControlApplyOutcome。
        return await backend.Writer
            .WriteAsync(controlObject, capability, adapted, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 这个值越界了吗。越界就返回该说的那句话，没越界返回 null。
    ///
    /// **越界是拒绝，不是悄悄夹回去。** 夹回去等于把用户要的 300 MHz 写成 0，
    /// 然后回一句"已应用" —— 那是改掉他的意图还不告诉他。
    /// 界面上的滑块用的就是这同一个范围，所以正常操作根本到不了这里。
    /// </summary>
    private static string? OutOfRange(ControlSetting setting, ControlCapability capability)
    {
        if (setting.Number is not { } number || capability.Range is not { } range)
        {
            return null;
        }

        // 浮点和步进的零头不算越界。
        const double Tolerance = 1e-6;
        if (number >= range.Minimum - Tolerance && number <= range.Maximum + Tolerance)
        {
            return null;
        }

        return $"这一项现在只能设在 {Format(range.Minimum)} 到 {Format(range.Maximum)} {range.Unit}。";
    }

    private static string Format(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
