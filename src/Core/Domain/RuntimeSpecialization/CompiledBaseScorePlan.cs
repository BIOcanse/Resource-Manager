using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledBaseScorePlan(
    IReadOnlyDictionary<string, double> SoftwareBaseScoresBySoftwareId,
    IReadOnlyDictionary<string, double> ProcessBaseScoresBySoftwareAndProcessKey)
{
    public IReadOnlyDictionary<string, BaseScoreLayer> CatalogSoftwareScores { get; init; } =
        new Dictionary<string, BaseScoreLayer>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<CompiledCatalogProcessBaseScore> CatalogProcessScores { get; init; } = [];
    public static CompiledBaseScorePlan Empty { get; } = new(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    public double ResolveBaseScore(
        string? softwareId,
        string? softwareKind,
        string? processKey,
        string? processName = null,
        string? executablePath = null,
        IReadOnlyList<string>? serviceNames = null)
        => GetInputs(softwareId, softwareKind, processKey, processName, executablePath, serviceNames).Effective.Value;

    public BaseScoreResolution Resolve(
        string? softwareId,
        string? softwareKind,
        string? processKey = null,
        string? processName = null,
        string? executablePath = null,
        IReadOnlyList<string>? serviceNames = null)
    {
        var inputs = GetInputs(softwareId, softwareKind, processKey, processName, executablePath, serviceNames);
        var (value, source) = inputs.Effective;
        BaseScoreLayer[] layers = [
            new("type_default", inputs.TypeDefault, softwareKind),
            new("catalog_group", inputs.Group?.Value, inputs.Group?.Key),
            new("catalog_process", inputs.CatalogProcess?.BaseScore, inputs.CatalogProcess?.Key),
            new("user_software", inputs.SoftwareScore, inputs.SoftwareScore.HasValue ? softwareId : null),
            new("user_process", inputs.ProcessScore, inputs.ProcessScore.HasValue ? processKey : null)
        ];
        return new(value, source, layers);
    }

    private Inputs GetInputs(string? softwareId, string? softwareKind, string? processKey,
        string? processName, string? executablePath, IReadOnlyList<string>? serviceNames)
    {
        var group = softwareId is not null ? CatalogSoftwareScores.GetValueOrDefault(softwareId) : null;
        var catalogProcess = ResolveCatalogProcess(softwareId, processName, executablePath, serviceNames);
        double? softwareScore = softwareId is not null
            && SoftwareBaseScoresBySoftwareId.TryGetValue(softwareId, out var s) ? s : null;
        double? processScore = softwareId is not null && processKey is not null
            && ProcessBaseScoresBySoftwareAndProcessKey.TryGetValue(CreateProcessPolicyKey(softwareId, processKey), out var p) ? p : null;
        return new(OptimizationRuntimeScoringDefaults.BaseScoreForKind(softwareKind ?? string.Empty),
            group, catalogProcess, softwareScore, processScore);
    }

    private readonly record struct Inputs(double TypeDefault, BaseScoreLayer? Group,
        CompiledCatalogProcessBaseScore? CatalogProcess, double? SoftwareScore, double? ProcessScore)
    {
        public (double Value, string Source) Effective => ProcessScore is { } process ? (process, "user_process")
            : SoftwareScore is { } software ? (software, "user_software")
            : CatalogProcess is { } catalog ? (catalog.BaseScore, "catalog_process")
            : Group?.Value is { } group ? (group, "catalog_group") : (TypeDefault, "type_default");
    }

    private CompiledCatalogProcessBaseScore? ResolveCatalogProcess(
        string? softwareId, string? processName, string? path, IReadOnlyList<string>? serviceNames)
    {
        var name = SoftwareProcessMatch.NormalizeProcessName(processName);
        var normalizedPath = SoftwareProcessMatch.NormalizeExecutablePath(path);
        var pathName = SoftwareProcessMatch.NormalizeProcessName(normalizedPath);
        CompiledCatalogProcessBaseScore? nameMatch = null;
        CompiledCatalogProcessBaseScore? serviceMatch = null;
        foreach (var entry in CatalogProcessScores)
        {
            if (entry.ProcessName is not null
                && (string.Equals(entry.ProcessName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.ProcessName, pathName, StringComparison.OrdinalIgnoreCase))
                && (entry.ExecutablePath is null || string.Equals(entry.ExecutablePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.ExecutablePath is not null) return entry;
                nameMatch ??= entry;
            }
            if (entry.ServiceName is not null
                && (serviceNames?.Contains(entry.ServiceName, StringComparer.OrdinalIgnoreCase) == true
                    || string.Equals(softwareId, entry.ServiceSoftwareId, StringComparison.OrdinalIgnoreCase))
                && (serviceMatch is null || entry.BaseScore > serviceMatch.BaseScore)) serviceMatch = entry;
        }
        return nameMatch ?? serviceMatch;
    }

    public static string CreateProcessPolicyKey(string softwareId, string processKey)
    {
        return $"{softwareId}\n{processKey}";
    }
}

public sealed record BaseScoreLayer(string Source, double? Value, string? Key);
public sealed record BaseScoreResolution(double Value, string Source, IReadOnlyList<BaseScoreLayer> Layers);
public sealed record CompiledCatalogProcessBaseScore(
    string Key, string? ProcessName, string? ExecutablePath, string? ServiceName, double BaseScore)
{
    public string? ServiceSoftwareId { get; } = ServiceName is null ? null : WindowsServiceSoftwareIds.ForService(ServiceName);
}
