using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// NVML 的写入侧：显卡功耗上限。
///
/// 为什么功耗走 NVML 而频率偏移走 NVAPI：这块卡的 NVAPI 功耗策略表是空的
/// （四种结构体版本都返回成功、内容全零），而 NVML 报得出完整的
/// 「当前 / 默认 / 最小 / 最大」四个值 —— 也就是厂商把这一项开在 NVML 这条路上。
/// 两条路各管各的，不互相兜底：兜底会让"到底哪条生效了"说不清。
///
/// 设备身份沿用 <see cref="WindowsGpuProviderIdentityResolver"/>，
/// 和读取侧同一套 —— 同一块卡在监控页、控制页和这里必须是同一个东西。
/// </summary>
internal sealed class NvidiaNvmlControlBridge
{
    private const int NvmlSuccess = 0;
    /// <summary>NVML 自己的"还没初始化"状态码。库根本加载不上时也用它。</summary>
    private const int NvmlUninitialized = 1;
    private const uint NvmlPowerScopeGpu = 0;
    /// <summary>
    /// NVML 的结构体版本号是 <c>大小 | 版本 &lt;&lt; 24</c>。
    /// nvmlPowerValue_v2_t 是三个 4 字节字段：version、作用域、毫瓦。
    /// </summary>
    private const uint PowerValueV2Version = 12 | (2u << 24);

    private static readonly TimeSpan HandleMapLifetime = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly WindowsGpuAdapterOrderReader adapterReader = new();
    private bool initAttempted;
    private bool initialized;
    private Dictionary<int, IntPtr>? handleMap;
    private DateTime handleMapReadAt;

    /// <summary>找出这个显示适配器序号对应的 NVML 句柄。缓存很短，理由同 NVAPI 那边。</summary>
    internal IntPtr? FindHandleByAdapterIndex(int adapterIndex)
    {
        lock (gate)
        {
            return ReadHandleMap().TryGetValue(adapterIndex, out var handle) ? handle : null;
        }
    }

    /// <summary>
    /// 这块卡的出厂唯一标识（GPU UUID）。
    ///
    /// **这是真正的唯一 id**，不随槽位、驱动版本、系统重装而变 ——
    /// 配置挂在它上面，卡换个位置插回去，原来的设定还在。
    /// 读不到就返回 null，那时身份退到型号。
    /// </summary>
    internal string? ReadUniqueId(IntPtr device)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            try
            {
                var buffer = new byte[UuidBufferLength];
                if (NativeMethods.nvmlDeviceGetUUID(device, buffer, (uint)buffer.Length)
                    != NvmlSuccess)
                {
                    return null;
                }
                var text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0').Trim();
                return text.Length == 0 ? null : text;
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>NVML 的 UUID 字符串长度上限，官方头文件给的是 96。</summary>
    private const int UuidBufferLength = 96;

    /// <summary>
    /// 走 NVML 的字段接口一次问齐四个值。四个里少一个就整份放弃 ——
    /// 半份范围比没有更糟：界面会拿一个编出来的上界去画滑块。
    /// </summary>
    private static NvidiaPowerLimitWatts? ReadPowerLimitFields(IntPtr device)
    {
        // 字段号取自 nvml.h。作用域 0 是整块 GPU（还有显存、整模组两种，这里不用）。
        const uint MinimumLimitField = 187;
        const uint MaximumLimitField = 188;
        const uint DefaultLimitField = 189;
        const uint CurrentLimitField = 190;

        var values = new[]
        {
            new NativeMethods.NvmlFieldValue { FieldId = MinimumLimitField },
            new NativeMethods.NvmlFieldValue { FieldId = MaximumLimitField },
            new NativeMethods.NvmlFieldValue { FieldId = DefaultLimitField },
            new NativeMethods.NvmlFieldValue { FieldId = CurrentLimitField }
        };

        try
        {
            if (NativeMethods.nvmlDeviceGetFieldValues(device, values.Length, values)
                != NvmlSuccess)
            {
                return null;
            }
        }
        catch (Exception error)
            when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老驱动没有这个接口，退回旧的那几个。
            return null;
        }

        var milliwatts = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (FieldMilliwatts(values[index]) is not { } value)
            {
                return null;
            }
            milliwatts[index] = value;
        }

        if (milliwatts[1] <= milliwatts[0])
        {
            return null;
        }

        return new NvidiaPowerLimitWatts(
            milliwatts[0] / 1000.0,
            milliwatts[1] / 1000.0,
            milliwatts[2] / 1000.0,
            milliwatts[3] / 1000.0);
    }

    /// <summary>
    /// 一个字段的毫瓦值。这一项本身失败、或者报回来的量纲不是我们认识的整数类型，
    /// 都返回 null —— **不按"大概是这个意思"去解那 8 个字节**。
    /// </summary>
    private static double? FieldMilliwatts(NativeMethods.NvmlFieldValue value)
    {
        if (value.NvmlReturn != NvmlSuccess)
        {
            return null;
        }
        // nvmlValueType_t：1 = unsigned int，2 = unsigned long，3 = unsigned long long。
        // 功耗字段文档写的是 unsigned int 毫瓦，另外两种一并收下，其余不猜。
        return value.ValueType is 1 or 2 or 3 ? value.Value : null;
    }

    /// <summary>
    /// 这块卡的功耗上限能设到哪儿、现在是多少、出厂默认是多少。单位瓦。
    ///
    /// **先问字段接口，再退回旧接口。**
    /// 本机实测（RTX 5060 Laptop，驱动 610.88）：
    /// <c>nvmlDeviceGetPowerManagementLimit</c> 直接返回 Not Supported，
    /// 而字段接口把四个值全给得出来。NVML 把功耗上限挪进了带作用域的字段接口
    /// （nvidia-smi 里那个 "GPU Ceiling Power Limit" 就是它），
    /// 旧的那几个在新卡上逐渐没了。
    ///
    /// 先前只有旧接口这一条路，读不到就报"读不到当前功耗上限，无法确认能否改" ——
    /// 用户看到的是"这张卡不给读"，其实是我们问错了地方。
    /// </summary>
    internal NvidiaPowerLimitWatts? ReadPowerLimit(IntPtr device)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
            }

            if (ReadPowerLimitFields(device) is { } fromFields)
            {
                return fromFields;
            }

            try
            {
                if (NativeMethods.nvmlDeviceGetPowerManagementLimitConstraints(
                        device,
                        out var minimumMilliwatts,
                        out var maximumMilliwatts) != NvmlSuccess
                    || maximumMilliwatts <= minimumMilliwatts)
                {
                    return null;
                }

                var current = NativeMethods.nvmlDeviceGetPowerManagementLimit(
                    device,
                    out var currentMilliwatts) == NvmlSuccess
                        ? currentMilliwatts / 1000.0
                        : (double?)null;
                var standard = NativeMethods.nvmlDeviceGetPowerManagementDefaultLimit(
                    device,
                    out var defaultMilliwatts) == NvmlSuccess
                        ? defaultMilliwatts / 1000.0
                        : (double?)null;

                return new NvidiaPowerLimitWatts(
                    minimumMilliwatts / 1000.0,
                    maximumMilliwatts / 1000.0,
                    // 读不到默认值就退到当前值：它至少是这块卡真实待过的一个档位，
                    // 而凭空造一个"默认"会让界面上的复位键把卡带到从没设过的地方。
                    standard ?? current,
                    current);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 把功耗上限写下去，单位瓦。需要管理员权限，本程序本来就是提权运行的。
    ///
    /// 新驱动把功耗上限分成了几个「作用域」（整卡 / 模块 / 显存），旧的单参数接口
    /// 会以"当前作用域不支持"为由拒绝 —— 所以优先用带作用域的 v2 接口，
    /// 明确指定要改的是整卡那一个；v2 不在（老驱动）才退回旧接口。
    /// 返回驱动给的原始状态码，0 是成功，其余原样往上报，不改写成一句"失败"。
    /// </summary>
    /// <summary>
    /// 温度阈值的种类，照 nvml.h 的 <c>nvmlTemperatureThresholds_t</c>。
    ///
    /// 只列我们用得上的三个。本机实测另外四个（显存上限、三个噪声相关的）
    /// 这块卡直接报不支持 —— 列出来也只会得到一排永远不可用的项。
    /// </summary>
    internal static class TemperatureThresholds
    {
        /// <summary>撞上就断电保护。**最后一道防线。**</summary>
        internal const uint Shutdown = 0;
        /// <summary>撞上就开始强制降频。</summary>
        internal const uint Slowdown = 1;
        /// <summary>工作温度上限，也就是通常说的"温度墙"。</summary>
        internal const uint GpuMaximum = 3;
    }

    /// <summary>
    /// 读一个温度阈值。这块卡不支持这一种就返回 null。
    ///
    /// **返回的是绝对温度。** 新驱动的 nvidia-smi 会用 T.Limit 口径把它显示成
    /// 相对值（"离墙还有 40 度"），但 NVML 这个接口给的仍然是绝对度数 ——
    /// 本机实测 105 / 102 / 89。照显示口径去理解会差出一整个量级。
    /// </summary>
    internal double? ReadTemperatureThreshold(IntPtr device, uint thresholdType)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            try
            {
                return NativeMethods.nvmlDeviceGetTemperatureThreshold(
                    device,
                    thresholdType,
                    out var celsius) == NvmlSuccess
                        ? celsius
                        : null;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                // 老驱动上没有这个入口。不是错误，就是没有。
                return null;
            }
        }
    }

    /// <summary>
    /// 写一个温度阈值。返回 NVML 的原始返回码，0 是成功。
    ///
    /// **不在这里判断"应不应该写"** —— 那是上面那层的事（档位、范围、
    /// 用户有没有勾选）。这里只负责把它交给驱动，并如实把驱动的回答带回去。
    /// </summary>
    internal int WriteTemperatureThreshold(IntPtr device, uint thresholdType, int celsius)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return -1;
            }
            try
            {
                var value = celsius;
                return NativeMethods.nvmlDeviceSetTemperatureThreshold(
                    device,
                    thresholdType,
                    ref value);
            }
            catch (DllNotFoundException)
            {
                return -1;
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
        }
    }

    /// <summary>时钟域，照 nvml.h 的 <c>nvmlClockType_t</c>。</summary>
    internal static class ClockTypes
    {
        internal const uint Graphics = 0;
        internal const uint Memory = 2;
    }

    /// <summary>这块卡这个时钟域最高能到多少 MHz。读不到就是 null。</summary>
    internal double? ReadMaximumClockMhz(IntPtr device, uint clockType)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            try
            {
                return NativeMethods.nvmlDeviceGetMaxClockInfo(device, clockType, out var mhz)
                    == NvmlSuccess
                        ? mhz
                        : null;
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 把某个时钟域锁进一个区间。返回 NVML 的原始返回码，0 是成功。
    ///
    /// **下限给 0 就是不设下限** —— 本机实测驱动接受 <c>(0, 最大值)</c>，
    /// 那等于没有任何限制。所以"只设上限"是表达得出来的，不必编一个最低频率。
    /// </summary>
    internal int WriteLockedClocks(IntPtr device, uint clockType, uint minimumMhz, uint maximumMhz)
        => Invoke(() => clockType == ClockTypes.Memory
            ? NativeMethods.nvmlDeviceSetMemoryLockedClocks(device, minimumMhz, maximumMhz)
            : NativeMethods.nvmlDeviceSetGpuLockedClocks(device, minimumMhz, maximumMhz));

    /// <summary>解开某个时钟域的锁，交还驱动自己调度。</summary>
    internal int ResetLockedClocks(IntPtr device, uint clockType)
        => Invoke(() => clockType == ClockTypes.Memory
            ? NativeMethods.nvmlDeviceResetMemoryLockedClocks(device)
            : NativeMethods.nvmlDeviceResetGpuLockedClocks(device));

    private int Invoke(Func<int> call)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return -1;
            }
            try
            {
                return call();
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return -1;
            }
        }
    }

    internal int WritePowerLimitWatts(IntPtr device, double watts)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return NvmlUninitialized;
            }

            var milliwatts = (uint)Math.Round(watts * 1000.0);
            try
            {
                var scoped = new NvmlPowerValue
                {
                    Version = PowerValueV2Version,
                    PowerScope = NvmlPowerScopeGpu,
                    PowerValueMilliwatts = milliwatts
                };
                var v2 = NativeMethods.nvmlDeviceSetPowerManagementLimitV2(device, ref scoped);
                if (v2 == NvmlSuccess)
                {
                    return v2;
                }
                /*
                 * **v2 失败也要试旧接口，不只是"找不到入口"时才试。**
                 *
                 * 本机实测（RTX 5060 Laptop，驱动 610.88）：v2 这条路存在、能调用，
                 * 但这块卡上它报不支持；而旧接口是支持的。先前这里只在
                 * EntryPointNotFoundException 时才退回去 —— 入口明明在，
                 * 于是一个"这块卡不支持"就直接回给了用户，旧接口一次都没试过。
                 */
            }
            catch (EntryPointNotFoundException)
            {
                // 老驱动没有 v2，退回旧接口。
            }
            catch (DllNotFoundException)
            {
                return NvmlUninitialized;
            }

            try
            {
                return NativeMethods.nvmlDeviceSetPowerManagementLimit(device, milliwatts);
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException)
            {
                return NvmlUninitialized;
            }
        }
    }

    private IReadOnlyDictionary<int, IntPtr> ReadHandleMap()
    {
        if (handleMap is not null && DateTime.UtcNow - handleMapReadAt < HandleMapLifetime)
        {
            return handleMap;
        }

        handleMapReadAt = DateTime.UtcNow;
        handleMap = EnumerateHandles();
        return handleMap;
    }

    private Dictionary<int, IntPtr> EnumerateHandles()
    {
        var found = new Dictionary<int, IntPtr>();
        if (!EnsureInitialized())
        {
            return found;
        }

        try
        {
            if (NativeMethods.nvmlDeviceGetCount(out var count) != NvmlSuccess)
            {
                return found;
            }

            var inventory = adapterReader.ReadInventory();
            for (var index = 0u; index < count; index++)
            {
                if (NativeMethods.nvmlDeviceGetHandleByIndex(index, out var device) != NvmlSuccess
                    || NativeMethods.nvmlDeviceGetPciInfo(device, out var pciInfo) != NvmlSuccess
                    || !WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(
                        pciInfo,
                        out var evidence))
                {
                    continue;
                }

                var match = WindowsGpuProviderIdentityResolver.ResolvePci(inventory, evidence);
                if (match.IsCurrent)
                {
                    found[match.Binding.Adapter.Index] = device;
                }
            }
        }
        catch (DllNotFoundException)
        {
            return found;
        }
        catch (EntryPointNotFoundException)
        {
            return found;
        }
        return found;
    }

    private bool EnsureInitialized()
    {
        if (initAttempted)
        {
            return initialized;
        }

        initAttempted = true;
        try
        {
            initialized = NativeMethods.nvmlInit() == NvmlSuccess;
        }
        catch (Exception error) when (error is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            initialized = false;
        }
        return initialized;
    }

    private static class NativeMethods
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        internal static extern int nvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        internal static extern int nvmlDeviceGetCount(out uint deviceCount);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3", CharSet = CharSet.Ansi)]
        internal static extern int nvmlDeviceGetPciInfo(IntPtr device, out NvmlPciInfo pciInfo);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi)]
        internal static extern int nvmlDeviceGetUUID(
            IntPtr device,
            [Out] byte[] uuid,
            uint length);

        /// <summary>
        /// NVML 字段接口的一条记录。前两个字段是入参，其余是出参。
        /// 布局照 nvml.h 的 nvmlFieldValue_t，不能改顺序。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlFieldValue
        {
            public uint FieldId;
            /// <summary>作用域：0 = 整块 GPU。</summary>
            public uint ScopeId;
            public long Timestamp;
            public long LatencyUsec;
            public int ValueType;
            public int NvmlReturn;
            /// <summary>联合体，8 字节。按 ValueType 解释。</summary>
            public ulong Value;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFieldValues")]
        internal static extern int nvmlDeviceGetFieldValues(
            IntPtr device,
            int count,
            [In, Out] NvmlFieldValue[] values);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        internal static extern int nvmlDeviceGetPowerManagementLimit(
            IntPtr device,
            out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
        internal static extern int nvmlDeviceGetPowerManagementDefaultLimit(
            IntPtr device,
            out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
        internal static extern int nvmlDeviceGetPowerManagementLimitConstraints(
            IntPtr device,
            out uint minimumMilliwatts,
            out uint maximumMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit")]
        internal static extern int nvmlDeviceSetPowerManagementLimit(
            IntPtr device,
            uint limitMilliwatts);

        /*
         * 温度阈值。**读和写是两个不同形状的函数**：
         * 读按值出参，写要传指针进去 —— nvml.h 里就是这么定的，不能想当然对称。
         */
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperatureThreshold")]
        internal static extern int nvmlDeviceGetTemperatureThreshold(
            IntPtr device,
            uint thresholdType,
            out uint temperature);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetTemperatureThreshold")]
        internal static extern int nvmlDeviceSetTemperatureThreshold(
            IntPtr device,
            uint thresholdType,
            ref int temperature);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")]
        internal static extern int nvmlDeviceGetMaxClockInfo(
            IntPtr device,
            uint clockType,
            out uint clockMhz);

        /*
         * 时钟锁。**一次调用同时给上下限**，这是驱动那边的形状，不是我们的选择。
         * 解锁是另一个函数，不是"设成某个特殊值"。
         */
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetGpuLockedClocks")]
        internal static extern int nvmlDeviceSetGpuLockedClocks(
            IntPtr device,
            uint minimumMhz,
            uint maximumMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
        internal static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetMemoryLockedClocks")]
        internal static extern int nvmlDeviceSetMemoryLockedClocks(
            IntPtr device,
            uint minimumMhz,
            uint maximumMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetMemoryLockedClocks")]
        internal static extern int nvmlDeviceResetMemoryLockedClocks(IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit_v2")]
        internal static extern int nvmlDeviceSetPowerManagementLimitV2(
            IntPtr device,
            ref NvmlPowerValue powerValue);
    }
}

/// <summary>带作用域的功耗值，对应 NVML 的 nvmlPowerValue_v2_t。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlPowerValue
{
    internal uint Version;
    internal uint PowerScope;
    internal uint PowerValueMilliwatts;
}

/// <summary>这块卡的功耗上限能设到哪儿，单位瓦。</summary>
internal readonly record struct NvidiaPowerLimitWatts(
    double MinimumWatts,
    double MaximumWatts,
    double? DefaultWatts,
    double? CurrentWatts);
