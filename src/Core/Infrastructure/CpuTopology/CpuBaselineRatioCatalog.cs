using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class CpuBaselineRatioCatalog
{
    private const string ResourceName = "ResourceManager.Configuration.Cpu.baseline-defaults.json";
    private readonly CatalogDefaults document;
    private readonly Lazy<FrozenDictionary<string, IntelModelDefault>> intelModels;

    private CpuBaselineRatioCatalog(CatalogDefaults document, Func<FrozenDictionary<string, IntelModelDefault>> loadIntelModels)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.DatasetVersion)
            || !CpuBaselineRatio.IsValid(document.UnmatchedRatio)
            || !CpuBaselineRatio.IsValid(document.AmdSingleCcdRatio)
            || !CpuBaselineRatio.IsValid(document.AmdSymmetricDualCcdRatio))
        {
            throw new InvalidDataException("Invalid CPU baseline default catalog.");
        }
        this.document = document;
        intelModels = new(loadIntelModels);
    }

    internal bool IntelModelsLoaded => intelModels.IsValueCreated;

    private static FrozenDictionary<string, IntelModelDefault> CreateIntelModels(CatalogDocument document)
    {
        if (document.IntelModels is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid CPU baseline default catalog.");
        }
        var models = new Dictionary<string, IntelModelDefault>(StringComparer.Ordinal);
        var bases = new HashSet<string>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in document.IntelModels)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Name)
                || string.IsNullOrWhiteSpace(model.Basis) || string.IsNullOrWhiteSpace(model.Group)
                || model.PhysicalCores <= 0 || !CpuBaselineRatio.IsValid(model.Ratio))
            {
                throw new InvalidDataException("Invalid or duplicate Intel CPU baseline default.");
            }
            // These catalog-controlled values repeat across the model table; keep sharing local to this owner.
            if (!bases.TryGetValue(model.Basis, out var basis)) bases.Add(basis = model.Basis);
            if (!groups.TryGetValue(model.Group, out var group)) groups.Add(group = model.Group);
            if (!models.TryAdd(NormalizeModelName(model.Name), model with { Basis = basis, Group = group }))
            {
                throw new InvalidDataException("Invalid or duplicate Intel CPU baseline default.");
            }
        }
        return models.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static CpuBaselineRatioCatalog LoadEmbedded()
    {
        using var stream = typeof(CpuBaselineRatioCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The CPU baseline default catalog is missing.");
        // Skip the Intel array without retaining its JSON or constructing its models on other vendors.
        var defaults = JsonSerializer.Deserialize<CatalogDefaults>(stream, CreateJsonOptions())
            ?? throw new InvalidDataException("The CPU baseline default catalog is empty.");
        return new CpuBaselineRatioCatalog(defaults, static () =>
        {
            using var modelStream = typeof(CpuBaselineRatioCatalog).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("The CPU baseline default catalog is missing.");
            return CreateIntelModels(ReadDocument(modelStream));
        });
    }

    internal static CpuBaselineRatioCatalog Read(Stream stream)
    {
        var document = ReadDocument(stream);
        var catalog = new CpuBaselineRatioCatalog(new(document.SchemaVersion, document.DatasetVersion,
            document.UnmatchedRatio, document.AmdSingleCcdRatio, document.AmdSymmetricDualCcdRatio),
            () => CreateIntelModels(document));
        _ = catalog.intelModels.Value;
        return catalog;
    }

    private static CatalogDocument ReadDocument(Stream stream)
        => JsonSerializer.Deserialize<CatalogDocument>(stream, CreateJsonOptions())
            ?? throw new InvalidDataException("The CPU baseline default catalog is empty.");

    private static JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict
        };

    public CpuBaselineRatioSettings Resolve(CpuTopologySnapshot topology, double? manualRatio)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (manualRatio.HasValue) CpuBaselineRatio.Validate(manualRatio.Value);

        var ratio = document.UnmatchedRatio;
        var source = "uncompensated";
        string? matchedModel = null;
        var vendor = NormalizeVendor(topology.Specification.Vendor);
        if (vendor == "Intel"
            && intelModels.Value.TryGetValue(NormalizeModelName(topology.CpuName), out var model)
            && model.PhysicalCores == topology.PhysicalCoreCount)
        {
            ratio = model.Ratio;
            source = $"intel:{model.Basis}";
            matchedModel = model.Name;
        }
        else if (vendor == "AMD"
            && MatchesHomogeneousAmdLayout(topology)
            && HasCompleteSymmetricCcdLayout(topology))
        {
            ratio = topology.CcdCount == 1 ? document.AmdSingleCcdRatio : document.AmdSymmetricDualCcdRatio;
            source = topology.CcdCount == 1 ? "amd:single-ccd" : "amd:symmetric-dual-ccd";
        }

        return new CpuBaselineRatioSettings(topology.CpuName, ratio, manualRatio, source,
            matchedModel, document.DatasetVersion);
    }

    private static string? NormalizeVendor(string vendor)
    {
        var value = vendor.Trim();
        if (value.Equals("Intel", StringComparison.OrdinalIgnoreCase)
            || value.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }
        if (value.Equals("AMD", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }
        return null;
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

    private sealed record CatalogDefaults(int SchemaVersion, string DatasetVersion,
        double UnmatchedRatio, double AmdSingleCcdRatio, double AmdSymmetricDualCcdRatio)
    {
        [JsonIgnore]
        public object? IntelModels { get; init; }
    }

    private sealed record IntelModelDefault(string Name, double Ratio, int PhysicalCores, string Basis, string Group);
}
