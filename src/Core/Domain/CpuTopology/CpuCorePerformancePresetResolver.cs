using System.Text.RegularExpressions;

namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuCorePerformanceInput(
    int CoreIndex,
    int RelationshipEfficiencyClass,
    int? CpuSetEfficiencyClass,
    int? CpuSetSchedulingClass,
    int LogicalProcessorCount);

public sealed record CpuCorePerformanceScore(
    int CoreIndex,
    double PerformanceScore,
    string PerformanceClass,
    string Source);

public sealed record CpuCorePerformanceScoreResult(
    IReadOnlyDictionary<int, CpuCorePerformanceScore> ScoresByCoreIndex,
    IReadOnlyList<string> Notes);

public sealed record CpuCorePerformancePreset(
    string Vendor,
    string Family,
    bool KnownHybrid,
    bool KnownHomogeneous,
    bool KnownX3d,
    string Source,
    int? ExpectedPerformanceCoreCount = null,
    int? ExpectedEfficientCoreCount = null,
    int? ExpectedLowPowerEfficientCoreCount = null,
    bool PerformanceCoreHasSmt = true,
    bool EfficientCoreHasSmt = false,
    bool LowPowerEfficientCoreHasSmt = false,
    double PerformanceCoreScore = 100,
    double EfficientCoreScore = 65,
    double LowPowerEfficientCoreScore = 45,
    string PerformanceCoreClassName = "P-core",
    string EfficientCoreClassName = "E-core",
    string LowPowerEfficientCoreClassName = "LP-E-core")
{
    public bool HasExpectedCoreLayout =>
        ExpectedPerformanceCoreCount.HasValue
        || ExpectedEfficientCoreCount.HasValue
        || ExpectedLowPowerEfficientCoreCount.HasValue;

    public int ExpectedPhysicalCoreCount =>
        (ExpectedPerformanceCoreCount ?? 0)
        + (ExpectedEfficientCoreCount ?? 0)
        + (ExpectedLowPowerEfficientCoreCount ?? 0);

    public int ExpectedLogicalProcessorCount =>
        (ExpectedPerformanceCoreCount ?? 0) * (PerformanceCoreHasSmt ? 2 : 1)
        + (ExpectedEfficientCoreCount ?? 0) * (EfficientCoreHasSmt ? 2 : 1)
        + (ExpectedLowPowerEfficientCoreCount ?? 0) * (LowPowerEfficientCoreHasSmt ? 2 : 1);
}

public static class CpuCorePerformancePresetResolver
{
    private const double PerformanceCoreScore = 100;
    private const double EfficientCoreScore = 65;
    private const double LowPowerEfficientCoreScore = 45;

    public static CpuCorePerformanceScoreResult Resolve(
        string cpuName,
        IReadOnlyList<CpuCorePerformanceInput> cores)
    {
        var ordered = cores.OrderBy(static core => core.CoreIndex).ToArray();
        if (ordered.Length == 0)
        {
            return new CpuCorePerformanceScoreResult(
                new Dictionary<int, CpuCorePerformanceScore>(),
                ["CPU 拓扑为空，无法建立核心性能分。"]);
        }

        var preset = Classify(cpuName);
        var notes = new List<string>
        {
            $"CPU 型号预设：{preset.Family}（{preset.Source}）。"
        };

        if (preset.KnownX3d)
        {
            notes.Add("X3D 属于缓存/频率 CCD 偏好，不是大小核；当前单维性能分保持同分，后续用 workload-specific CCD role 区分。");
        }

        var presetScores = TryResolveFromExactPresetLayout(ordered, preset, notes);
        if (presetScores is not null)
        {
            return new CpuCorePerformanceScoreResult(presetScores, notes);
        }

        var cpuSetScores = TryResolveFromCpuSetEfficiency(ordered, preset, notes);
        if (cpuSetScores is not null)
        {
            return new CpuCorePerformanceScoreResult(cpuSetScores, notes);
        }

        var relationshipScores = TryResolveFromRelationshipEfficiency(ordered, preset, notes);
        if (relationshipScores is not null)
        {
            return new CpuCorePerformanceScoreResult(relationshipScores, notes);
        }

        var smtScores = TryResolveFromIntelHybridShape(ordered, preset, notes);
        if (smtScores is not null)
        {
            return new CpuCorePerformanceScoreResult(smtScores, notes);
        }

        if (preset.KnownHomogeneous)
        {
            notes.Add("主流同构 CPU 预设命中，所有物理核心使用同一性能分。");
        }
        else
        {
            notes.Add("未识别到可靠异构核心分级，所有物理核心使用同一性能分。");
        }

        return new CpuCorePerformanceScoreResult(
            CreateUniformScores(
                ordered,
                preset.KnownHomogeneous ? "model-homogeneous" : "uniform-fallback",
                preset.PerformanceCoreScore),
            notes);
    }

    public static CpuCorePerformancePreset Classify(string cpuName)
    {
        var normalized = NormalizeCpuName(cpuName);

        if (TryClassifyIntelCoreUltraDesktop(normalized, out var coreUltraPreset))
        {
            return coreUltraPreset;
        }

        if (TryClassifyIntelCoreUltraMobile(normalized, out var coreUltraMobilePreset))
        {
            return coreUltraMobilePreset;
        }

        if (TryClassifyIntelCoreSeriesMobile(normalized, out var coreSeriesMobilePreset))
        {
            return coreSeriesMobilePreset;
        }

        if (TryClassifyIntelNSeries(normalized, out var intelNSeriesPreset))
        {
            return intelNSeriesPreset;
        }

        if (TryClassifyIntelCoreLegacy(normalized, out var corePreset))
        {
            return corePreset;
        }

        if (IsIntelCoreUltra(normalized))
        {
            return new CpuCorePerformancePreset(
                "Intel",
                "Intel Core Ultra hybrid CPU",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-ultra-generic");
        }

        var intel = ContainsAny(normalized, "Intel", "Core(TM)", "Xeon");
        if (intel)
        {
            return new CpuCorePerformancePreset(
                "Intel",
                "Intel mainstream homogeneous or unknown-hybrid",
                KnownHybrid: false,
                KnownHomogeneous: true,
                KnownX3d: false,
                "model-preset:intel-default");
        }

        if (TryClassifyAmdRyzenAi(normalized, out var amdRyzenAiPreset))
        {
            return amdRyzenAiPreset;
        }

        if (TryClassifyAmdRyzenZSeries(normalized, out var amdRyzenZPreset))
        {
            return amdRyzenZPreset;
        }

        if (TryClassifyAmdRyzenMobile(normalized, out var amdRyzenMobilePreset))
        {
            return amdRyzenMobilePreset;
        }

        var amd = ContainsAny(normalized, "AMD", "Ryzen", "Threadripper", "EPYC");
        var x3d = normalized.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        if (amd)
        {
            return new CpuCorePerformancePreset(
                "AMD",
                x3d ? "AMD X3D CCD-role CPU" : "AMD homogeneous Zen CPU",
                KnownHybrid: false,
                KnownHomogeneous: true,
                KnownX3d: x3d,
                x3d ? "model-preset:amd-x3d" : "model-preset:amd-zen");
        }

        return new CpuCorePerformancePreset(
            "Unknown",
            "Unknown CPU",
            KnownHybrid: false,
            KnownHomogeneous: false,
            KnownX3d: false,
            "model-preset:unknown");
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore>? TryResolveFromExactPresetLayout(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        CpuCorePerformancePreset preset,
        ICollection<string> notes)
    {
        if (!preset.HasExpectedCoreLayout)
        {
            return null;
        }

        var actualPhysicalCoreCount = cores.Count;
        var actualLogicalProcessorCount = cores.Sum(static core => Math.Max(0, core.LogicalProcessorCount));
        if (actualPhysicalCoreCount != preset.ExpectedPhysicalCoreCount
            || actualLogicalProcessorCount != preset.ExpectedLogicalProcessorCount)
        {
            notes.Add(
                $"型号预设核心形态与系统当前拓扑不一致：预设 {preset.ExpectedPhysicalCoreCount}C/{preset.ExpectedLogicalProcessorCount}T，当前 {actualPhysicalCoreCount}C/{actualLogicalProcessorCount}T；改用系统事实或 fallback。");
            return null;
        }

        notes.Add($"使用主流 CPU 型号预设核心形态建立性能分：{FormatExpectedLayout(preset)}。");
        var ordered = cores.OrderBy(static core => core.CoreIndex).ToArray();
        var result = new Dictionary<int, CpuCorePerformanceScore>();
        var performanceCoreCount = preset.ExpectedPerformanceCoreCount ?? 0;
        var efficientCoreCount = preset.ExpectedEfficientCoreCount ?? 0;
        var lowPowerEfficientCoreCount = preset.ExpectedLowPowerEfficientCoreCount ?? 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            var core = ordered[index];
            CpuCorePerformanceScore score;
            if (index < performanceCoreCount)
            {
                score = new CpuCorePerformanceScore(
                    core.CoreIndex,
                    preset.PerformanceCoreScore,
                    preset.PerformanceCoreClassName,
                    $"{preset.Source}:core-layout");
            }
            else if (index < performanceCoreCount + efficientCoreCount)
            {
                score = new CpuCorePerformanceScore(
                    core.CoreIndex,
                    preset.EfficientCoreScore,
                    preset.EfficientCoreClassName,
                    $"{preset.Source}:core-layout");
            }
            else if (index < performanceCoreCount + efficientCoreCount + lowPowerEfficientCoreCount)
            {
                score = new CpuCorePerformanceScore(
                    core.CoreIndex,
                    preset.LowPowerEfficientCoreScore,
                    preset.LowPowerEfficientCoreClassName,
                    $"{preset.Source}:core-layout");
            }
            else
            {
                score = new CpuCorePerformanceScore(
                    core.CoreIndex,
                    preset.PerformanceCoreScore,
                    "Homogeneous-core",
                    $"{preset.Source}:core-layout");
            }

            result[core.CoreIndex] = score;
        }

        return result;
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore>? TryResolveFromCpuSetEfficiency(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        CpuCorePerformancePreset preset,
        ICollection<string> notes)
    {
        if (!HasDiverseValues(cores.Select(static core => core.CpuSetEfficiencyClass)))
        {
            return null;
        }

        notes.Add("使用 Windows CPU Sets EfficiencyClass 建立核心性能分。");
        return CreateEfficiencyClassScores(
            cores,
            static core => core.CpuSetEfficiencyClass!.Value,
            preset,
            "windows-cpu-set-efficiency");
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore>? TryResolveFromRelationshipEfficiency(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        CpuCorePerformancePreset preset,
        ICollection<string> notes)
    {
        if (!HasDiverseValues(cores.Select(static core => (int?)core.RelationshipEfficiencyClass)))
        {
            return null;
        }

        notes.Add("使用 GetLogicalProcessorInformationEx PROCESSOR_RELATIONSHIP.EfficiencyClass 建立核心性能分。");
        return CreateEfficiencyClassScores(
            cores,
            static core => core.RelationshipEfficiencyClass,
            preset,
            "windows-processor-relationship-efficiency");
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore>? TryResolveFromIntelHybridShape(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        CpuCorePerformancePreset preset,
        ICollection<string> notes)
    {
        if (!preset.KnownHybrid)
        {
            return null;
        }

        var physicalCoreCount = cores.Count;
        var logicalProcessorCount = cores.Sum(static core => Math.Max(0, core.LogicalProcessorCount));
        var pCoreCount = logicalProcessorCount - physicalCoreCount;
        var eCoreCount = physicalCoreCount - pCoreCount;
        if (!preset.Vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
            || pCoreCount <= 0
            || eCoreCount <= 0
            || pCoreCount >= physicalCoreCount)
        {
            return null;
        }

        notes.Add($"Intel 主流异构型号预设命中；Windows 未暴露异构分级，按核心/线程数推导 {pCoreCount}P + {eCoreCount}E，P-core 在前、E-core 在后。");
        return cores
            .OrderBy(static core => core.CoreIndex)
            .Select((core, index) =>
            {
                var isPerformance = index < pCoreCount;
                return new CpuCorePerformanceScore(
                    core.CoreIndex,
                    isPerformance ? PerformanceCoreScore : EfficientCoreScore,
                    isPerformance ? "P-core" : "E-core",
                    "model-preset:intel-hybrid-thread-shape");
            })
            .ToDictionary(static score => score.CoreIndex);
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore> CreateEfficiencyClassScores(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        Func<CpuCorePerformanceInput, int> resolveClass,
        CpuCorePerformancePreset preset,
        string source)
    {
        var values = cores.Select(resolveClass).ToArray();
        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(1, max - min);
        return cores
            .Select(core =>
            {
                var value = resolveClass(core);
                var normalized = (value - min) / (double)range;
                var score = Math.Round(50 + normalized * 50, 2);
                var isTop = value == max;
                var isBottom = value == min;
                var performanceClass = preset.Vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
                    ? isTop ? "P-core" : isBottom ? "E-core" : "Middle-core"
                    : isTop ? "High-efficiency-class" : isBottom ? "Low-efficiency-class" : "Middle-efficiency-class";
                return new CpuCorePerformanceScore(core.CoreIndex, score, performanceClass, source);
            })
            .ToDictionary(static score => score.CoreIndex);
    }

    private static IReadOnlyDictionary<int, CpuCorePerformanceScore> CreateUniformScores(
        IReadOnlyList<CpuCorePerformanceInput> cores,
        string source,
        double performanceScore = PerformanceCoreScore)
    {
        return cores
            .Select(core => new CpuCorePerformanceScore(
                core.CoreIndex,
                performanceScore,
                "Homogeneous-core",
                source))
            .ToDictionary(static score => score.CoreIndex);
    }

    private static bool TryClassifyIntelCoreUltraDesktop(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var match = Regex.Match(
            cpuName,
            @"\bCore(?:\(TM\))?\s*Ultra\s*(?<tier>[579])(?:\s*Processor)?\s*(?<model>\d{3})(?<suffix>[A-Z]*)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            preset = default!;
            return false;
        }

        var model = int.Parse(match.Groups["model"].Value);
        var suffix = match.Groups["suffix"].Value.ToUpperInvariant();
        if (suffix is not ("" or "K" or "KF" or "F" or "T"))
        {
            preset = default!;
            return false;
        }

        preset = model switch
        {
            285 => IntelHybridPreset("Intel Core Ultra 9 285 desktop", "model-preset:intel-core-ultra-200s-285", 8, 16, performanceCoreHasSmt: false),
            270 => IntelHybridPreset("Intel Core Ultra 7 270 desktop Plus", "model-preset:intel-core-ultra-200s-270-plus", 8, 16, performanceCoreHasSmt: false),
            265 => IntelHybridPreset("Intel Core Ultra 7 265 desktop", "model-preset:intel-core-ultra-200s-265", 8, 12, performanceCoreHasSmt: false),
            250 => IntelHybridPreset("Intel Core Ultra 5 250 desktop Plus", "model-preset:intel-core-ultra-200s-250-plus", 6, 12, performanceCoreHasSmt: false),
            245 => IntelHybridPreset("Intel Core Ultra 5 245 desktop", "model-preset:intel-core-ultra-200s-245", 6, 8, performanceCoreHasSmt: false),
            _ => new CpuCorePerformancePreset(
                "Intel",
                "Intel Core Ultra hybrid CPU",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-ultra-generic")
        };
        return true;
    }

    private static bool TryClassifyIntelCoreUltraMobile(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var match = Regex.Match(
            cpuName,
            @"\bCore(?:\(TM\))?\s*Ultra\s*(?<tier>[579])(?:\s*Processor)?\s*(?<model>\d{3})(?<suffix>[A-Z]*)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            preset = default!;
            return false;
        }

        var tier = int.Parse(match.Groups["tier"].Value);
        var model = int.Parse(match.Groups["model"].Value);
        var suffix = match.Groups["suffix"].Value.ToUpperInvariant();
        var series = model / 100;
        var sku = model % 100;

        preset = suffix switch
        {
            "HX" when series == 2 => ClassifyIntelCoreUltraSeries2Hx(tier, model),
            "H" when series == 1 => ClassifyIntelCoreUltraSeries1H(tier, sku),
            "U" when series == 1 => ClassifyIntelCoreUltraSeries1U(sku),
            "H" when series == 2 => ClassifyIntelCoreUltraSeries2H(tier),
            "V" when series == 2 => IntelHybridPreset(
                $"Intel Core Ultra {tier} {model}V mobile",
                $"model-preset:intel-core-ultra-series2-{model}v",
                4,
                0,
                lowPowerEfficientCoreCount: 4,
                performanceCoreHasSmt: false),
            _ => new CpuCorePerformancePreset(
                "Intel",
                "Intel Core Ultra mobile hybrid CPU",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-ultra-mobile-generic")
        };
        return true;
    }

    private static bool TryClassifyIntelCoreSeriesMobile(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var match = Regex.Match(
            cpuName,
            @"\bCore(?:\(TM\))?\s*(?<tier>[3579])(?:\s*Processor)?\s*(?<model>\d{3})(?<suffix>[A-Z]*)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            preset = default!;
            return false;
        }

        var tier = int.Parse(match.Groups["tier"].Value);
        var model = int.Parse(match.Groups["model"].Value);
        var suffix = match.Groups["suffix"].Value.ToUpperInvariant();
        if (suffix is not ("H" or "U"))
        {
            preset = default!;
            return false;
        }

        preset = suffix switch
        {
            "H" => ClassifyIntelCoreSeriesH(tier, model),
            "U" => ClassifyIntelCoreSeriesU(tier, model),
            _ => new CpuCorePerformancePreset(
                "Intel",
                "Intel Core Series mobile hybrid CPU",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-series-mobile-generic")
        };
        return true;
    }

    private static bool TryClassifyIntelNSeries(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var match = Regex.Match(
            cpuName,
            @"\b(?:Core(?:\(TM\))?\s*i?(?:3|5|7)?[-\s]*|Processor\s*)N(?<model>\d{2,3})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            preset = default!;
            return false;
        }

        var model = int.Parse(match.Groups["model"].Value);
        var coreCount = model >= 300 ? 8 : model >= 100 ? 4 : 2;
        preset = new CpuCorePerformancePreset(
            "Intel",
            "Intel N-series low-power mobile homogeneous CPU",
            KnownHybrid: false,
            KnownHomogeneous: true,
            KnownX3d: false,
            $"model-preset:intel-n-series-n{model}",
            ExpectedPerformanceCoreCount: coreCount,
            ExpectedEfficientCoreCount: 0,
            ExpectedLowPowerEfficientCoreCount: 0,
            PerformanceCoreHasSmt: false,
            EfficientCoreHasSmt: false,
            LowPowerEfficientCoreHasSmt: false,
            PerformanceCoreScore: PerformanceCoreScore,
            EfficientCoreScore: EfficientCoreScore,
            LowPowerEfficientCoreScore: LowPowerEfficientCoreScore,
            PerformanceCoreClassName: "Intel-N-core");
        return true;
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreUltraSeries1H(int tier, int sku)
    {
        return tier >= 7 || sku >= 50
            ? IntelHybridPreset("Intel Core Ultra Series 1 H mobile", "model-preset:intel-core-ultra-series1-h-high", 6, 8, lowPowerEfficientCoreCount: 2)
            : IntelHybridPreset("Intel Core Ultra Series 1 H mobile", "model-preset:intel-core-ultra-series1-h-mid", 4, 8, lowPowerEfficientCoreCount: 2);
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreUltraSeries1U(int sku)
    {
        return sku >= 25
            ? IntelHybridPreset("Intel Core Ultra Series 1 U mobile", "model-preset:intel-core-ultra-series1-u-12c", 2, 8, lowPowerEfficientCoreCount: 2)
            : IntelHybridPreset("Intel Core Ultra Series 1 U mobile", "model-preset:intel-core-ultra-series1-u-8c", 2, 4, lowPowerEfficientCoreCount: 2);
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreUltraSeries2H(int tier)
    {
        return tier >= 7
            ? IntelHybridPreset(
                "Intel Core Ultra Series 2 H mobile",
                "model-preset:intel-core-ultra-series2-h-high",
                6,
                8,
                lowPowerEfficientCoreCount: 2,
                performanceCoreHasSmt: false)
            : IntelHybridPreset(
                "Intel Core Ultra Series 2 H mobile",
                "model-preset:intel-core-ultra-series2-h-mid",
                4,
                8,
                lowPowerEfficientCoreCount: 2,
                performanceCoreHasSmt: false);
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreUltraSeries2Hx(int tier, int model)
    {
        if (tier >= 9 || model >= 275)
        {
            return IntelHybridPreset(
                $"Intel Core Ultra {tier} {model}HX mobile",
                $"model-preset:intel-core-ultra-series2-{model}hx-high",
                8,
                16,
                performanceCoreHasSmt: false);
        }

        if (model >= 255)
        {
            return IntelHybridPreset(
                $"Intel Core Ultra {tier} {model}HX mobile",
                $"model-preset:intel-core-ultra-series2-{model}hx-mid",
                8,
                12,
                performanceCoreHasSmt: false);
        }

        return IntelHybridPreset(
            $"Intel Core Ultra {tier} {model}HX mobile",
            $"model-preset:intel-core-ultra-series2-{model}hx-entry",
            6,
            8,
            performanceCoreHasSmt: false);
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreSeriesH(int tier, int model)
    {
        if (model >= 250)
        {
            return IntelHybridPreset($"Intel Core {tier} {model}H mobile", $"model-preset:intel-core-series-{model}h-14c", 6, 8);
        }

        if (model >= 240)
        {
            return IntelHybridPreset($"Intel Core {tier} {model}H mobile", $"model-preset:intel-core-series-{model}h-10c", 6, 4);
        }

        if (model >= 220)
        {
            return IntelHybridPreset($"Intel Core {tier} {model}H mobile", $"model-preset:intel-core-series-{model}h-12c", 4, 8);
        }

        if (model >= 210)
        {
            return IntelHybridPreset($"Intel Core {tier} {model}H mobile", $"model-preset:intel-core-series-{model}h-8c", 4, 4);
        }

        return new CpuCorePerformancePreset(
            "Intel",
            "Intel Core Series H mobile hybrid CPU",
            KnownHybrid: true,
            KnownHomogeneous: false,
            KnownX3d: false,
            "model-preset:intel-core-series-h-generic");
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreSeriesU(int tier, int model)
    {
        return model >= 120 || tier >= 5
            ? IntelHybridPreset($"Intel Core {tier} {model}U mobile", $"model-preset:intel-core-series-{model}u-10c", 2, 8)
            : IntelHybridPreset($"Intel Core {tier} {model}U mobile", $"model-preset:intel-core-series-{model}u-6c", 2, 4);
    }

    private static bool TryClassifyIntelCoreLegacy(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var match = Regex.Match(
            cpuName,
            @"\bi(?<tier>[3579])[-\s]*(?<model>\d{4,5})(?<suffix>[A-Z0-9]*)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            preset = default!;
            return false;
        }

        var tier = int.Parse(match.Groups["tier"].Value);
        var model = int.Parse(match.Groups["model"].Value);
        var suffix = match.Groups["suffix"].Value.ToUpperInvariant();
        var (generation, sku) = ParseIntelCoreGenerationAndSku(model);
        if (IsIntelMobileCoreSuffix(suffix))
        {
            preset = ClassifyIntelCoreMobile(generation, tier, sku, suffix);
            return true;
        }

        preset = generation switch
        {
            14 => ClassifyIntelCore14thDesktop(tier, sku),
            13 => ClassifyIntelCore13thDesktop(tier, sku),
            12 => ClassifyIntelCore12thDesktop(tier, sku, suffix),
            _ => new CpuCorePerformancePreset(
                "Intel",
                "Intel Core desktop homogeneous or unknown-hybrid",
                KnownHybrid: false,
                KnownHomogeneous: true,
                KnownX3d: false,
                "model-preset:intel-core-default")
        };
        return true;
    }

    private static (int Generation, int Sku) ParseIntelCoreGenerationAndSku(int model)
    {
        if (model >= 10000)
        {
            return (model / 1000, model % 1000);
        }

        if (model >= 8000)
        {
            return (model / 1000, model % 1000);
        }

        return (model / 100, model % 100);
    }

    private static CpuCorePerformancePreset ClassifyIntelCoreMobile(
        int generation,
        int tier,
        int sku,
        string suffix)
    {
        return generation switch
        {
            14 => ClassifyIntelCore14thMobile(tier, sku, suffix),
            13 => ClassifyIntelCore13thMobile(tier, sku, suffix),
            12 => ClassifyIntelCore12thMobile(tier, sku, suffix),
            >= 8 and <= 11 => IntelHomogeneousPreset(
                $"Intel Core {generation}th Gen mobile homogeneous CPU",
                $"model-preset:intel-core-{generation}th-mobile-homogeneous"),
            _ => IntelHomogeneousPreset(
                "Intel Core mobile homogeneous or unknown-hybrid",
                "model-preset:intel-core-mobile-default")
        };
    }

    private static CpuCorePerformancePreset ClassifyIntelCore14thMobile(
        int tier,
        int sku,
        string suffix)
    {
        if (!suffix.Contains("HX", StringComparison.OrdinalIgnoreCase))
        {
            return new CpuCorePerformancePreset(
                "Intel",
                "Intel Core 14th Gen mobile hybrid candidate",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-14th-mobile-generic");
        }

        return tier switch
        {
            9 => IntelHybridPreset("Intel Core i9 14th Gen HX mobile", "model-preset:intel-core-14th-hx-i9", 8, 16),
            7 when sku >= 700 => IntelHybridPreset("Intel Core i7 14th Gen HX mobile", "model-preset:intel-core-14th-hx-i7-high", 8, 12),
            7 when sku >= 650 => IntelHybridPreset("Intel Core i7 14th Gen HX mobile", "model-preset:intel-core-14th-hx-i7-mid", 8, 8),
            5 when sku >= 500 => IntelHybridPreset("Intel Core i5 14th Gen HX mobile", "model-preset:intel-core-14th-hx-i5-high", 6, 8),
            5 => IntelHybridPreset("Intel Core i5 14th Gen HX mobile", "model-preset:intel-core-14th-hx-i5-mid", 6, 4),
            _ => new CpuCorePerformancePreset(
                "Intel",
                "Intel Core 14th Gen HX mobile hybrid candidate",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:intel-core-14th-hx-generic")
        };
    }

    private static CpuCorePerformancePreset ClassifyIntelCore13thMobile(
        int tier,
        int sku,
        string suffix)
    {
        if (suffix.Contains("HX", StringComparison.OrdinalIgnoreCase))
        {
            return tier switch
            {
                9 => IntelHybridPreset("Intel Core i9 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i9", 8, 16),
                7 when sku >= 850 => IntelHybridPreset("Intel Core i7 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i7-13850", 8, 12),
                7 when sku >= 700 => IntelHybridPreset("Intel Core i7 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i7-13700", 8, 8),
                7 when sku >= 650 => IntelHybridPreset("Intel Core i7 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i7-13650", 6, 8),
                5 when sku >= 500 => IntelHybridPreset("Intel Core i5 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i5-high", 6, 8),
                5 => IntelHybridPreset("Intel Core i5 13th Gen HX mobile", "model-preset:intel-core-13th-hx-i5-mid", 6, 4),
                _ => new CpuCorePerformancePreset(
                    "Intel",
                    "Intel Core 13th Gen HX mobile hybrid candidate",
                    KnownHybrid: true,
                    KnownHomogeneous: false,
                    KnownX3d: false,
                    "model-preset:intel-core-13th-hx-generic")
            };
        }

        if (suffix == "H" || suffix.Contains("HK", StringComparison.OrdinalIgnoreCase))
        {
            return tier switch
            {
                >= 7 => IntelHybridPreset("Intel Core 13th Gen H mobile", "model-preset:intel-core-13th-h-i7-i9", 6, 8),
                5 => IntelHybridPreset("Intel Core i5 13th Gen H mobile", "model-preset:intel-core-13th-h-i5", 4, 8),
                _ => IntelHybridPreset("Intel Core 13th Gen H mobile", "model-preset:intel-core-13th-h-low", 2, 4)
            };
        }

        if (suffix == "P")
        {
            return tier == 7 && sku >= 70
                ? IntelHybridPreset("Intel Core i7 13th Gen P mobile", "model-preset:intel-core-13th-p-i7-1370", 6, 8)
                : IntelHybridPreset("Intel Core 13th Gen P mobile", "model-preset:intel-core-13th-p-mainstream", tier >= 5 ? 4 : 2, tier >= 5 ? 8 : 4);
        }

        if (suffix == "U")
        {
            return IntelHybridPreset("Intel Core 13th Gen U mobile", "model-preset:intel-core-13th-u", 2, tier >= 5 ? 8 : 4);
        }

        return new CpuCorePerformancePreset(
            "Intel",
            "Intel Core 13th Gen mobile hybrid candidate",
            KnownHybrid: true,
            KnownHomogeneous: false,
            KnownX3d: false,
            "model-preset:intel-core-13th-mobile-generic");
    }

    private static CpuCorePerformancePreset ClassifyIntelCore12thMobile(
        int tier,
        int sku,
        string suffix)
    {
        if (suffix.Contains("HX", StringComparison.OrdinalIgnoreCase))
        {
            return tier switch
            {
                9 => IntelHybridPreset("Intel Core i9 12th Gen HX mobile", "model-preset:intel-core-12th-hx-i9", 8, 8),
                7 when sku >= 800 => IntelHybridPreset("Intel Core i7 12th Gen HX mobile", "model-preset:intel-core-12th-hx-i7-high", 8, 8),
                7 when sku >= 600 => IntelHybridPreset("Intel Core i7 12th Gen HX mobile", "model-preset:intel-core-12th-hx-i7-mid", 6, 8),
                5 when sku >= 600 => IntelHybridPreset("Intel Core i5 12th Gen HX mobile", "model-preset:intel-core-12th-hx-i5-high", 4, 8),
                5 => IntelHybridPreset("Intel Core i5 12th Gen HX mobile", "model-preset:intel-core-12th-hx-i5-mid", 4, 4),
                _ => new CpuCorePerformancePreset(
                    "Intel",
                    "Intel Core 12th Gen HX mobile hybrid candidate",
                    KnownHybrid: true,
                    KnownHomogeneous: false,
                    KnownX3d: false,
                    "model-preset:intel-core-12th-hx-generic")
            };
        }

        if (suffix == "H" || suffix.Contains("HK", StringComparison.OrdinalIgnoreCase))
        {
            return tier switch
            {
                >= 7 => IntelHybridPreset("Intel Core 12th Gen H mobile", "model-preset:intel-core-12th-h-i7-i9", 6, 8),
                5 when sku >= 500 => IntelHybridPreset("Intel Core i5 12th Gen H mobile", "model-preset:intel-core-12th-h-i5-mainstream", 4, 8),
                5 => IntelHybridPreset("Intel Core i5 12th Gen H mobile", "model-preset:intel-core-12th-h-i5-low", 4, 4),
                _ => IntelHybridPreset("Intel Core 12th Gen H mobile", "model-preset:intel-core-12th-h-low", 2, 4)
            };
        }

        if (suffix == "P")
        {
            return tier == 7 && sku >= 80
                ? IntelHybridPreset("Intel Core i7 12th Gen P mobile", "model-preset:intel-core-12th-p-i7-1280", 6, 8)
                : IntelHybridPreset("Intel Core 12th Gen P mobile", "model-preset:intel-core-12th-p-mainstream", tier >= 5 ? 4 : 2, tier >= 5 ? 8 : 4);
        }

        if (suffix == "U")
        {
            return IntelHybridPreset("Intel Core 12th Gen U mobile", "model-preset:intel-core-12th-u", 2, tier >= 5 ? 8 : 4);
        }

        return new CpuCorePerformancePreset(
            "Intel",
            "Intel Core 12th Gen mobile hybrid candidate",
            KnownHybrid: true,
            KnownHomogeneous: false,
            KnownX3d: false,
            "model-preset:intel-core-12th-mobile-generic");
    }

    private static CpuCorePerformancePreset ClassifyIntelCore14thDesktop(int tier, int sku)
    {
        return tier switch
        {
            9 when sku >= 900 => IntelHybridPreset("Intel Core i9 14th Gen desktop", "model-preset:intel-core-14th-i9", 8, 16),
            7 when sku >= 700 => IntelHybridPreset("Intel Core i7 14th Gen desktop", "model-preset:intel-core-14th-i7", 8, 12),
            5 when sku >= 600 => IntelHybridPreset("Intel Core i5 14600 desktop", "model-preset:intel-core-14th-i5-14600", 6, 8),
            5 when sku >= 500 => IntelHybridPreset("Intel Core i5 14500 desktop", "model-preset:intel-core-14th-i5-14500", 6, 8),
            5 when sku >= 400 => IntelHybridPreset("Intel Core i5 14400 desktop", "model-preset:intel-core-14th-i5-14400", 6, 4),
            _ => IntelHomogeneousPreset("Intel Core 14th Gen homogeneous desktop", "model-preset:intel-core-14th-homogeneous")
        };
    }

    private static CpuCorePerformancePreset ClassifyIntelCore13thDesktop(int tier, int sku)
    {
        return tier switch
        {
            9 when sku >= 900 => IntelHybridPreset("Intel Core i9 13th Gen desktop", "model-preset:intel-core-13th-i9", 8, 16),
            7 when sku >= 700 => IntelHybridPreset("Intel Core i7 13th Gen desktop", "model-preset:intel-core-13th-i7", 8, 8),
            5 when sku >= 600 => IntelHybridPreset("Intel Core i5 13600 desktop", "model-preset:intel-core-13th-i5-13600", 6, 8),
            5 when sku >= 500 => IntelHybridPreset("Intel Core i5 13500 desktop", "model-preset:intel-core-13th-i5-13500", 6, 8),
            5 when sku >= 400 => IntelHybridPreset("Intel Core i5 13400 desktop", "model-preset:intel-core-13th-i5-13400", 6, 4),
            _ => IntelHomogeneousPreset("Intel Core 13th Gen homogeneous desktop", "model-preset:intel-core-13th-homogeneous")
        };
    }

    private static CpuCorePerformancePreset ClassifyIntelCore12thDesktop(
        int tier,
        int sku,
        string suffix)
    {
        return tier switch
        {
            9 when sku >= 900 => IntelHybridPreset("Intel Core i9 12th Gen desktop", "model-preset:intel-core-12th-i9", 8, 8),
            7 when sku >= 700 => IntelHybridPreset("Intel Core i7 12th Gen desktop", "model-preset:intel-core-12th-i7", 8, 4),
            5 when sku >= 600 && suffix.Contains('K') => IntelHybridPreset("Intel Core i5 12600K desktop", "model-preset:intel-core-12th-i5-12600k", 6, 4),
            _ => IntelHomogeneousPreset("Intel Core 12th Gen homogeneous desktop", "model-preset:intel-core-12th-homogeneous")
        };
    }

    private static CpuCorePerformancePreset IntelHybridPreset(
        string family,
        string source,
        int performanceCoreCount,
        int efficientCoreCount,
        int lowPowerEfficientCoreCount = 0,
        bool performanceCoreHasSmt = true,
        bool efficientCoreHasSmt = false,
        bool lowPowerEfficientCoreHasSmt = false,
        double efficientCoreScore = EfficientCoreScore,
        double lowPowerEfficientCoreScore = LowPowerEfficientCoreScore)
    {
        return new CpuCorePerformancePreset(
            "Intel",
            family,
            KnownHybrid: true,
            KnownHomogeneous: false,
            KnownX3d: false,
            source,
            performanceCoreCount,
            efficientCoreCount,
            lowPowerEfficientCoreCount,
            performanceCoreHasSmt,
            efficientCoreHasSmt,
            lowPowerEfficientCoreHasSmt,
            PerformanceCoreScore,
            efficientCoreScore,
            lowPowerEfficientCoreScore);
    }

    private static bool TryClassifyAmdRyzenAi(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var ryzenAiMax = Regex.Match(
            cpuName,
            @"\bRyzen(?:\W|TM)*AI(?:\W|TM)*Max\+?(?:\s*PRO)?\s*(?<model>\d{3})\b",
            RegexOptions.IgnoreCase);
        if (ryzenAiMax.Success)
        {
            preset = new CpuCorePerformancePreset(
                "AMD",
                "AMD Ryzen AI Max homogeneous Zen CPU",
                KnownHybrid: false,
                KnownHomogeneous: true,
                KnownX3d: false,
                "model-preset:amd-ryzen-ai-max-zen");
            return true;
        }

        var ryzenAi = Regex.Match(
            cpuName,
            @"\bRyzen(?:\W|TM)*AI\s*(?<tier>[579])(?:(?:\s*HX|\s*H|\s*PRO))*\s*(?<model>\d{3})\b",
            RegexOptions.IgnoreCase);
        if (!ryzenAi.Success)
        {
            preset = default!;
            return false;
        }

        var model = int.Parse(ryzenAi.Groups["model"].Value);
        preset = model switch
        {
            475 or 470 => AmdZen5Zen5cPreset($"AMD Ryzen AI {ryzenAi.Groups["tier"].Value} HX {model}", $"model-preset:amd-ryzen-ai-{model}", 4, 8),
            450 => AmdZen5Zen5cPreset("AMD Ryzen AI 7 450", "model-preset:amd-ryzen-ai-7-450", 4, 4),
            435 => AmdZen5Zen5cPreset("AMD Ryzen AI 5 435", "model-preset:amd-ryzen-ai-5-435", 2, 4),
            370 => AmdZen5Zen5cPreset("AMD Ryzen AI 9 HX 370", "model-preset:amd-ryzen-ai-9-hx-370", 4, 8),
            365 => AmdZen5Zen5cPreset("AMD Ryzen AI 9 H/HX 365", "model-preset:amd-ryzen-ai-9-365", 4, 6),
            350 => AmdZen5Zen5cPreset("AMD Ryzen AI 7 350", "model-preset:amd-ryzen-ai-7-350", 4, 4),
            340 => AmdZen5Zen5cPreset("AMD Ryzen AI 5 340", "model-preset:amd-ryzen-ai-5-340", 3, 3),
            _ => new CpuCorePerformancePreset(
                "AMD",
                "AMD Ryzen AI mixed Zen CPU",
                KnownHybrid: true,
                KnownHomogeneous: false,
                KnownX3d: false,
                "model-preset:amd-ryzen-ai-generic")
        };
        return true;
    }

    private static bool TryClassifyAmdRyzenZSeries(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var zSeries = Regex.Match(
            cpuName,
            @"\bRyzen(?:\W|TM)*(?:AI\s*)?Z(?<series>[12])(?:\s*(?<variant>Extreme|Go|A))?\b",
            RegexOptions.IgnoreCase);
        if (!zSeries.Success)
        {
            preset = default!;
            return false;
        }

        var series = int.Parse(zSeries.Groups["series"].Value);
        var variant = zSeries.Groups["variant"].Success
            ? zSeries.Groups["variant"].Value.ToUpperInvariant()
            : string.Empty;

        preset = (series, variant) switch
        {
            (2, "EXTREME") when cpuName.Contains("AI", StringComparison.OrdinalIgnoreCase) =>
                AmdZen5Zen5cPreset("AMD Ryzen AI Z2 Extreme handheld", "model-preset:amd-ryzen-ai-z2-extreme", 3, 5),
            (2, "EXTREME") => AmdZen5Zen5cPreset("AMD Ryzen Z2 Extreme handheld", "model-preset:amd-ryzen-z2-extreme", 3, 5),
            (2, "GO") => AmdHomogeneousZenPreset("AMD Ryzen Z2 Go handheld", "model-preset:amd-ryzen-z2-go", 4),
            (2, _) => AmdHomogeneousZenPreset("AMD Ryzen Z2 handheld", "model-preset:amd-ryzen-z2", 8),
            (1, "EXTREME") => AmdHomogeneousZenPreset("AMD Ryzen Z1 Extreme handheld", "model-preset:amd-ryzen-z1-extreme", 8),
            (1, _) => AmdHomogeneousZenPreset("AMD Ryzen Z1 handheld", "model-preset:amd-ryzen-z1", 6),
            _ => AmdHomogeneousZenPreset("AMD Ryzen Z handheld", "model-preset:amd-ryzen-z")
        };
        return true;
    }

    private static bool TryClassifyAmdRyzenMobile(
        string cpuName,
        out CpuCorePerformancePreset preset)
    {
        var ryzenMobile = Regex.Match(
            cpuName,
            @"\bRyzen(?:\W|TM)*?(?<tier>[3579])(?:\s*PRO)?\s*(?<model>\d{4})(?<suffix>HX3D|HX|HS|H|U|C|E)\b",
            RegexOptions.IgnoreCase);
        if (!ryzenMobile.Success)
        {
            preset = default!;
            return false;
        }

        var tier = int.Parse(ryzenMobile.Groups["tier"].Value);
        var model = int.Parse(ryzenMobile.Groups["model"].Value);
        var suffix = ryzenMobile.Groups["suffix"].Value.ToUpperInvariant();
        var x3d = suffix.Contains("3D", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        var cleanSuffix = suffix.Replace("3D", string.Empty, StringComparison.OrdinalIgnoreCase);
        var coreCount = ResolveAmdRyzenMobileCoreCount(tier, model, cleanSuffix);

        preset = AmdHomogeneousZenPreset(
            x3d
                ? $"AMD Ryzen mobile {cleanSuffix} X3D CCD-role Zen CPU"
                : $"AMD Ryzen mobile {cleanSuffix} homogeneous Zen CPU",
            x3d
                ? $"model-preset:amd-ryzen-mobile-{model}{suffix.ToLowerInvariant()}-x3d"
                : $"model-preset:amd-ryzen-mobile-{model}{suffix.ToLowerInvariant()}",
            coreCount,
            knownX3d: x3d);
        return true;
    }

    private static int? ResolveAmdRyzenMobileCoreCount(
        int tier,
        int model,
        string suffix)
    {
        if (suffix == "HX")
        {
            return model switch
            {
                9955 or 8945 or 8940 or 7945 => 16,
                9850 or 7845 => 12,
                9845 or 7745 => 8,
                7645 => 6,
                _ => ResolveAmdRyzenHxCoreCountByTierAndModel(tier, model)
            };
        }

        if (suffix is "H" or "HS" or "U" or "C" or "E")
        {
            return ResolveAmdRyzenMainstreamMobileCoreCount(tier, model);
        }

        return null;
    }

    private static int ResolveAmdRyzenHxCoreCountByTierAndModel(
        int tier,
        int model)
    {
        var marketDigit = model / 100 % 10;
        return tier switch
        {
            >= 9 when marketDigit >= 9 => 16,
            >= 9 => 12,
            7 => 8,
            5 => 6,
            _ => 4
        };
    }

    private static int ResolveAmdRyzenMainstreamMobileCoreCount(
        int tier,
        int model)
    {
        var marketDigit = model / 100 % 10;
        if (model is >= 7020 and < 7030)
        {
            return tier >= 5 ? 4 : 2;
        }

        return tier switch
        {
            >= 7 => 8,
            5 when marketDigit >= 4 => 6,
            5 => 4,
            3 => 4,
            _ => 4
        };
    }

    private static CpuCorePerformancePreset AmdZen5Zen5cPreset(
        string family,
        string source,
        int zen5CoreCount,
        int zen5cCoreCount)
    {
        return new CpuCorePerformancePreset(
            "AMD",
            family,
            KnownHybrid: true,
            KnownHomogeneous: false,
            KnownX3d: false,
            source,
            ExpectedPerformanceCoreCount: zen5CoreCount,
            ExpectedEfficientCoreCount: zen5cCoreCount,
            ExpectedLowPowerEfficientCoreCount: 0,
            PerformanceCoreHasSmt: true,
            EfficientCoreHasSmt: true,
            LowPowerEfficientCoreHasSmt: false,
            PerformanceCoreScore: PerformanceCoreScore,
            EfficientCoreScore: 82,
            LowPowerEfficientCoreScore: LowPowerEfficientCoreScore,
            PerformanceCoreClassName: "Zen5-core",
            EfficientCoreClassName: "Zen5c-core",
            LowPowerEfficientCoreClassName: "Low-power-core");
    }

    private static CpuCorePerformancePreset AmdHomogeneousZenPreset(
        string family,
        string source,
        int? expectedCoreCount = null,
        bool knownX3d = false)
    {
        if (!expectedCoreCount.HasValue)
        {
            return new CpuCorePerformancePreset(
                "AMD",
                family,
                KnownHybrid: false,
                KnownHomogeneous: true,
                KnownX3d: knownX3d,
                source,
                PerformanceCoreScore: PerformanceCoreScore,
                EfficientCoreScore: EfficientCoreScore,
                LowPowerEfficientCoreScore: LowPowerEfficientCoreScore,
                PerformanceCoreClassName: "Zen-core",
                EfficientCoreClassName: "Zen-efficiency-class",
                LowPowerEfficientCoreClassName: "Zen-low-power-class");
        }

        return new CpuCorePerformancePreset(
            "AMD",
            family,
            KnownHybrid: false,
            KnownHomogeneous: true,
            KnownX3d: knownX3d,
            source,
            ExpectedPerformanceCoreCount: expectedCoreCount,
            ExpectedEfficientCoreCount: 0,
            ExpectedLowPowerEfficientCoreCount: 0,
            PerformanceCoreHasSmt: true,
            EfficientCoreHasSmt: false,
            LowPowerEfficientCoreHasSmt: false,
            PerformanceCoreScore: PerformanceCoreScore,
            EfficientCoreScore: EfficientCoreScore,
            LowPowerEfficientCoreScore: LowPowerEfficientCoreScore,
            PerformanceCoreClassName: "Zen-core",
            EfficientCoreClassName: "Zen-efficiency-class",
            LowPowerEfficientCoreClassName: "Zen-low-power-class");
    }

    private static CpuCorePerformancePreset IntelHomogeneousPreset(
        string family,
        string source)
    {
        return new CpuCorePerformancePreset(
            "Intel",
            family,
            KnownHybrid: false,
            KnownHomogeneous: true,
            KnownX3d: false,
            source);
    }

    private static string FormatExpectedLayout(CpuCorePerformancePreset preset)
    {
        var parts = new List<string>();
        if (preset.ExpectedPerformanceCoreCount is > 0)
        {
            parts.Add(preset.PerformanceCoreClassName == "P-core"
                ? $"{preset.ExpectedPerformanceCoreCount}P"
                : $"{preset.ExpectedPerformanceCoreCount} {preset.PerformanceCoreClassName}");
        }

        if (preset.ExpectedEfficientCoreCount is > 0)
        {
            parts.Add(preset.EfficientCoreClassName == "E-core"
                ? $"{preset.ExpectedEfficientCoreCount}E"
                : $"{preset.ExpectedEfficientCoreCount} {preset.EfficientCoreClassName}");
        }

        if (preset.ExpectedLowPowerEfficientCoreCount is > 0)
        {
            parts.Add($"{preset.ExpectedLowPowerEfficientCoreCount}LP-E");
        }

        return parts.Count == 0 ? "同构" : string.Join(" + ", parts);
    }

    private static bool IsIntelCoreUltra(string cpuName)
    {
        return Regex.IsMatch(
            cpuName,
            @"\bCore(?:\(TM\))?\s*Ultra\b",
            RegexOptions.IgnoreCase);
    }

    private static bool IsIntelMobileCoreSuffix(string suffix)
    {
        return suffix.Contains('H')
            || suffix is "P" or "U" or "Y"
            || suffix.StartsWith("G", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDiverseValues(IEnumerable<int?> values)
    {
        var concrete = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        return concrete.Length > 1;
    }

    private static string NormalizeCpuName(string cpuName)
    {
        return string.IsNullOrWhiteSpace(cpuName)
            ? string.Empty
            : cpuName.Trim();
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
