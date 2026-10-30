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
internal sealed class UniwillEcBridge : IDisposable
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
    private static readonly int AgreeingReads = HardwareControlSettings.ProductDefaults.UniwillEcChannel.AgreeingReads;
    private static readonly int ReadAttempts = HardwareControlSettings.ProductDefaults.UniwillEcChannel.ReadAttempts;

    private readonly ILogger<UniwillEcBridge>? logger;
    private readonly Func<IUniwillEcWmiSession?> openSession;
    private readonly object operationGate = new();
    private readonly object gate = new();
    private readonly EcChannelLock channel = new();
    private bool probed;
    private bool disposed;
    private IUniwillEcWmiSession? device;

    internal UniwillEcBridge(ILogger<UniwillEcBridge>? logger = null)
        : this(OpenWmiSession, logger) { }

    internal UniwillEcBridge(Func<IUniwillEcWmiSession?> openSession, ILogger<UniwillEcBridge>? logger = null)
    {
        this.openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        this.logger = logger;
    }

    /// <summary>这台机器有没有这条通道。</summary>
    internal bool IsAvailable
    {
        get
        {
            lock (operationGate)
            {
                ThrowIfDisposed();
                return ResolveDevice() is not null;
            }
        }
    }

    /// <summary>
    /// 读一个 EC 字节。读到连续一致的值才返回，否则返回 null ——
    /// 单次读数不可信，见 <see cref="AgreeingReads"/>。
    /// </summary>
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    internal byte? Read(ushort address)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            return channel.Hold(() => ReadHeld(address), out var value) ? value : null;
        }
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
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
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    internal bool Write(ushort address, byte value)
        => WriteReadingBack(address, value) == value;

    /// <summary>
    /// 写一个 EC 字节，返回随后观测到的寄存器值；通道失败返回 null。
    /// 回读不同不能区分固件限幅、写入未生效或其他写入者覆盖。
    /// </summary>
    // Serializes our cooperating clients, not the OEM service or firmware.
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    internal byte? WriteReadingBack(ushort address, byte value)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            return channel.Hold(() => WriteHeld(address, value), out var readBack) ? readBack : null;
        }
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private byte? WriteHeld(ushort address, byte value)
    {
        lock (gate)
        {
            var argument = (ulong)address | ((ulong)value << 16);
            if (Invoke(argument | (FunctionWrite << FunctionBitShift)) is null)
            {
                return null;
            }
        }

        var readBack = ReadHeld(address);
        if (readBack != value)
        {
            logger?.LogWarning(
                "EC 写入没有原样生效：地址 0x{Address:X4} 写 {Value}，回读 {ReadBack}。",
                address,
                value,
                readBack?.ToString() ?? "读不到");
        }
        return readBack;
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private uint? Invoke(ulong data)
    {
        if (ResolveDevice() is not { } target)
        {
            return null;
        }

        try
        {
            if (target.Invoke(data) is not { } returned || returned == CallFailed)
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
                InvalidateDevice();
            }
            return null;
        }
        catch (UnauthorizedAccessException error)
        {
            logger?.LogWarning(error, "没有权限访问 Uniwill EC 接口，需要管理员。");
            return null;
        }
    }

    [ResourceManager.ReadWrite.RuntimeReadCache]
    private IUniwillEcWmiSession? ResolveDevice()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (probed)
            {
                return device;
            }

            probed = true;
            try
            {
                device = openSession();
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

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static IUniwillEcWmiSession? OpenWmiSession()
    {
        using var searcher = new ManagementObjectSearcher(
            Scope,
            $"SELECT * FROM {ClassName} WHERE InstanceName = '{InstanceName.Replace(@"\", @"\\")}'");
        using var collection = searcher.Get();
        ManagementObject? selected = null;
        try
        {
            foreach (ManagementBaseObject found in collection)
            {
                if (selected is null && found is ManagementObject target) selected = target;
                else found.Dispose();
            }
            return selected is null ? null : new WmiSession(selected);
        }
        catch
        {
            selected?.Dispose();
            throw;
        }
    }

    private void InvalidateDevice()
    {
        device?.Dispose();
        device = null;
        probed = false;
    }

    public void Dispose()
    {
        lock (operationGate)
        {
            if (disposed) return;
            lock (gate)
            {
                disposed = true;
                device?.Dispose();
                device = null;
            }
            channel.Dispose();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    internal sealed class WmiSession(ManagementObject target, Func<ManagementBaseObject?>? readParameters = null) : IUniwillEcWmiSession
    {
        private ManagementBaseObject? parameters;
        private bool parametersRead;
        private bool disposed;

        [ResourceManager.ReadWrite.RuntimeReadTransport]
        public uint? Invoke(ulong data)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            parameters = ResolveParameters();
            if (parameters is null) return null;
            parameters["Data"] = data;
            using var result = target.InvokeMethod(MethodName, parameters, null);
            return result?["Return"] is uint returned ? returned : null;
        }

        [ResourceManager.ReadWrite.RuntimeReadCache]
        internal ManagementBaseObject? ResolveParameters()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!parametersRead)
            {
                parametersRead = true;
                parameters = ReadParameters();
            }
            return parameters;
        }

        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
        private ManagementBaseObject? ReadParameters()
            => readParameters is null ? target.GetMethodParameters(MethodName) : readParameters();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var ownedParameters = parameters;
            parameters = null;
            try { ownedParameters?.Dispose(); }
            finally { target.Dispose(); }
        }
    }
}

internal interface IUniwillEcWmiSession : IDisposable
{
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    uint? Invoke(ulong data);
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
    internal const ushort SystemId = 0x0456;

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
