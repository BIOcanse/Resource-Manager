using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader(
    ICpuCorePerformanceOverrideStore performanceOverrideStore) : ICpuTopologySampler
{
    private readonly object usageGate = new();
    private IReadOnlyList<ProcessorTimes>? previousTimes;

    public CpuTopologySnapshot CaptureSnapshot() => Capture(includeUsage: true);

    public CpuTopologySnapshot CaptureTopology() => Capture(includeUsage: false);

    private CpuTopologySnapshot Capture(bool includeUsage)
    {
        var notes = new List<string>();
        var cpuSpecification = ResolveCpuSpecification();
        var cpuName = cpuSpecification.Name;
        var cpuPreset = CpuCorePerformancePresetResolver.Classify(cpuName);
        var coreRecords = ReadPhysicalCoreRecords(notes);
        var cpuSetRecords = ReadCpuSetRecords(notes);
        var cacheRecords = ReadCacheRecords(notes);
        var logicalProcessorCount = Math.Max(
            Environment.ProcessorCount,
            coreRecords.SelectMany(static core => core.LogicalProcessors).Select(static logical => logical.GlobalId).DefaultIfEmpty(-1).Max() + 1);
        var usageValues = includeUsage
            ? ReadLogicalProcessorUsage(logicalProcessorCount, notes)
            : new double?[logicalProcessorCount];
        var ccdRecords = ReadCcdRecords(cpuName, coreRecords, logicalProcessorCount, cpuSetRecords, notes);
        var performanceScores = CpuCorePerformancePresetResolver.Resolve(
            cpuName,
            CreatePerformanceInputs(coreRecords, cpuSetRecords));
        notes.AddRange(performanceScores.Notes);
        var performanceOverrides = performanceOverrideStore.LoadScores(cpuName);
        var physicalCores = new List<CpuPhysicalCoreModel>();
        var logicalProcessors = new List<CpuLogicalProcessorModel>();
        var cpuSetByLogicalProcessorId = cpuSetRecords
            .GroupBy(static item => item.GlobalLogicalProcessorId)
            .ToDictionary(static group => group.Key, static group => group.First());

        foreach (var core in coreRecords.OrderBy(static core => core.Index))
        {
            var ccd = ccdRecords.FirstOrDefault(record => core.LogicalProcessors.Any(logical => record.LogicalProcessorIds.Contains(logical.GlobalId)))
                ?? ccdRecords[0];
            var performanceScore = ResolvePerformanceScore(
                core.Index,
                performanceScores,
                performanceOverrides);
            var usagePercent = CpuTopologyMath.AverageUsage(core.LogicalProcessors.Select(logical => TryGetUsage(usageValues, logical.GlobalId)));
            var coreId = $"core:{core.Index}";
            physicalCores.Add(new CpuPhysicalCoreModel(
                coreId,
                core.Index,
                $"Core {core.Index}",
                ccd.Id,
                core.EfficiencyClass,
                performanceScore,
                usagePercent,
                core.LogicalProcessors.Select(static logical => logical.GlobalId).Order().ToArray(),
                CreateCoreCacheLevels(core, cacheRecords)));

            foreach (var logical in core.LogicalProcessors.OrderBy(static logical => logical.GlobalId))
            {
                cpuSetByLogicalProcessorId.TryGetValue(logical.GlobalId, out var cpuSet);
                logicalProcessors.Add(new CpuLogicalProcessorModel(
                    logical.GlobalId,
                    logical.Group,
                    logical.GroupRelativeIndex,
                    coreId,
                    ccd.Id,
                    performanceScore,
                    TryGetUsage(usageValues, logical.GlobalId),
                    AffinitySelectable: logical.Group == 0 || cpuSet is not null,
                    CpuSetId: cpuSet?.Id));
            }
        }

        var physicalCoreIndexesByCcd = physicalCores
            .GroupBy(static core => core.CcdId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static core => core.Index).Order().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var logicalIdsByCcd = logicalProcessors
            .GroupBy(static logical => logical.CcdId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static logical => logical.Id).Order().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var ccds = ccdRecords
            .OrderBy(static ccd => ccd.Index)
            .Select(ccd =>
            {
                physicalCoreIndexesByCcd.TryGetValue(ccd.Id, out var coreIndexes);
                logicalIdsByCcd.TryGetValue(ccd.Id, out var logicalIds);
                coreIndexes ??= [];
                logicalIds ??= [];
                return new CpuCcdModel(
                    ccd.Id,
                    ccd.Index,
                    $"CCD {ccd.Index}",
                    CpuTopologyMath.AverageUsage(logicalIds.Select(id => TryGetUsage(usageValues, id))),
                    coreIndexes,
                    logicalIds,
                    ccd.Source,
                    Math.Round(physicalCores
                        .Where(core => core.CcdId.Equals(ccd.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(static core => core.PerformanceScore)
                        .Where(static score => double.IsFinite(score))
                        .DefaultIfEmpty(0)
                        .Average(), 2));
            })
            .ToArray();
        var visualLayout = CpuTopologyVisualLayoutResolver.Resolve(cpuPreset, ccds.Length);

        if (coreRecords.Any(static core => core.LogicalProcessors.Count > 1))
        {
            notes.Add("物理核心占用由该核心下的逻辑处理器平均聚合。");
        }

        notes.Add("Windows 进程/线程 affinity mask 的目标是逻辑处理器 bit；选择物理核心或 CCD 时会展开成逻辑处理器集合。");

        return new CpuTopologySnapshot(
            DateTimeOffset.Now,
            cpuName,
            cpuSpecification with
            {
                Family = cpuPreset.Family,
                PhysicalCoreCount = physicalCores.Count,
                LogicalProcessorCount = logicalProcessors.Count
            },
            cpuSetRecords.Count > 0
                ? "ModelPreset + GetSystemCpuSetInformation + GetLogicalProcessorInformationEx"
                : "ModelPreset + GetLogicalProcessorInformationEx",
            !includeUsage ? "None" : usageValues.Any(static value => value.HasValue)
                ? "NtQuerySystemInformation(SystemProcessorPerformanceInformation)"
                : "WaitingForSecondSample",
            CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            visualLayout.Kind,
            visualLayout.Source,
            physicalCores.Count,
            logicalProcessors.Count,
            ccds.Length,
            physicalCores.Any(static core => core.LogicalProcessorIds.Count > 1),
            ccds,
            physicalCores.OrderBy(static core => core.Index).ToArray(),
            logicalProcessors.OrderBy(static logical => logical.Id).ToArray(),
            notes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<CacheRelationshipRecord> ReadCacheRecords(ICollection<string> notes)
    {
        try
        {
            return QueryCacheRecords();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            notes.Add($"读取 CPU cache 关系失败：{ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<CpuCoreCacheLevelModel> CreateCoreCacheLevels(
        PhysicalCoreRecord core,
        IReadOnlyList<CacheRelationshipRecord> cacheRecords)
    {
        var coreLogicalIds = core.LogicalProcessors
            .Select(static logical => logical.GlobalId)
            .ToHashSet();
        return Enumerable.Range(1, 3)
            .Select(level => CreateCoreCacheLevel(level, coreLogicalIds, cacheRecords))
            .ToArray();
    }

    private static CpuCoreCacheLevelModel CreateCoreCacheLevel(
        int level,
        IReadOnlySet<int> coreLogicalIds,
        IReadOnlyList<CacheRelationshipRecord> cacheRecords)
    {
        var matching = cacheRecords
            .Where(record => record.Level == level
                && record.LogicalProcessorIds.Any(coreLogicalIds.Contains))
            .ToArray();
        if (matching.Length == 0)
        {
            return new CpuCoreCacheLevelModel(level, null, [], "unknown");
        }

        var logicalIds = matching
            .SelectMany(static record => record.LogicalProcessorIds)
            .Distinct()
            .Order()
            .ToArray();
        var totalBytes = matching.Sum(static record => Math.Max(0, record.CacheSizeBytes));
        var sizeKb = totalBytes > 0
            ? (int)Math.Max(1, Math.Round(totalBytes / 1024d))
            : (int?)null;
        var scope = logicalIds.Length <= coreLogicalIds.Count
            ? "core"
            : "shared";
        return new CpuCoreCacheLevelModel(level, sizeKb, logicalIds, scope);
    }

    private static double ResolvePerformanceScore(
        int coreIndex,
        CpuCorePerformanceScoreResult performanceScores,
        IReadOnlyDictionary<int, double> performanceOverrides)
    {
        if (performanceOverrides.TryGetValue(coreIndex, out var overrideScore))
        {
            return overrideScore;
        }

        return performanceScores.ScoresByCoreIndex.TryGetValue(coreIndex, out var score)
            ? score.PerformanceScore
            : 100;
    }

    private static IReadOnlyList<CpuCorePerformanceInput> CreatePerformanceInputs(
        IReadOnlyList<PhysicalCoreRecord> coreRecords,
        IReadOnlyList<CpuSetRecord> cpuSetRecords)
    {
        var cpuSetsByLogicalId = cpuSetRecords
            .GroupBy(static record => record.GlobalLogicalProcessorId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        return coreRecords
            .OrderBy(static core => core.Index)
            .Select(core =>
            {
                var matchingCpuSets = core.LogicalProcessors
                    .Where(logical => cpuSetsByLogicalId.ContainsKey(logical.GlobalId))
                    .SelectMany(logical => cpuSetsByLogicalId[logical.GlobalId])
                    .ToArray();
                return new CpuCorePerformanceInput(
                    core.Index,
                    core.EfficiencyClass,
                    ResolveDominantCpuSetValue(matchingCpuSets.Select(static record => record.EfficiencyClass)),
                    ResolveDominantCpuSetValue(matchingCpuSets.Select(static record => record.SchedulingClass)),
                    core.LogicalProcessors.Count);
            })
            .ToArray();
    }

    private static int? ResolveDominantCpuSetValue(IEnumerable<int> values)
    {
        var concrete = values
            .Where(static value => value >= 0)
            .GroupBy(static value => value)
            .OrderByDescending(static group => group.Count())
            .ThenByDescending(static group => group.Key)
            .Select(static group => (int?)group.Key)
            .FirstOrDefault();
        return concrete;
    }
}
