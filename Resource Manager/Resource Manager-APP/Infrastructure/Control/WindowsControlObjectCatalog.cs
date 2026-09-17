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
    IEnumerable<IControlWriter> writers) : IControlObjectCatalog
{
    /// <summary>读显卡出厂唯一标识用的。读不到就退到型号，见 ControlInstanceIdentity。</summary>
    private readonly NvidiaNvmlControlBridge uniqueIdReader = new();

    /// <summary>处理器的身份。核显跟着它走 —— 核显没有自己的标识。</summary>
    private readonly WindowsCpuIdentityReader cpuIdentityReader = new();

    /// <summary>没有任何写入器认领这一项时的原因。接进一个就少一条。</summary>
    private const string WriterNotImplemented = "控制写入尚未接入，当前只能读取。";

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
            objects.Select(ResolveAvailability).ToArray(),
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

    private ControlCapability ResolveCapability(
        ControlObject candidate,
        ControlCapability capability)
    {
        foreach (var writer in writers)
        {
            var availability = writer.Probe(candidate, capability);
            if (!availability.IsMine)
            {
                continue;
            }
            return capability with
            {
                Supported = availability.CanWrite,
                UnavailableReason = availability.CanWrite ? null : availability.Reason,
                // 硬件报得出真实范围就用真实的；报不出就保留目录里的形状。
                Range = availability.Range ?? capability.Range
            };
        }
        return capability;
    }

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
                    : DiscreteGpuCapabilities(vendor, gpu),
                $"GPU{gpu.Index}",
                attachment,
                gpu.Index));
        }
    }

    private static IReadOnlyList<ControlCapability> DiscreteGpuCapabilities(
        string vendor,
        GpuMetrics gpu)
    {
        // 厂商决定这块卡要哪条写入路径。列出来是为了让用户看到差别，
        // 而不是笼统地说"不支持超频"。
        var (componentId, componentName) = vendor switch
        {
            ControlVendors.Nvidia => ("nvidia-nvapi-provider", "NVIDIA NVAPI Provider"),
            ControlVendors.Amd => ("amd-adlx-provider", "AMD ADLX / ADL Provider"),
            _ => ("msi-afterburner", "MSI Afterburner")
        };

        return
        [
            Unsupported(
                "gpu.core-clock-offset",
                "核心频率偏移",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(-500, 500, 5, "MHz", 0)),
            Unsupported(
                "gpu.memory-clock-offset",
                "显存频率偏移",
                ControlValueKinds.Number,
                componentId,
                componentName,
                new ControlNumberRange(-1000, 2000, 5, "MHz", 0)),
            Unsupported(
                "gpu.power-limit",
                "功耗上限",
                ControlValueKinds.Number,
                componentId,
                componentName,
                // 上限用这块卡自己报的额定功耗，读不到就不给范围。
                gpu.Sensors.PowerLimitWatts is { } limit and > 0
                    ? new ControlNumberRange(50, Math.Round(limit * 1.2), 1, "W", limit)
                    : null),
            Unsupported(
                "gpu.fan-curve",
                "显卡风扇曲线",
                ControlValueKinds.Curve,
                componentId,
                componentName),
            // 笔记本的功率预算不在显卡里，在固件里：EC 把预算报给 ACPI 的 NvPCF 表，
            // 驱动读到之后才决定 TGP。所以这两项和上面那个"功耗上限"是两条不同的路，
            // 谁能用由各自的写入器回答。
            Unsupported(
                "gpu.ctgp-offset",
                "cTGP 偏移",
                ControlValueKinds.Number,
                "uniwill-ec-provider",
                "Uniwill 平台接口",
                new ControlNumberRange(0, 100, 1, "W", 0)),
            Unsupported(
                "gpu.dynamic-boost-offset",
                "Dynamic Boost 额度",
                ControlValueKinds.Number,
                "uniwill-ec-provider",
                "Uniwill 平台接口",
                new ControlNumberRange(0, 100, 1, "W", 0))
        ];
    }

    /// <summary>
    /// 核显能调的比独显少得多。
    ///
    /// 它的频率和功耗由 CPU 封装的 SMU 统一管，没有独立的显存也没有独立供电，
    /// 所以没有"显存频率偏移"，"功耗上限"也不是这块卡自己的事 ——
    /// 真要限它得去调处理器的封装功耗。与其列一堆它做不到的项，不如如实说清楚。
    /// </summary>
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
                    new ControlNumberRange(-30, 10, 1, ControlUnits.Step, 0))
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
                    new ControlNumberRange(-200, 200, 5, ControlUnits.Megahertz, 0))
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
            [
                Unsupported(
                    "cpu.power-limit",
                    "功耗上限",
                    ControlValueKinds.Number,
                    componentId,
                    componentName,
                    new ControlNumberRange(5, 200, 1, "W")),
                // 瞬时那一条单独露出来。它和上面那个不是一回事：上面管的是持续和长时，
                // 决定机器长期跑在多少瓦；这一条管的是短暂加速能冲到多高。
                // 底层本来就是两条 SMU 命令，合成一个滑块等于替用户砍掉一半能力。
                Unsupported(
                    "cpu.fast-power-limit",
                    "瞬时功耗上限",
                    ControlValueKinds.Number,
                    componentId,
                    componentName,
                    new ControlNumberRange(5, 200, 1, "W")),
                // 单位是**档**不是伏：一档大概几毫伏，具体多少随体质变，
                // 厂商也不给换算，编一个伏特数出来只会骗人。
                Unsupported(
                    "cpu.curve-optimizer",
                    "Curve Optimizer 偏移",
                    ControlValueKinds.Number,
                    componentId,
                    componentName,
                    new ControlNumberRange(-30, 10, 1, "档", 0))
            ]));
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
    private static void AddFans(
        List<ControlObject> objects,
        HardwareMetricSnapshot snapshot)
    {
        if (snapshot.Items.TryGetValue("cpu.fanRpm", out var cpuFan)
            && cpuFan.NumericValue is { } rpm
            && double.IsFinite(rpm))
        {
            // 笔记本风扇和台式风扇是两种情况，需要的组件也不同。
            var notebook = cpuFan.Detail?.Contains("notebook", StringComparison.OrdinalIgnoreCase)
                == true;
            // 机身风扇走风扇控制核心：它按控制通道分类，
            // 不区分笔记本和台式。
            var (componentId, componentName) = ("fan-control-core", "风扇控制核心");

            objects.Add(new ControlObject(
                "fan:cpu",
                ControlObjectKinds.Fan,
                notebook ? "机身风扇" : "CPU 风扇",
                new ControlObjectPlatform(
                    ControlOperatingSystems.Windows,
                    notebook ? ControlVendors.Oem : ControlVendors.Unknown),
                [
                    Unsupported(
                        "fan.curve",
                        "转速曲线",
                        ControlValueKinds.Curve,
                        componentId,
                        componentName),
                    Unsupported(
                        "fan.lock-maximum",
                        "一键强冷",
                        ControlValueKinds.Toggle,
                        componentId,
                        componentName)
                ],
                cpuFan.Detail));
        }

        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.Sensors.FanSpeedRpm is not { } gpuRpm || !double.IsFinite(gpuRpm))
            {
                continue;
            }
            // 显卡风扇挂在显卡那条写入路径上，不是独立风扇控制器。
            var vendor = VendorOf(gpu.Name);
            var (componentId, componentName) = vendor switch
            {
                ControlVendors.Nvidia => ("nvidia-nvapi-provider", "NVIDIA NVAPI Provider"),
                ControlVendors.Amd => ("amd-adlx-provider", "AMD ADLX / ADL Provider"),
                _ => ("msi-afterburner", "MSI Afterburner")
            };
            objects.Add(new ControlObject(
                $"fan:gpu{gpu.Index}",
                ControlObjectKinds.Fan,
                $"{gpu.Name} 风扇",
                new ControlObjectPlatform(ControlOperatingSystems.Windows, vendor),
                [
                    Unsupported(
                        "fan.curve",
                        "转速曲线",
                        ControlValueKinds.Curve,
                        componentId,
                        componentName),
                    Unsupported(
                        "fan.lock-maximum",
                        "一键强冷",
                        ControlValueKinds.Toggle,
                        componentId,
                        componentName)
                ],
                $"GPU{gpu.Index}",
                AdapterIndex: gpu.Index));
        }
    }

    private static ControlCapability Unsupported(
        string id,
        string label,
        string valueKind,
        string componentId,
        string componentName,
        ControlNumberRange? range = null)
        => new(
            id,
            label,
            valueKind,
            Supported: false,
            UnavailableReason: WriterNotImplemented,
            RequiredComponentId: componentId,
            RequiredComponentName: componentName,
            Range: range);

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
