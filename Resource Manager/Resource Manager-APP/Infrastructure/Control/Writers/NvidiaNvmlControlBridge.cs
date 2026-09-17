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

    /// <summary>这块卡的功耗上限能设到哪儿、现在是多少、出厂默认是多少。单位瓦。</summary>
    internal NvidiaPowerLimitWatts? ReadPowerLimit(IntPtr device)
    {
        lock (gate)
        {
            if (!EnsureInitialized())
            {
                return null;
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
                return NativeMethods.nvmlDeviceSetPowerManagementLimitV2(device, ref scoped);
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
