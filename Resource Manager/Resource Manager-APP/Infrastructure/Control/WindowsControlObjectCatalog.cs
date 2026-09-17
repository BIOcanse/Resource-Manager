using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
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
    /// <summary>没有任何写入器认领这一项时的原因。接进一个就少一条。</summary>
    private const string WriterNotImplemented = "控制写入尚未接入，当前只能读取。";

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();

    public ControlObjectCatalog ReadObjects()
    {
        var snapshot = catalogState.Current;
        var objects = new List<ControlObject>();
        if (snapshot is not null)
        {
            AddGpus(objects, snapshot);
            AddCpu(objects, snapshot);
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

    private static void AddGpus(List<ControlObject> objects, HardwareMetricSnapshot snapshot)
    {
        foreach (var gpu in snapshot.Gpus)
        {
            var vendor = VendorOf(gpu.Name);
            // 核显和独显的可调自由度差很多，所以要分开 —— 沿用性能分那边已有的判定，
            // 不另写一套型号名单。
            var attachment = GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(gpu.Name)
                ? ControlGpuAttachments.Integrated
                : ControlGpuAttachments.Discrete;
            // 每块卡一个对象，各自带自己的 (系统, 厂商) —— 不是全局状态。
            objects.Add(new ControlObject(
                $"gpu:{gpu.IdentityKey ?? gpu.Index.ToString()}",
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
    private static IReadOnlyList<ControlCapability> IntegratedGpuCapabilities(string vendor)
    {
        var (componentId, componentName) = vendor switch
        {
            ControlVendors.Amd => ("amd-smu-pawnio-provider", "AMD SMU / PawnIO Provider"),
            // Intel 核显的控制库（IGCL）随显卡驱动一起装，不是单独的组件。
            ControlVendors.Intel => ("intel-graphics-driver", "Intel 显卡驱动"),
            _ => ("librehardwaremonitor-provider", "LibreHardwareMonitor Provider")
        };
        return
        [
            Unsupported(
                "gpu.core-clock-offset",
                "核心频率偏移",
                ControlValueKinds.Number,
                componentId,
                componentName,
                // 核显的频率余量比独显窄得多。
                new ControlNumberRange(-200, 200, 5, "MHz", 0))
        ];
    }

    private static void AddCpu(List<ControlObject> objects, HardwareMetricSnapshot snapshot)
    {
        var vendor = VendorOf(snapshot.Cpu.Name);
        var (componentId, componentName) = vendor switch
        {
            ControlVendors.Amd => ("amd-smu-pawnio-provider", "AMD SMU / PawnIO Provider"),
            ControlVendors.Intel => ("intel-pcm-provider", "Intel PCM / MSR Provider"),
            _ => ("librehardwaremonitor-provider", "LibreHardwareMonitor Provider")
        };

        objects.Add(new ControlObject(
            "cpu:package",
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
                Unsupported(
                    "cpu.core-voltage-offset",
                    "核心电压偏移",
                    ControlValueKinds.Number,
                    componentId,
                    componentName,
                    new ControlNumberRange(-0.2, 0.2, 0.005, "V", 0))
            ]));
    }

    /// <summary>
    /// 风扇。现在只认已经在报转速的那些 —— 报得出转速说明采集这一侧通了，
    /// 缺的只是写入。认不出来的风扇不假装存在。
    /// </summary>
    private static void AddFans(List<ControlObject> objects, HardwareMetricSnapshot snapshot)
    {
        if (snapshot.Items.TryGetValue("cpu.fanRpm", out var cpuFan)
            && cpuFan.NumericValue is { } rpm
            && double.IsFinite(rpm))
        {
            // 笔记本风扇和台式风扇是两种情况，需要的组件也不同。
            var notebook = cpuFan.Detail?.Contains("notebook", StringComparison.OrdinalIgnoreCase)
                == true;
            var (componentId, componentName) = notebook
                ? ("notebook-fancontrol-provider", "Notebook FanControl Provider")
                : ("librehardwaremonitor-provider", "LibreHardwareMonitor Provider");

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
                $"fan:gpu:{gpu.IdentityKey ?? gpu.Index.ToString()}",
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
