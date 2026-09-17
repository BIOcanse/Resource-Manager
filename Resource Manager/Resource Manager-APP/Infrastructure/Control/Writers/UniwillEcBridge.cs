using System.Management;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// Uniwill（同方公版）笔记本的 EC RAM 通道。
///
/// **走的是厂商固件自己的 ACPI WMI 接口，不是端口 I/O，所以不需要任何内核驱动。**
/// 协议照 tuxedo-drivers 的 GPL 实现（<c>uniwill_wmi.c</c>）：
///
/// - GUID <c>ABBC0F6F-8EA1-11D1-00A0-C90629100000</c>，method id 4，instance 0。
/// - 入参是 40 字节缓冲：<c>bytes[0..3]</c> 是 arg，<c>bytes[5]</c> 是功能号（0 写 / 1 读）。
///   Windows 把这个方法映射成 <c>GetSetULong(UInt64)</c>，那个 UInt64 就是缓冲区的前 8 字节，
///   所以 <c>Data = arg | (功能号 &lt;&lt; 40)</c>。
/// - 读：arg 就是 16 位 EC 地址；写：arg = <c>数据 &lt;&lt; 16 | 地址</c>。
/// - 返回值的低字节是数据，<c>0xFEFEFEFE</c> 表示这次调用失败。
///
/// Windows 上这个 GUID 落在 <c>root\WMI</c> 的 <c>AcpiTest_MULong</c> ——
/// Uniwill 复用了微软 ACPI 示例 MOF 的 GUID，所以类名看着毫不相干。
/// 需要管理员权限，本程序本来就是提权运行的。
/// </summary>
internal sealed class UniwillEcBridge(ILogger<UniwillEcBridge>? logger = null)
{
    /// <summary>厂商 ACPI 设备。它不在，这台机器就不是 Uniwill 平台。</summary>
    internal const string AcpiDeviceId = "ACPI\\INOU0000";

    private const string Scope = @"root\WMI";
    private const string ClassName = "AcpiTest_MULong";
    private const string MethodName = "GetSetULong";
    private const string InstanceName = @"ACPI\PNP0C14\1_0";
    private const ulong FunctionRead = 1;
    private const ulong FunctionWrite = 0;
    private const int FunctionBitShift = 40;
    private const uint CallFailed = 0xFEFE_FEFE;

    /// <summary>
    /// 一次读取要重复几遍才作数。
    ///
    /// 直接读 EC 会偶发撞上固件自己的写入，返回一个假的 <c>0x00</c> ——
    /// 实测两轮采样里三十多个寄存器都出现过单次掉零又弹回。
    /// 所以读到一致才算读到，否则宁可报读不到。
    /// </summary>
    private const int AgreeingReads = 3;
    private const int ReadAttempts = 6;

    private readonly object gate = new();
    private readonly EcChannelLock channel = new();
    private bool probed;
    private ManagementObject? device;

    /// <summary>这台机器有没有这条通道。</summary>
    internal bool IsAvailable => ResolveDevice() is not null;

    /// <summary>
    /// 读一个 EC 字节。读到连续一致的值才返回，否则返回 null ——
    /// 单次读数不可信，见 <see cref="AgreeingReads"/>。
    /// </summary>
    internal byte? Read(ushort address)
        => channel.Hold(() => ReadHeld(address), out var value) ? value : null;

    private byte? ReadHeld(ushort address)
    {
        lock (gate)
        {
            var agreed = 0;
            byte? last = null;
            for (var attempt = 0; attempt < ReadAttempts; attempt++)
            {
                if (Invoke((ulong)address | (FunctionRead << FunctionBitShift)) is not { } value)
                {
                    return null;
                }
                var current = (byte)(value & 0xFF);
                if (last == current)
                {
                    agreed++;
                    if (agreed >= AgreeingReads - 1)
                    {
                        return current;
                    }
                }
                else
                {
                    agreed = 0;
                    last = current;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 写一个 EC 字节，然后**回读核对**。
    ///
    /// 固件对这个接口从不报错 —— 地址不认、值越界都一样返回成功，
    /// 所以"写成功"只能由回读来定义。核对不上就如实说没写进去。
    /// </summary>
    internal bool Write(ushort address, byte value)
        // 写和回读之间不能松手：松手了回读到的可能是别人写进去的。
        => channel.Hold(() => WriteHeld(address, value), out var written) && written;

    private bool WriteHeld(ushort address, byte value)
    {
        lock (gate)
        {
            var argument = (ulong)address | ((ulong)value << 16);
            if (Invoke(argument | (FunctionWrite << FunctionBitShift)) is null)
            {
                return false;
            }
        }

        var readBack = ReadHeld(address);
        if (readBack == value)
        {
            return true;
        }

        logger?.LogWarning(
            "EC 写入没有生效：地址 0x{Address:X4} 写 {Value}，回读 {ReadBack}。",
            address,
            value,
            readBack?.ToString() ?? "读不到");
        return false;
    }

    private uint? Invoke(ulong data)
    {
        if (ResolveDevice() is not { } target)
        {
            return null;
        }

        try
        {
            using var parameters = target.GetMethodParameters(MethodName);
            parameters["Data"] = data;
            using var result = target.InvokeMethod(MethodName, parameters, null);
            if (result?["Return"] is not uint returned || returned == CallFailed)
            {
                return null;
            }
            return returned;
        }
        catch (ManagementException error)
        {
            logger?.LogWarning(error, "调用 Uniwill EC 接口失败。");
            // 接口可能是刚刚消失的（驱动被停用），下次重新解析。
            lock (gate)
            {
                device?.Dispose();
                device = null;
                probed = false;
            }
            return null;
        }
        catch (UnauthorizedAccessException error)
        {
            logger?.LogWarning(error, "没有权限访问 Uniwill EC 接口，需要管理员。");
            return null;
        }
    }

    private ManagementObject? ResolveDevice()
    {
        lock (gate)
        {
            if (probed)
            {
                return device;
            }

            probed = true;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    Scope,
                    $"SELECT * FROM {ClassName} WHERE InstanceName = '{InstanceName.Replace(@"\", @"\\")}'");
                foreach (var found in searcher.Get())
                {
                    device = (ManagementObject)found;
                    return device;
                }
            }
            catch (ManagementException error)
            {
                logger?.LogInformation(error, "这台机器上没有 Uniwill EC 接口。");
            }
            catch (UnauthorizedAccessException error)
            {
                logger?.LogWarning(error, "没有权限枚举 Uniwill EC 接口，需要管理员。");
            }
            return device;
        }
    }
}

/// <summary>
/// Uniwill EC RAM 里我们用到的地址，照 tuxedo-drivers 的 <c>uniwill_interfaces.h</c>。
///
/// 这些地址在 0x0700 之上，属于扩展 EC RAM —— 标准的 256 字节 EC 空间里看不到它们，
/// 只有走上面那条 WMI 通道才读得到。
/// </summary>
internal static class UniwillEcRegisters
{
    internal const ushort BareboneId = 0x0740;

    /// <summary>cTGP / Dynamic Boost 的使能位。</summary>
    internal const ushort CtgpDbEnable = 0x0743;
    internal const byte CtgpDbEnableGeneralBit = 0x01;
    internal const byte CtgpDbEnableDynamicBoostBit = 0x02;
    internal const byte CtgpDbEnableCtgpBit = 0x04;

    /// <summary>cTGP 偏移，单位瓦，加在显卡的基础 TGP 之上。</summary>
    internal const ushort CtgpOffset = 0x0744;
    internal const ushort TppOffset = 0x0745;
    /// <summary>Dynamic Boost 偏移，单位瓦。</summary>
    internal const ushort DynamicBoostOffset = 0x0746;

    internal const ushort PerformanceProfile = 0x0751;

    internal const ushort FanControlStatus = 0x078E;
    internal const byte FanControlStatusHasUniwillFanControlBit = 0x40;
}
