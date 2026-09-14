using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class CpuBaselineRatioCatalog
{
    private const string ResourceName = "ResourceManager.Configuration.Cpu.baseline-defaults.json";
    private readonly CatalogDocument document;
    private readonly FrozenDictionary<string, IntelModelDefault> intelModels;

    private CpuBaselineRatioCatalog(CatalogDocument document)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.DatasetVersion)
            || !CpuBaselineRatio.IsValid(document.UnmatchedRatio)
            || !CpuBaselineRatio.IsValid(document.AmdSingleCcdRatio)
            || !CpuBaselineRatio.IsValid(document.AmdSymmetricDualCcdRatio)
            || document.IntelModels is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid CPU baseline default catalog.");
        }
        var models = new Dictionary<string, IntelModelDefault>(StringComparer.Ordinal);
        foreach (var model in document.IntelModels)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Name)
                || string.IsNullOrWhiteSpace(model.Basis) || string.IsNullOrWhiteSpace(model.Group)
                || model.PhysicalCores <= 0 || !CpuBaselineRatio.IsValid(model.Ratio)
                || !models.TryAdd(NormalizeModelName(model.Name), model))
            {
                throw new InvalidDataException("Invalid or duplicate Intel CPU baseline default.");
            }
        }
        this.document = document;
        intelModels = models.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static CpuBaselineRatioCatalog LoadEmbedded()
    {
        using var stream = typeof(CpuBaselineRatioCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The CPU baseline default catalog is missing.");
        return Read(stream);
    }

    internal static CpuBaselineRatioCatalog Read(Stream stream)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict
        }) ?? throw new InvalidDataException("The CPU baseline default catalog is empty.");
        return new CpuBaselineRatioCatalog(document);
    }

    public CpuBaselineRatioSettings Resolve(CpuTopologySnapshot topology, double? manualRatio)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (manualRatio.HasValue) CpuBaselineRatio.Validate(manualRatio.Value);

        var ratio = document.UnmatchedRatio;
        var source = "uncompensated";
        string? matchedModel = null;
        if (topology.Specification.Vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
            && intelModels.TryGetValue(NormalizeModelName(topology.CpuName), out var model)
            && model.PhysicalCores == topology.PhysicalCoreCount)
        {
            ratio = model.Ratio;
            source = $"intel:{model.Basis}";
            matchedModel = model.Name;
        }
        else if (topology.Specification.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase)
            && MatchesHomogeneousAmdLayout(topology)
            && HasCompleteSymmetricCcdLayout(topology))
        {
            ratio = topology.CcdCount == 1 ? document.AmdSingleCcdRatio : document.AmdSymmetricDualCcdRatio;
            source = topology.CcdCount == 1 ? "amd:single-ccd" : "amd:symmetric-dual-ccd";
        }

        return new CpuBaselineRatioSettings(topology.CpuName, ratio, manualRatio, source,
            matchedModel, document.DatasetVersion);
    }

    private static bool MatchesHomogeneousAmdLayout(CpuTopologySnapshot topology)
    {
        var preset = CpuCorePerformancePresetResolver.Classify(topology.CpuName);
        return preset.KnownHomogeneous
            && (!preset.HasExpectedCoreLayout || preset.ExpectedPhysicalCoreCount == topology.PhysicalCoreCount);
    }

    private static bool HasCompleteSymmetricCcdLayout(CpuTopologySnapshot topology)
    {
        if (topology.CcdCount is not (1 or 2) || topology.Ccds.Count != topology.CcdCount
            || topology.PhysicalCoreCount <= 0 || topology.PhysicalCores.Count != topology.PhysicalCoreCount)
        {
            return false;
        }
        var knownCores = topology.PhysicalCores.Select(static core => core.Index).ToHashSet();
        var groupedCores = new HashSet<int>();
        var perCcd = topology.Ccds[0].PhysicalCoreIndexes.Count;
        foreach (var ccd in topology.Ccds)
        {
            if (ccd.PhysicalCoreIndexes.Count != perCcd || perCcd == 0
                || ccd.Source != "RelationProcessorDie")
            {
                return false;
            }
            foreach (var core in ccd.PhysicalCoreIndexes)
            {
                if (!knownCores.Contains(core) || !groupedCores.Add(core)) return false;
            }
        }
        return groupedCores.Count == topology.PhysicalCoreCount;
    }

    internal static string NormalizeModelName(string name)
    {
        var normalized = name.ToUpperInvariant().Replace("(R)", "").Replace("(TM)", "")
            .Replace("\u00ae", "").Replace("\u2122", "");
        normalized = GenerationPrefix().Replace(normalized, "");
        normalized = BrandWords().Replace(normalized, " ");
        normalized = ClockSuffix().Replace(normalized, "");
        return Whitespace().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex(@"^\s*\d+(?:ST|ND|RD|TH)\s+GEN\s+", RegexOptions.CultureInvariant)]
    private static partial Regex GenerationPrefix();

    [GeneratedRegex(@"\b(?:INTEL|PROCESSOR|CPU)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BrandWords();

    [GeneratedRegex(@"\s*(?:@\s*)?\d+(?:\.\d+)?\s*[GM]HZ\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockSuffix();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed record CatalogDocument(int SchemaVersion, string DatasetVersion,
        double UnmatchedRatio, double AmdSingleCcdRatio, double AmdSymmetricDualCcdRatio,
        IntelModelDefault[] IntelModels);

    private sealed record IntelModelDefault(string Name, double Ratio, int PhysicalCores, string Basis, string Group);
}
