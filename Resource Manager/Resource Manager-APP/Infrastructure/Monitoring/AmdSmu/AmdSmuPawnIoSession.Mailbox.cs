namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

/// <summary>
/// SMU 邮箱：除了读 PM table，这个模块还能读写 SMU 寄存器、发 SMU 命令。
///
/// 写入能力放在这里而不是另开一个会话，是因为**打开这个模块的就是这个类** ——
/// PawnIO 模块、PCI 互斥、句柄生命周期都由它拥有，再开一份只会多一个属主。
///
/// PawnIO 模块放行 <c>0x3B10000–0x3B10FFF</c> 整段寄存器，两个 SMU 邮箱
/// （MP1 和 PSMU）都落在里面，所以两条都够得着。
/// </summary>
internal sealed partial class AmdSmuPawnIoSession
{
    /// <summary>SMU 命令最多带几个参数。和模块里的 <c>SMU_REQ_MAX_ARGS</c> 一致。</summary>
    internal const int SmuArgumentCount = 6;

    /// <summary>
    /// 走模块自带的命令通道发一条 SMU 命令。
    ///
    /// 这条通道用的是模块自己解析出来的那个邮箱（PM table 走的也是它，
    /// 在 Raphael / Dragon Range 上就是 PSMU）。要发到另一个邮箱，
    /// 得用 <see cref="SendMailboxCommand"/> 自己驱动。
    /// </summary>
    internal ulong[] SendSmuCommand(uint message, ReadOnlySpan<ulong> arguments)
    {
        var input = new ulong[1 + SmuArgumentCount];
        input[0] = message;
        for (var index = 0; index < SmuArgumentCount && index < arguments.Length; index++)
        {
            input[index + 1] = arguments[index];
        }
        return WithPciLock(() => executor.Execute(
            "ioctl_send_smu_command",
            input,
            SmuArgumentCount));
    }

    internal uint ReadSmuRegister(uint address)
    {
        var result = WithPciLock(() => executor.Execute(
            "ioctl_read_smu_register",
            [address],
            1));
        return result.Length == 0 ? 0 : (uint)result[0];
    }

    internal void WriteSmuRegister(uint address, uint value)
    {
        WithPciLock(() => executor.Execute(
            "ioctl_write_smu_register",
            [address, value],
            0));
    }

    /// <summary>
    /// 自己驱动一个指定的 SMU 邮箱，按 AMD 的握手顺序来：
    /// 先等上一条做完、清回执、写参数、写命令，再等回执。
    ///
    /// 顺序照 RyzenAdj 的 <c>smu_service_req</c>（LGPL）。清回执这一步不能省 ——
    /// 不清的话会把上一条命令的结果当成这一条的，看起来永远"成功"。
    /// </summary>
    /// <returns>SMU 的回执码，1 表示成功。</returns>
    internal uint SendMailboxCommand(
        AmdSmuMailbox mailbox,
        uint message,
        ReadOnlySpan<ulong> arguments)
    {
        var argumentValues = new uint[SmuArgumentCount];
        for (var index = 0; index < SmuArgumentCount && index < arguments.Length; index++)
        {
            argumentValues[index] = (uint)arguments[index];
        }

        return WithPciLock(() =>
        {
            if (!WaitForResponse(mailbox, out _))
            {
                return SmuResponseTimeout;
            }

            executor.Execute("ioctl_write_smu_register", [mailbox.ResponseAddress, 0], 0);
            for (var index = 0; index < SmuArgumentCount; index++)
            {
                executor.Execute(
                    "ioctl_write_smu_register",
                    [mailbox.ArgumentBaseAddress + ((uint)index * 4), argumentValues[index]],
                    0);
            }
            executor.Execute("ioctl_write_smu_register", [mailbox.MessageAddress, message], 0);

            return WaitForResponse(mailbox, out var response) ? response : SmuResponseTimeout;
        });
    }

    /// <summary>SMU 说这条命令成功了。</summary>
    internal const uint SmuResponseOk = 1;

    /// <summary>等回执超时。不是 SMU 给的码，是我们自己的。</summary>
    internal const uint SmuResponseTimeout = 0;

    private const int SmuResponseAttempts = 100;
    private const int SmuResponseDelayMilliseconds = 10;

    private bool WaitForResponse(AmdSmuMailbox mailbox, out uint response)
    {
        for (var attempt = 0; attempt < SmuResponseAttempts; attempt++)
        {
            var read = executor.Execute("ioctl_read_smu_register", [mailbox.ResponseAddress], 1);
            response = read.Length == 0 ? 0 : (uint)read[0];
            // 0 表示 SMU 还在忙。等它给出一个确定的回执再往下走。
            if (response != 0)
            {
                return true;
            }
            Thread.Sleep(SmuResponseDelayMilliseconds);
        }
        response = 0;
        return false;
    }
}

/// <summary>
/// 一个 SMU 邮箱的三个寄存器地址。
///
/// 地址随处理器代号而不同，取自 RyzenAdj 的 <c>nb_smu_ops.c</c>（LGPL）。
/// </summary>
internal readonly record struct AmdSmuMailbox(
    uint MessageAddress,
    uint ResponseAddress,
    uint ArgumentBaseAddress)
{
    /// <summary>Dragon Range / Fire Range（Zen4 HX）的 MP1 邮箱。功耗上限走这条。</summary>
    internal static AmdSmuMailbox DragonRangeMp1 { get; } =
        new(0x3B10530, 0x3B1057C, 0x3B109C4);

    /// <summary>
    /// 其余代号（含 Raven / Picasso / Dali / Lucienne）的 MP1 邮箱。
    /// 核显频率那条命令走这里。
    /// </summary>
    internal static AmdSmuMailbox DefaultMp1 { get; } =
        new(0x3B10528, 0x3B10564, 0x3B10998);

    /// <summary>Dragon Range / Fire Range 的 PSMU 邮箱。PM table 和 Curve Optimizer 走这条。</summary>
    internal static AmdSmuMailbox DragonRangePsmu { get; } =
        new(0x3B10524, 0x3B10570, 0x3B10A40);
}
