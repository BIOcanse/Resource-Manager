using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 从已有的监控快照里认出可控对象。
///
/// 刻意不新开采集：哪几块卡、哪几个风扇，监控侧早就知道了。
/// 同一块卡在监控页和控制页必须是同一个东西，所以身份直接用监控侧的
/// <see cref="GpuMetrics.IdentityKey"/>，不另造编号。
///
/// **这一片只认对象、只声明能力的形状，不判断能不能写。**
/// 「这一项现在能不能调、为什么不能、范围到哪儿」由写入器回答 ——
/// 它才是真去碰硬件的那个。两边各存一份的话迟早对不上：
/// 目录说能调，写下去却报不支持。
/// </summary>
public sealed class WindowsControlObjectCatalog(
    DashboardMonitoringCatalogState catalogState,
    IEnumerable<IControlWriter> writers,
    IControlAccessLevel accessLevel,
    FanControlCoreClient fanCore,
    WindowsChassisKindReader chassis) : IControlObjectCatalog
{
    /// <summary>读显卡出厂唯一标识用的。读不到就退到型号，见 ControlInstanceIdentity。</summary>
    private readonly NvidiaNvmlControlBridge uniqueIdReader = new();

    /// <summary>处理器的身份。核显跟着它走 —— 核显没有自己的标识。</summary>
    private readonly WindowsCpuIdentityReader cpuIdentityReader = new();

    /// <summary>没有任何写入器认领这一项时的原因。接进一个就少一条。</summary>
    private const string WriterNotImplemented = "只读，写入未接入。";

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();

    public ControlObjectCatalog ReadObjects()
    {
        var snapshot = catalogState.Current;
        var objects = new List<ControlObject>();
        if (snapshot is not null)
        {
            // 核显没有独立标识，跟着处理器走，所以先把处理器的身份定下来。
            var cpuIdentity = cpuIdentityReader.Resolve(snapshot.Cpu.Name);
            var gpuIdentities = ResolveGpuIdentities(snapshot, cpuIdentity);
            AddGpus(objects, snapshot, gpuIdentities);
            AddCpu(objects, snapshot, cpuIdentity);
            AddFans(objects, snapshot);
        }
        return new ControlObjectCatalog(
            objects.Select(WithChassisTerm).Select(ResolveAvailability).ToArray(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 逐项问写入器：你能写吗？写不了为什么？范围是多少？
    ///
    /// 第一个认领这一项的写入器说了算。没人认领就保持目录里那句"还没接"。
    /// </summary>
    private ControlObject ResolveAvailability(ControlObject candidate)
    {
        var resolved = new List<ControlCapability>(candidate.Capabilities.Count);
        foreach (var capability in candidate.Capabilities)
        {
            resolved.Add(ResolveCapability(candidate, capability));
        }
        return candidate with { Capabilities = resolved };
    }

    internal ControlCapability ResolveCapability(
        ControlObject candidate,
        ControlCapability capability)
    {
        var reachable = ControlAccessLevels.Allows(
            accessLevel.Current,
            capability.RequiredAccessLevel);

        foreach (var writer in writers)
        {
            var availability = writer.Probe(candidate, capability);
            if (!availability.IsMine)
            {
                continue;
            }
            /*
             * **"这台机器做不到"压过"你档位不够"。**
             *
             * 先前这里是档位够不着就直接短路，连写入器都不问，理由是
             * "问了也只是白碰一次硬件，答案不会改变这一项的结论"。
             * 那个理由不成立：答案改变的是**给用户的那句话**。
             *
             * 显卡那三档温度阈值就是活例子 —— 它们需要普通/root 档，
             * 而这块卡的驱动**根本不接受改**。短路之后用户看到的是
             * "切到普通档就能调"，他切过去，发现还是不行，
             * 而我们其实一开始就知道。
             *
             * 所以先问写入器；只有在它没说"做不到"的时候，档位闸才说话。
             */
            if (!reachable && availability.CanWrite)
            {
                return Locked(candidate, capability, availability);
            }
            return capability with
            {
                Supported = availability.CanWrite,
                UnavailableReason = availability.CanWrite ? null : availability.Reason,
                // 写入器是真去问过硬件的那个，它说属于哪一类就是哪一类。
                UnavailableKind = availability.CanWrite
                    ? null
                    : availability.UnavailableKind ?? ControlUnavailableKinds.Platform,
                // 硬件报得出真实范围就用真实的；报不出就保留目录里的形状。
                Range = availability.Range ?? capability.Range,
                // 认领这一项的写入器说了算；它不说就保留目录声明的意向链路 ——
                // **没接不等于不知道该走哪条**，那句话用户也该看得见。
                Channel = writer.ChannelOf(candidate, capability.Id) ?? capability.Channel
            };
        }
        // Changing access cannot supply a missing writer.
        return capability;
    }

    /// <summary>
    /// 档位够不着时的那一份。
    ///
    /// **范围也带上**：写入器读到了真实范围就用真的。锁着不等于不知道它有多大，
    /// 而用户切档位之前多半想先看看能调到哪儿。
    /// </summary>
    private ControlCapability Locked(
        ControlObject candidate,
        ControlCapability capability,
        ControlWriteAvailability? availability)
        => capability with
        {
            Supported = false,
            UnavailableReason = NeedsAccessLevel(capability.RequiredAccessLevel),
            UnavailableKind = ControlUnavailableKinds.AccessLevel,
            Range = availability?.Range ?? capability.Range,
            // 锁着也要说清楚它走哪条 —— 用户最想知道链路的就是这些项。
            Channel = ChannelOf(candidate, capability.Id) ?? capability.Channel
        };

    /// <summary>
    /// 这一项归哪条链路。第一个认领的写入器说了算，没人认领就是还没有链路。
    /// </summary>
    private string? ChannelOf(ControlObject candidate, string capabilityId)
    {
        foreach (var writer in writers)
        {
            if (writer.ChannelOf(candidate, capabilityId) is { } channel)
            {
                return channel;
            }
        }
        return null;
    }

    /// <summary>
    /// 差一档时说的那句话。
    ///
    /// **说的是差哪一档，不是"不支持"。** 这一项在这台机器上是存在的，
    /// 只是用户现在这一档够不着；把它说成不支持，用户就去查硬件了。
    /// </summary>
    private static string NeedsAccessLevel(string required)
        => $"需要切换到{ControlAccessLevels.DisplayName(required)}档（设置 → 调节权限）。";

    /// <summary>
    /// 每块显卡的身份。独显优先用出厂唯一标识；核显没有自己的标识，跟处理器走。
    /// </summary>
    private Dictionary<int, string> ResolveGpuIdentities(
        HardwareMetricSnapshot snapshot,
        string cpuIdentity)
    {
        // 同型号出现过几次。只有读不到唯一 id 时才用得上 ——
        // 两个对象必须有两个不同的标识，否则目录里会撞车。
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var identities = new Dictionary<int, string>();
        foreach (var gpu in snapshot.Gpus)
        {
            if (GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(gpu.Name))
            {
                // 核显长在处理器封装里，没有自己的标识 —— 就用处理器那一个。
                // 前缀不同（gpu: 对 cpu:），所以两者仍然是两个实例，不会混。
                // 换了处理器，核显自然也跟着换，这正是想要的。
                identities[gpu.Index] = cpuIdentity;
                continue;
            }
            var uniqueId = uniqueIdReader.FindHandleByAdapterIndex(gpu.Index) is { } device
                ? uniqueIdReader.ReadUniqueId(device)
                : null;
            occurrences[gpu.Name] = occurrences.GetValueOrDefault(gpu.Name) + 1;
            identities[gpu.Index] = ControlInstanceIdentity.ForGpu(
                gpu,
                uniqueId,
                occurrences[gpu.Name]);
        }
        return identities;
    }

    private void AddGpus(
        List<ControlObject> objects,
        HardwareMetricSnapshot snapshot,
        IReadOnlyDictionary<int, string> gpuIdentities)
    {
        foreach (var gpu in snapshot.Gpus)
        {
            var vendor = VendorOf(gpu.Name);
            // 核显和独显的可调自由度差很多，所以要分开 —— 沿用性能分那边已有的判定，
            // 不另写一套型号名单。
            var attachment = GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(gpu.Name)
                ? ControlGpuAttachments.Integrated
                : ControlGpuAttachments.Discrete;
            var identity = gpuIdentities[gpu.Index];

            // 每块卡一个对象，各自带自己的 (系统, 厂商) —— 不是全局状态。
            objects.Add(new ControlObject(
                $"gpu:{identity}",
                ControlObjectKinds.Gpu,
                gpu.Name,
                new ControlObjectPlatform(ControlOperatingSystems.Windows, vendor),
                attachment == ControlGpuAttachments.Integrated
                    ? IntegratedGpuCapabilities(vendor)
                    : DiscreteGpuCapabilities(vendor, gpu, chassis.Read()),
                $"GPU{gpu.Index}",
                GpuAttachment: attachment,
                AdapterIndex: gpu.Index));
        }
    }

    /// <summary>
    /// 独显能动的那些点。
    ///
    /// **按链路分组列全，不按"我们接没接"来筛。** 三组动的东西完全不同：
    /// A 组动显卡本体（NVML / NVAPI），B 组动驱动的脾气（驱动 Profile），
    /// C 组动整机固件给显卡的功率预算 —— 这三层的能力、失败方式、掉电后还在不在
    /// 都不一样，合成一条"显卡超频"说，用户就分不清自己到底在改什么。
    ///
    /// 没接的照样列出来并标明它该走哪条链路：**"我们没接"和"这台机器做不到"是两件事**，
    /// 藏起来用户只会当成后者。
    ///
    /// 显卡风扇**不在这里**：它是独立的风扇对象，只在那边用词条标出它吹的是显卡。
    /// </summary>
    private static IReadOnlyList<ControlCapability> DiscreteGpuCapabilities(
        string vendor,
        GpuMetrics gpu,
        /*
         * 机箱形态。**独显能调什么和它有关**：cTGP / Dynamic Boost 是笔记本
         * 整机固件的概念，台式机上根本不存在；而 AMD 独显反过来只做台式。
         * 传进来而不是在这里读，是因为这个方法是静态的，也不该自己去碰硬件。
         */
        string chassisKind)
    {
        // 厂商决定这块卡要哪条写入路径。列出来是为了让用户看到差别，
        // 而不是笼统地说"不支持超频"。
        var (componentId, componentName) = vendor switch
        {
            ControlVendors.Nvidia => ("nvidia-nvapi-provider", "NVIDIA NVAPI Provider"),
            ControlVendors.Amd => ("amd-adlx-provider", "AMD ADLX / ADL Provider"),
            _ => ("msi-afterburner", "MSI Afterburner")
        };

        ControlCapability Card(
            string id,
            string label,
            string channel,
            ControlNumberRange? range = null,
            string level = ControlAccessLevels.Normal,
            string valueKind = ControlValueKinds.Number)
            => Unsupported(id, label, valueKind, componentId, componentName, range, level, channel);

        // 整机固件那条路上的项，缺的组件和显卡驱动不是同一个，所以单列。
        ControlCapability Platform(
            string id,
            string label,
            ControlNumberRange? range = null,
            string valueKind = ControlValueKinds.Number,
            bool laptopOnly = false)
            => laptopOnly && chassisKind == ControlChassisKinds.Fixed
                /*
                 * **台式机上这些项根本不存在。**
                 * cTGP、Dynamic Boost、平台总功耗都是笔记本整机固件给显卡分预算
                 * 的概念；台式机的显卡直接吃 PCIe 和供电线，没有"整机预算"这回事。
                 * 列成"还没接"会让用户一直等一个不存在的东西。
                 */
                ? Excluded(id, label, "这几项是笔记本整机固件的功率预算，台式机上没有。")
                : Unsupported(
                    id,
                    label,
                    valueKind,
                    "uniwill-ec-provider",
                    "整机固件接口",
                    range,
                    ControlAccessLevels.Normal,
                    ControlChannels.OemEmbeddedController);

        List<ControlCapability> items =
        [
            // ── A 组：显卡本体调谐 ─────────────────────────────
            Card("gpu.core-clock-offset", "核心频率偏移", ControlChannels.Nvapi,
                new ControlNumberRange(-500, 500, 5, ControlUnits.Megahertz, 0),
                ControlAccessLevels.Normal),
            Card("gpu.memory-clock-offset", "显存频率偏移", ControlChannels.Nvapi,
                new ControlNumberRange(-1000, 2000, 5, ControlUnits.Megahertz, 0),
                ControlAccessLevels.Normal),
            /*
             * 频率锁和频率偏移不是一回事：偏移是把整条 V/F 曲线挪一挪，
             * 锁是给频率加一个硬的上界或下界。降频省电、压温度最常用的是锁上界，
             * 它和"负偏移"的效果完全不同 —— 所以两者都要有。
             */
            Card("gpu.core-clock-minimum", "核心频率下限", ControlChannels.Nvml,
                new ControlNumberRange(0, 3000, 15, ControlUnits.Megahertz),
                ControlAccessLevels.Normal),
            Card("gpu.core-clock-maximum", "核心频率上限", ControlChannels.Nvml,
                new ControlNumberRange(0, 3000, 15, ControlUnits.Megahertz),
                ControlAccessLevels.Normal),
            Card("gpu.memory-clock-minimum", "显存频率下限", ControlChannels.Nvml,
                new ControlNumberRange(0, 12000, 50, ControlUnits.Megahertz),
                ControlAccessLevels.Normal),
            Card("gpu.memory-clock-maximum", "显存频率上限", ControlChannels.Nvml,
                new ControlNumberRange(0, 12000, 50, ControlUnits.Megahertz),
                ControlAccessLevels.Normal),
            /*
             * 电压偏移。往下调是降压，往上调是**给核心加压** ——
             * 后者是硬件损伤风险最直接的一项，所以整条放在 root 档。
             */
            Card("gpu.core-voltage-offset", "核心电压偏移", ControlChannels.Nvapi,
                new ControlNumberRange(-200, 100, 5, ControlUnits.Millivolt, 0),
                ControlAccessLevels.Root),
            Unsupported(
                "gpu.power-limit",
                "功耗上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                /*
                 * **这里不给范围。**
                 *
                 * 功耗上限的上下界是驱动里那道硬件锁（NVML 的
                 * nvmlDeviceGetPowerManagementLimitConstraints 直接报），
                 * 只有写入器读得到，所以由它给。
                 *
                 * 先前这里编了一个「额定值 × 1.2」当上限 —— 本机实测硬件锁是 115 W，
                 * 编出来的是 136 W。用户照着那个数去拖，拖到的是一段根本不存在的区间。
                 * 读不到就不给，不编。
                 */
                null,
                ControlAccessLevels.Normal,
                ControlChannels.Nvml),
            /*
             * 温度墙。**这是显卡的控制项，不是风扇的。**
             *
             * 它改的是显卡自己什么时候开始降频，和"风扇转多快"是两回事：
             * 风扇拉满也压不住时照样撞墙，把墙抬高也不会让风扇多转一转。
             * 放到风扇那边去，用户就再也找不到为什么锁在某个频率上。
             */
            Card("gpu.temperature-limit", "温度上限", ControlChannels.Nvml,
                new ControlNumberRange(60, 95, 1, ControlUnits.Celsius),
                ControlAccessLevels.Normal),
            Card("gpu.slowdown-temperature", "降速温度阈值", ControlChannels.Nvml,
                new ControlNumberRange(70, 105, 1, ControlUnits.Celsius),
                ControlAccessLevels.Normal),
            /*
             * 保护关机阈值。**这一条动的是最后一道保护本身**，
             * 和"把降频点抬高几度"不是一个量级 —— 撞到它本来就是硬件在自救。
             */
            Card("gpu.shutdown-temperature", "保护关机阈值", ControlChannels.Nvml,
                new ControlNumberRange(80, 120, 1, ControlUnits.Celsius),
                ControlAccessLevels.Root),

            // ── B 组：驱动策略 ────────────────────────────────
            /*
             * 这一组动的是**驱动的脾气**，不是硬件：它们写进驱动的 Profile，
             * 重启后还在，失败方式也和上面那些完全不同。
             * 所以链路要单独标出来，不能和 NVAPI 的调谐接口混成一条。
             */
            Card("gpu.power-management-mode", "电源管理模式",
                ControlChannels.NvapiDriverProfile,
                new ControlNumberRange(0, 2, 1, ControlUnits.None)),
            Card("gpu.frame-rate-limit", "帧率限制",
                ControlChannels.NvapiDriverProfile,
                // 0 表示不限。
                new ControlNumberRange(0, 480, 1, ControlUnits.FramesPerSecond, 0)),

            // ── C 组：整机功率预算 ────────────────────────────
            /*
             * 这几条**不是往显卡里写的**。
             *
             * 笔记本上显卡能吃多少瓦是整机固件说了算：EC 把预算报给 ACPI 里的表，
             * 驱动读到之后才决定 TGP —— 显卡只是接收方。所以它们和上面那条
             * 走 NVML 的「功耗上限」是**两层东西，必须并存**：
             * 一个是卡自己的上限，一个是整机愿意给它的预算，谁低谁生效。
             */
            Platform("gpu.ctgp-offset", "cTGP 偏移",
                new ControlNumberRange(0, 100, 1, ControlUnits.Watt, 0), laptopOnly: true),
            /*
             * Dynamic Boost 的开关和额度**是两条**：能不能用是固件放不放行，
             * 给多少瓦是放行之后的额度。合成一条就没法表达"能用但给 0 瓦"。
             */
            Platform("gpu.dynamic-boost-enabled", "Dynamic Boost 开关",
                valueKind: ControlValueKinds.Toggle, laptopOnly: true),
            Platform("gpu.dynamic-boost-offset", "Dynamic Boost 额度",
                new ControlNumberRange(0, 100, 1, ControlUnits.Watt, 0), laptopOnly: true)
        ];

        if (string.Equals(vendor, ControlVendors.Nvidia, StringComparison.Ordinal))
        {
            return items;
        }

        /*
         * **Intel 独显不做。** 这是范围上的裁决，不是"还没接"：
         * Arc 这条线上公开的可写接口不足以支撑这一页的任何一项。
         * 报成"还没接"会让用户一直等一个不会来的东西。
         */
        if (string.Equals(vendor, ControlVendors.Intel, StringComparison.Ordinal))
        {
            return [Excluded(
                "gpu.core-clock-offset",
                "核心频率偏移",
                "Intel 独显的调节不在本软件的范围内。")];
        }

        /*
         * **AMD 独显只做台式。** 笔记本上的 Radeon 独显同样在范围外 ——
         * 那条路上的功率和频率多半还被整机固件二次接管，ADLX 拿不到最终决定权。
         */
        if (string.Equals(vendor, ControlVendors.Amd, StringComparison.Ordinal)
            && chassisKind == ControlChassisKinds.Portable)
        {
            return [Excluded(
                "gpu.core-clock-offset",
                "核心频率偏移",
                "笔记本上的 AMD 独显不在本软件的范围内。")];
        }

        /*
         * 台式 A 卡：只留两家语义一致的那几项，链路标 ADLX。
         * 剩下的等 ADLX 真接进来再按那边的真实能力列 ——
         * 给 A 卡列一排写着 NVML、NVAPI 的项，是拿别人家的链路冒充自己的。
         */
        return items.Where(item => item.Id is "gpu.core-clock-offset"
                or "gpu.memory-clock-offset"
                or "gpu.power-limit"
                or "gpu.temperature-limit")
            .Select(item => item with
            {
                Channel = string.Equals(vendor, ControlVendors.Amd, StringComparison.Ordinal)
                    ? ControlChannels.Adlx
                    : null
            })
            .ToArray();
    }

    /// <summary>
    /// 明确**不在范围内**的一项。
    ///
    /// 和"还没接"必须分开：后者用户等着就会有，前者等不来。
    /// 但也不能干脆不列 —— 什么都不显示，用户会以为是我们没认出他的显卡。
    /// </summary>
    private static ControlCapability Excluded(string id, string label, string reason)
        => new(
            id,
            label,
            ControlValueKinds.Number,
            Supported: false,
            UnavailableReason: reason,
            UnavailableKind: ControlUnavailableKinds.Platform);

    /// <summary>
    /// 核显能调什么，**两家不一样**，所以按厂商给各自的那一项。
    ///
    /// AMD 的核显归 CPU 封装里的 SMU 管，能调的是 Curve Optimizer 的**档位**；
    /// Intel 的核显走显卡驱动自带的 IGCL，能调的是频率**偏移（MHz）**。
    /// 硬凑成同一项只会让其中一边的单位和语义都是错的。
    /// </summary>
    private static IReadOnlyList<ControlCapability> IntegratedGpuCapabilities(string vendor)
        => vendor switch
        {
            ControlVendors.Amd =>
            [
                // 和处理器同一条通道（辅助进程 → ZenStates-Core → SMU）。
                // 单位是档不是伏，理由同处理器那一项。
                Unsupported(
                    "gpu.curve-optimizer",
                    "Curve Optimizer 偏移",
                    ControlValueKinds.Number,
                    "hardware-bridge",
                    "硬件写入辅助进程",
                    new ControlNumberRange(-30, 10, 1, ControlUnits.Step, 0),
                    ControlAccessLevels.Normal)
            ],
            ControlVendors.Intel =>
            [
                // IGCL 的核显接口收的是 MHz 偏移（ctlOverclockGpuFrequencyOffsetSet），
                // 不是绝对频率。控制库随 Intel 显卡驱动一起装，不是单独的组件。
                Unsupported(
                    "gpu.core-clock-offset",
                    "核心频率偏移",
                    ControlValueKinds.Number,
                    "intel-graphics-driver",
                    "Intel 显卡驱动",
                    new ControlNumberRange(-200, 200, 5, ControlUnits.Megahertz, 0),
                    ControlAccessLevels.Normal)
            ],
            _ => []
        };

    private static void AddCpu(
        List<ControlObject> objects,
        HardwareMetricSnapshot snapshot,
        string cpuIdentity)
    {
        var vendor = VendorOf(snapshot.Cpu.Name);
        var (componentId, componentName) = vendor switch
        {
            ControlVendors.Amd => ("hardware-bridge", "硬件写入辅助进程"),
            ControlVendors.Intel => ("intel-pcm-provider", "Intel PCM / MSR Provider"),
            _ => ("librehardwaremonitor-provider", "LibreHardwareMonitor Provider")
        };

        objects.Add(new ControlObject(
            $"cpu:{cpuIdentity}",
            ControlObjectKinds.Cpu,
            snapshot.Cpu.Name.Trim(),
            new ControlObjectPlatform(ControlOperatingSystems.Windows, vendor),
            /*
             * **两家能调的根本不是同一批东西，所以按厂商分。**
             *
             * 先前这里不分厂商，于是 Intel 机器上会列出 PBO 标量、Curve Optimizer、
             * TDC/EDC —— 那几项是 AMD 的概念，Intel 上压根不存在。
             * 用户对着一项永远调不了的东西，只会以为是我们没做完。
             */
            vendor switch
            {
                ControlVendors.Amd => AmdCpuCapabilities(componentId, componentName),
                ControlVendors.Intel => IntelCpuCapabilities(componentId, componentName),
                // 认不出厂商就不列具体项：列谁家的都是错的。
                _ => []
            }));
    }

    /// <summary>AMD 处理器能调的那些。走 SMU，经硬件写入辅助进程。</summary>
    private static IReadOnlyList<ControlCapability> AmdCpuCapabilities(
        string componentId,
        string componentName)
        =>
        [
            /*
             * 三条功耗墙。**它们管的是三件不同的事，所以是三项，不是一项。**
             *
             * 持续（STAPM）决定机器长期跑在多少瓦，时间常数是几十秒到几分钟的热积分；
             * 短时（PPT slow）管的是几秒到几十秒那一段；
             * 瞬时（PPT fast）是毫秒级的峰值，也就是供电电路的真极限。
             *
             * 底层本来就是三条独立的 SMU 命令。先前持续和短时合成一个滑块，
             * 是因为这台机器上两者的出厂值恰好一样 —— 那是巧合，不是道理。
             */
            Unsupported(
                "cpu.power-limit",
                "持续功耗上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(5, 200, 1, ControlUnits.Watt)),
            Unsupported(
                "cpu.slow-power-limit",
                "短时功耗上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(5, 200, 1, ControlUnits.Watt)),
            Unsupported(
                "cpu.fast-power-limit",
                "瞬时功耗上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(5, 200, 1, ControlUnits.Watt)),
            /*
             * 两条电流墙。功耗墙管散热扛不扛得住，这两条管供电扛不扛得住 ——
             * 同样的瓦数在不同电压下是不同的电流，所以两者各有各的上限。
             * TDC 是持续电流，EDC 是峰值电流。
             */
            Unsupported(
                "cpu.tdc-limit",
                "持续电流上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(10, 300, 1, ControlUnits.Ampere)),
            Unsupported(
                "cpu.edc-limit",
                "峰值电流上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(10, 400, 1, ControlUnits.Ampere)),
            /*
             * 温度墙。**这一项和上面几条不是一个性质。**
             *
             * 功耗墙和电流墙调多高都有温度墙兜底；温度墙调高了，兜底的就是它自己。
             * 所以它要普通档，而上面那些在安全档就能调。
             */
            Unsupported(
                "cpu.temperature-limit",
                "温度上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(45, 105, 1, ControlUnits.Celsius),
                ControlAccessLevels.Normal),
            /*
             * PBO 标量。放宽的是固件那套可靠性预算（FIT）——
             * 倍数越大，固件越肯让电压和频率长时间待在高处。
             * 1 倍就是厂商默认，也就是不放宽。
             */
            Unsupported(
                "cpu.pbo-scalar",
                "PBO 标量",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(1, 10, 1, ControlUnits.Multiplier, 1),
                ControlAccessLevels.Normal),
            // 单位是**档**不是伏：一档大概几毫伏，具体多少随体质变，
            // 厂商也不给换算，编一个伏特数出来只会骗人。
            Unsupported(
                "cpu.curve-optimizer",
                "Curve Optimizer 偏移",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(-30, 10, 1, ControlUnits.Step, 0),
                ControlAccessLevels.Normal)
        ];

    /// <summary>
    /// Intel 处理器能调的那些。**和 AMD 是完全不同的一批控制点。**
    ///
    /// 走的是 MSR（部分还要配 MCHBAR 那份 MMIO 镜像），经 PawnIO ——
    /// 那条路不需要 WinRing0 那类会被内存完整性挡住的驱动。
    ///
    /// 几处刻意的取舍：
    ///
    /// <list type="bullet">
    /// <item>**功耗墙是 PL1/PL2 两条，各自带时间窗**，不是 AMD 那种三条。
    ///   硬凑成同一组只会让两边的语义都错。</item>
    /// <item>**没有 PBO 标量、没有 Curve Optimizer、没有 TDC/EDC** ——
    ///   那些是 AMD 的概念，Intel 上不存在。</item>
    /// <item>电压偏移在 **root** 档：它走 OC mailbox，而且有一批机型
    ///   （Plundervolt 之后锁掉的）会直接拒绝，写下去的结果不可预期。</item>
    /// <item>**IccMax / cTDP / BD PROCHOT 这类不列**：设计文档标的就是"默认不开"，
    ///   我们连意向链路都还没定，列出来只是占位。</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<ControlCapability> IntelCpuCapabilities(
        string componentId,
        string componentName)
    {
        ControlCapability Item(
            string id,
            string label,
            ControlNumberRange? range = null,
            string level = ControlAccessLevels.Normal)
            => Unsupported(
                id,
                label,
                ControlValueKinds.Number,
                componentId,
                componentName,
                range,
                level,
                ControlChannels.IntelMsr);

        return
        [
            /*
             * PL1 / PL2。**两条，不是一条。**
             *
             * PL1 是长期能跑多少瓦（几十秒的热积分），PL2 是短时冲刺那一段。
             * 它们在同一个寄存器里，但是两个独立的域。
             */
            Item("cpu.power-limit", "持续功耗上限（PL1）",
                new ControlNumberRange(5, 200, 1, ControlUnits.Watt)),
            Item("cpu.short-power-limit", "短时功耗上限（PL2）",
                new ControlNumberRange(5, 300, 1, ControlUnits.Watt)),
            /*
             * 时间窗。**它和瓦数是两件事**：同样的 PL2，窗口给 8 秒还是 56 秒，
             * 机器的手感完全不同。合成一项就没法表达了。
             */
            Item("cpu.power-limit-window", "持续功耗时间窗",
                new ControlNumberRange(1, 128, 1, ControlUnits.Second)),
            Item("cpu.short-power-limit-window", "短时功耗时间窗",
                new ControlNumberRange(1, 128, 1, ControlUnits.Second)),
            /*
             * MMIO 里的那份功耗墙镜像。
             *
             * **笔记本上这一条特别要紧**：整机厂常常从 MMIO 那边压一个更低的值，
             * 只写 MSR 的话有效功耗仍然被它按住，用户会看到"改了但没效果"。
             * 所以它是独立的一项，不是上面那条的实现细节。
             */
            Item("cpu.power-limit-mmio", "功耗上限（MMIO 镜像）",
                new ControlNumberRange(5, 200, 1, ControlUnits.Watt),
                ControlAccessLevels.Normal),
            /*
             * 温度墙。给的是**往下偏多少度**，不是绝对温度 —— 底层就是这么编码的，
             * 换算成绝对值要知道这颗的结点温度上限，而那个值各型号不同。
             * 和 AMD 那边给绝对温度不一样，这不是不一致，是两家本来就不同。
             */
            Item("cpu.temperature-offset", "温度墙下调",
                new ControlNumberRange(0, 30, 1, ControlUnits.Celsius, 0),
                ControlAccessLevels.Normal),
            /*
             * 电压偏移。Intel 把它分成几个电压域，**各调各的** ——
             * 核心和缓存在多数型号上是绑在一起的，核显和系统代理是另外两路。
             */
            Item("cpu.core-voltage-offset", "核心电压偏移",
                new ControlNumberRange(-250, 100, 1, ControlUnits.Millivolt, 0),
                ControlAccessLevels.Root),
            Item("cpu.cache-voltage-offset", "缓存电压偏移",
                new ControlNumberRange(-250, 100, 1, ControlUnits.Millivolt, 0),
                ControlAccessLevels.Root),
            Item("cpu.igpu-voltage-offset", "核显电压偏移",
                new ControlNumberRange(-250, 100, 1, ControlUnits.Millivolt, 0),
                ControlAccessLevels.Root),
            Item("cpu.system-agent-voltage-offset", "系统代理电压偏移",
                new ControlNumberRange(-250, 100, 1, ControlUnits.Millivolt, 0),
                ControlAccessLevels.Root),
            /*
             * 睿频倍频上限。调低是限频降温降耗，调高是超频 ——
             * 同一项两个方向，所以放普通档。
             */
            Item("cpu.turbo-ratio-limit", "睿频倍频上限",
                new ControlNumberRange(1, 60, 1, ControlUnits.Multiplier),
                ControlAccessLevels.Normal),
            Unsupported(
                "cpu.turbo-enabled",
                "睿频开关",
                ControlValueKinds.Toggle,
                componentId,
                componentName,
                null,
                ControlAccessLevels.Normal,
                ControlChannels.IntelMsr),
            /*
             * 能效偏好。这一项动的是**调度策略**，不是硬件上限 ——
             * 和上面那些不是一回事，但它确实是用户会想调的东西。
             */
            Item("cpu.energy-performance-preference", "能效偏好",
                new ControlNumberRange(0, 255, 1, ControlUnits.None)),
            /*
             * 处理器和核显之间怎么分功率。两颗一起吃同一份预算，
             * 这一项决定吃紧的时候先保谁。
             */
            Item("cpu.igpu-power-balance", "处理器 / 核显功率分配",
                new ControlNumberRange(0, 31, 1, ControlUnits.None),
                ControlAccessLevels.Normal)
        ];
    }

    /// <summary>
    /// 风扇。现在只认已经在报转速的那些 —— 报得出转速说明采集这一侧通了，
    /// 缺的只是写入。认不出来的风扇不假装存在。
    /// </summary>
    /// <summary>
    /// 风扇。
    ///
    /// **风扇的身份不跟着显卡走。** 笔记本的风扇往往是整机共用一套散热，
    /// AI Max 395 这类 SoC 更是把 CPU 和 GPU 放在一颗封装里 —— 哪个风扇吹哪个部件
    /// 不是"卡上带一个风扇"那么简单，强行挂到显卡身份上只会把关系说错。
    ///
    /// 所以风扇就是一个独立实例，按位置定身份。风扇换了之后原来那份设定怎么作废，
    /// 归后面专门的风扇管理方案 —— 这里不先造一套将来要推翻的规则。
    /// </summary>
    /// <summary>
    /// 给对象挂上机箱形态词条。**一处统一挂，不由各个建造处各挂各的。**
    ///
    /// 笔记本和台式机不只是显示差别，是**路由差别**：笔记本上显卡的功率预算归
    /// 整机固件管（本机实测 NVML 两条写入路径都被拒），处理器的功耗墙也常常
    /// 被整机厂压过一道；台式机上主板管得少，同样的项走的是完全不同的通道。
    /// 把这一条摆成显式词条，写入器就不用各自从型号名、通道名里去猜。
    ///
    /// 风扇的**角色**（CPU 风扇 / 显卡风扇 / 内吹 / 机箱）先空着：
    /// 固件只给序号，不说哪个吹什么。认得出来（或者用户自己指）再挂，不拿猜的充数。
    /// </summary>
    private ControlObject WithChassisTerm(ControlObject candidate)
    {
        var kind = chassis.Read();
        if (string.Equals(kind, ControlChassisKinds.Unknown, StringComparison.Ordinal))
        {
            return candidate;
        }
        return candidate with
        {
            Terms = [kind, .. candidate.Terms ?? []]
        };
    }

    /// <summary>
    /// 一个风扇挂哪些词条。
    ///
    /// 角色（吹 CPU 还是吹显卡）由核心回答，认不出来就不挂 ——
    /// 挂一个"未知"会让用户以为我们查过并且查出了"未知"这个答案。
    ///
    /// 曲线归谁执行也挂出来：**固件执行和软件执行是完全不同的承诺**，
    /// 前者写进去就一直有效，后者程序一退就没了。用户得先知道这一点，
    /// 才谈得上判断这条曲线值不值得设。
    /// </summary>
    private static IReadOnlyList<string> FanTerms(FanCoreFan fan)
    {
        var terms = new List<string>();
        if (!string.Equals(fan.Role, ControlFanRoles.Unknown, StringComparison.Ordinal))
        {
            terms.Add(fan.Role);
        }
        /*
         * 曲线**能交给谁执行**。两条路都有的机器会同时挂两个词条 ——
         * 那正是用户需要看到的：他有得选。
         *
         * 一条都没有的（只能切模式、或者只读）不挂：那不是一种曲线，
         * 写上去反而像是某种曲线模式。为什么没有，由那一项自己的原因说。
         */
        if (fan.FirmwareCurve is not null)
        {
            terms.Add("curve-firmware");
            /*
             * 固件表里**只有转速档可写，温度断点是固件定死的**（联想 Legion 那种
             * 10 档表就是）。单挂一条词条标出来，界面据此说明"这里只调每一档的转速"。
             *
             * 不标的话，曲线图看起来和能任意拖点的那种一模一样 ——
             * 用户拖了温度却不生效，比不给更糟。
             */
            if (string.Equals(
                fan.FirmwareCurve,
                ControlFanFirmwareCurveKinds.Table,
                StringComparison.Ordinal))
            {
                terms.Add("curve-fixed-steps");
            }
        }
        if (fan.SupportsSoftwareCurve)
        {
            terms.Add("curve-software");
            /*
             * 软件接管是整机一个开关的平台，单挂一条词条标出来 ——
             * 界面据此在用户**选之前**提醒他这会波及别的风扇。
             * 事后才发现另一把风扇不转了，那就晚了。
             */
            if (fan.SoftwareTakeoverCoversAllFans)
            {
                terms.Add("takeover-all-fans");
            }
        }
        return terms;
    }

    /// <summary>
    /// 机身风扇上该有哪几项。**每个风扇一份，不是整机一份。**
    /// </summary>
    private static IReadOnlyList<ControlCapability> ChassisFanCapabilities()
    {
        var (componentId, componentName) = ("fan-control-core", "风扇控制核心");
        return
        [
            Unsupported("fan.rpm", "转速", ControlValueKinds.Number, componentId, componentName),
            Unsupported(
                "fan.curve",
                "转速曲线",
                ControlValueKinds.Curve,
                componentId,
                componentName),
            /*
             * 固定转速。
             *
             * **先前根本没列出来。** 核心的协议一直有 set（占空比），
             * 只是这一侧没往上接 —— 于是用户能看到的只有"曲线"和"全速"，
             * 中间那个最常用的"就固定在 45%"反而不存在。
             *
             * 下限给 0：停转是这条通道真实允许的值，由核心按机型夹。
             * 我们不自己猜一个"最低 20%"—— 猜低了风扇停转，猜高了白吵。
             */
            Unsupported(
                "fan.duty",
                "固定转速",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(0, 100, 1, ControlUnits.Percent)),
            Unsupported(
                "fan.lock-maximum",
                "一键强冷",
                ControlValueKinds.Toggle,
                componentId,
                componentName)
        ];
    }

    /// <summary>
    /// 机身风扇。
    ///
    /// **有几个风扇由风扇控制核心说了算**，不由监控里有几条转速读数决定。
    /// 核心是唯一知道"这条通道上挂着几个风扇、各自能做什么"的那个；
    /// 监控只知道"有一个风扇在转"。先前这里按后者建对象，于是本机两个风扇
    /// （主/副）被塌成一个，副风扇连存在都看不见。
    ///
    /// 核心报不出来时才退回监控那条转速读数，列一个只读对象 ——
    /// 那时我们确实只知道这么多。
    /// </summary>
    private void AddFans(
        List<ControlObject> objects,
        HardwareMetricSnapshot snapshot)
    {
        var ordinal = 0;
        var described = fanCore.Describe();
        var coreKnowsGpuFan = false;
        if (described.Count > 0)
        {
            foreach (var fan in described)
            {
                ordinal++;
                coreKnowsGpuFan |= string.Equals(
                    fan.Role,
                    ControlFanRoles.Gpu,
                    StringComparison.Ordinal);
                objects.Add(new ControlObject(
                    $"{FanControlWriter.ObjectIdPrefix}{fan.Index}",
                    ControlObjectKinds.Fan,
                    /*
                     * **名字按位置编号，角色用词条说。**
                     *
                     * 编号从 1 起：用户数风扇是从 1 数的，序号 0 是我们内部的事。
                     * 它吹的是 CPU 还是显卡不写进名字 —— 那是角色，
                     * 而角色由核心回答（它才知道这条通道上哪个是哪个），
                     * 认不出来的就不挂词条，不编一个。
                     */
                    $"风扇{ordinal}",
                    new ControlObjectPlatform(ControlOperatingSystems.Windows, ControlVendors.Oem),
                    ChassisFanCapabilities(),
                    fan.Detail,
                    Terms: FanTerms(fan)));
            }
        }
        else if (snapshot.Items.TryGetValue("cpu.fanRpm", out var cpuFan)
            && cpuFan.NumericValue is { } rpm
            && double.IsFinite(rpm))
        {
            var notebook = cpuFan.Detail?.Contains("notebook", StringComparison.OrdinalIgnoreCase)
                == true;
            ordinal++;
            objects.Add(new ControlObject(
                $"{FanControlWriter.ObjectIdPrefix}0",
                ControlObjectKinds.Fan,
                $"风扇{ordinal}",
                new ControlObjectPlatform(
                    ControlOperatingSystems.Windows,
                    notebook ? ControlVendors.Oem : ControlVendors.Unknown),
                ChassisFanCapabilities(),
                cpuFan.Detail));
        }

        /*
         * 显卡上那个风扇。
         *
         * **它不跟显卡绑。** 笔记本往往整机共用一套散热，AI SoC 更是把 CPU 和 GPU
         * 放进一颗封装 —— 哪个风扇吹哪个部件不是"卡上带一个风扇"那么简单，
         * 挂到显卡身份上只会把关系说错（设计文档里就是这么定的）。
         *
         * 所以它和别的风扇一样，是一个独立的风扇对象、按位置编号；
         * "这是显卡的风扇"用**词条**说，不用名字说。
         *
         * **但核心已经报了显卡风扇的话，这里就不能再建一个。**
         * 那是同一个风扇：本机实测核心报两个（CPU / 显卡），显卡那边的转速读数
         * 读的就是核心的 1 号，于是界面上出现了"风扇2"和"风扇3"两个条目、
         * 其实是同一把风扇。有没有这个风扇由核心说了算 ——
         * 监控只知道"有个风扇在转"，不知道它是不是已经被数过了。
         */
        foreach (var gpu in snapshot.Gpus)
        {
            if (coreKnowsGpuFan
                || gpu.Sensors.FanSpeedRpm is not { } gpuRpm
                || !double.IsFinite(gpuRpm))
            {
                continue;
            }
            var vendor = VendorOf(gpu.Name);
            /*
             * 它该走哪条，**看机箱**：台式机上这个风扇装在卡上，归显卡驱动管；
             * 笔记本上它是整机散热的一部分，归整机固件管，显卡驱动碰不到。
             * 同一个"显卡风扇"在两种机器上是两条完全不同的路，
             * 缺的组件当然也跟着不一样 —— 所以链路和组件一起在这里定。
             */
            var (fanChannel, componentId, componentName) =
                chassis.Read() == ControlChassisKinds.Portable
                    ? ((string?)ControlChannels.OemEmbeddedController,
                        "uniwill-ec-provider", "整机固件接口")
                    : vendor switch
                    {
                        /*
                         * 台式 N 卡的风扇走 **NVML**（`nvmlDeviceSetFanSpeed_v2`
                         * 设每个风扇的百分比，`SetDefaultFanSpeed_v2` 恢复），
                         * 不是 NVAPI —— 先前标成 NVAPI 是错的。
                         * 它只给固定占空比，曲线由统一的软件曲线引擎跑。
                         */
                        ControlVendors.Nvidia => (ControlChannels.Nvml,
                            "nvidia-nvapi-provider", "NVIDIA NVAPI Provider"),
                        // 台式 A 卡走 ADLX 的风扇调节（原生曲线、目标转速、零转速）。
                        ControlVendors.Amd => (ControlChannels.Adlx,
                            "amd-adlx-provider", "AMD ADLX / ADL Provider"),
                        // 不认识的卡就不编链路：说不出走哪条，比说错强。
                        _ => (null, "msi-afterburner", "MSI Afterburner")
                    };
            ordinal++;
            objects.Add(new ControlObject(
                $"fan:gpu{gpu.Index}",
                ControlObjectKinds.Fan,
                $"风扇{ordinal}",
                new ControlObjectPlatform(ControlOperatingSystems.Windows, vendor),
                [
                    Unsupported(
                        "fan.curve",
                        "转速曲线",
                        ControlValueKinds.Curve,
                        componentId,
                        componentName,
                        channel: fanChannel),
                    Unsupported(
                        "fan.duty",
                        "固定转速",
                        ControlValueKinds.Number,
                        componentId,
                        componentName,
                        new ControlNumberRange(0, 100, 1, ControlUnits.Percent),
                        channel: fanChannel),
                    Unsupported(
                        "fan.lock-maximum",
                        "一键强冷",
                        ControlValueKinds.Toggle,
                        componentId,
                        componentName,
                        channel: fanChannel)
                ],
                $"GPU{gpu.Index}",
                Terms: [ControlFanRoles.Gpu]));
        }
    }

    private static ControlCapability Unsupported(
        string id,
        string label,
        string valueKind,
        string componentId,
        string componentName,
        ControlNumberRange? range = null,
        string accessLevel = ControlAccessLevels.Normal,
        string? channel = null)
        => new(
            id,
            label,
            valueKind,
            Supported: false,
            UnavailableReason: WriterNotImplemented,
            // 没有任何写入器认领时就是"我们还没接"，不是"这台机器做不到" ——
            // 后者要由真去问过硬件的写入器来说。
            UnavailableKind: ControlUnavailableKinds.NotImplemented,
            RequiredComponentId: componentId,
            RequiredComponentName: componentName,
            Range: range,
            Channel: channel,
            RequiredAccessLevel: accessLevel);

    private static string VendorOf(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return ControlVendors.Nvidia;
        }
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return ControlVendors.Amd;
        }
        return name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            ? ControlVendors.Intel
            : ControlVendors.Unknown;
    }
}
