using System.Text.Json.Serialization;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Monitoring;

internal sealed record ExpandedMonitoringHook(string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int IntervalMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpandedMonitoringRule? Rule)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Event { get; init; }
}
internal sealed record ExpandedMonitoringRule(string Binding, string MetricId, string Quantity, string Comparison,
    double Threshold, double HoldSeconds, int TopOwners,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SoftwareId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ProcessId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeviceIdentity = null);
internal sealed record ConditionName(string Name, string Binding, string MetricId, string Quantity, string Unit,
    bool SupportsOwners, string? DeviceIdentity = null);
internal sealed record ConditionUnit(string Unit, IReadOnlyList<ConditionUnitSpelling> Spellings);
internal sealed record ConditionUnitSpelling(string Spelling, double Scale);
internal sealed record ConditionOperator(string Spelling, string Comparison, bool IncreaseThreshold);
internal sealed record ConditionDefaults(int IntervalMs, int TopOwners);
internal sealed record ConditionCredentials(string KeyHeader, string KeylessCaller, string MetricScope, string ObjectOrOwnerScope);
internal sealed record MonitoringConditionVocabulary(IReadOnlyList<ConditionName> Names, IReadOnlyList<ConditionUnit> Units,
    IReadOnlyList<ConditionOperator> Operators, ConditionDefaults Defaults, IReadOnlyList<string> Grammar,
    ConditionCredentials Credentials, int MaximumLength, string WaitPath, string ExpansionHeader)
{
    public IReadOnlyList<string> Events => MemoryProtectionEvents;
    internal static readonly string[] MemoryProtectionEvents = ["active", "inactive", "normal-running-point-created",
        "threshold-reached", "below-threshold", "action-started", "action", "reserve-remaining-changed", "reserve-error"];
    // One declaration table, consumed by both expansion and the public vocabulary endpoint.
    private static readonly ConditionName[] FixedNames =
    [
        new("memory.free", "machine", "memory.usage", "remaining", "B", true),
        new("memory.used", "machine", "memory.usage", "value", "B", true),
        new("memory", "machine", "memory.usage", "percentOfTotal", "%", true),
        new("commit", "machine", "virtualMemory.usage", "percentOfTotal", "%", true),
        new("commit.used", "machine", "virtualMemory.usage", "value", "B", true),
        new("commit.free", "machine", "virtualMemory.usage", "remaining", "B", true),
        new("cpu", "machine", "cpu.usage", "value", "%", true),
        new("private", "process", "process_all_private_commit_actual", "value", "B", false),
        new("resident", "process", "process_all_working_set_actual", "value", "B", false),
        new("private-resident", "process", "process_all_private_working_set_actual", "value", "B", false),
        new("cpu", "process", "process_all_cpu_usage_actual", "value", "%", false),
        new("private", "software", "process_all_private_commit_actual", "value", "B", true),
        new("resident", "software", "process_all_working_set_actual", "value", "B", true),
        new("private-resident", "software", "process_all_private_working_set_actual", "value", "B", true),
        new("cpu", "software", "process_all_cpu_usage_actual", "value", "%", true)
    ];

    internal static MonitoringConditionVocabulary Create(HardwareMetricSnapshot? catalog)
    {
        var names = FixedNames.ToList();
        if (catalog is not null)
            names.AddRange(MetricCatalog.FromSnapshot(catalog)
                .Where(item => item.ValueKind is "float64" or "signed64" or "unsigned64" or "unsigned128")
                .Where(item => !string.IsNullOrEmpty(item.DeviceIdentity ?? item.ScopeKey))
                .Select(item => new ConditionName(item.Id, "device", item.Id, "value", item.Unit, false,
                    item.DeviceIdentity ?? item.ScopeKey)));
        var units = new List<ConditionUnit>
        {
            new("B", [new("B", 1), new("KiB", 1024), new("MiB", 1048576), new("GiB", 1073741824)]),
            new("%", [new("%", 1)]),
            new("time", [new("s", 1), new("ms", .001)])
        };
        foreach (var unit in names.Select(item => item.Unit).Distinct(StringComparer.Ordinal))
            if (!units.Any(item => item.Unit == unit)) units.Add(new(unit, [new(unit, 1)]));
        return new(names, units,
            [new("<", "below", false), new(">=", "atOrAbove", false), new(">", "atOrAbove", true)],
            new(1000, 0),
            [
                "machine-name operator number+unit for number+time [every number+time] [top integer]",
                "memory-protection event-name (one future occurrence; no hold, frequency or replay; processes.read scope)",
                "process PID[:STARTKEY] process-name operator number+unit for number+time [every number+time] [top 0]",
                "software \"software id\" software-name operator number+unit for number+time [every number+time] [top integer]",
                "device \"exact device identity\" device-name operator number+unit for number+time [every number+time] [top 0]",
                "Names, keywords and unit spellings are case-sensitive. Tokens require ASCII spaces; leading/trailing spaces are allowed. Input is one line.",
                "Numbers are unsigned decimal digits with an optional fractional part; no sign or exponent. Number and unit are adjacent. A unitless device value has no unit suffix.",
                "Hold time must be positive and at least 100 ns. Frequency must expand to a positive whole Int32 millisecond count and meet the original subscription source minimum.",
                "Optional clauses occur once in every-then-top order. Defaults are explicit in the expanded hook. Quoted ids allow only escaped quote and escaped backslash.",
                "A PID without STARTKEY resolves once from the published current process inventory; if unavailable, expansion fails. Explicit STARTKEY is a positive UInt64 Windows creation FILETIME.",
                "The > operator expands to atOrAbove with the smallest representable double greater than the converted threshold; low-level readings and thresholds are doubles. No extra evaluation is performed.",
                "Error positions are zero-based UTF-16 character offsets; end-of-input is position length. No partial acceptance or implicit unit conversion outside the listed spellings."
            ],
            new("X-Resource-Manager-Key", "verified-loopback-Windows-caller-for-local-read-scopes; elevated-administrator-also-accepted", "metrics.read", "processes.read"),
            4096, "/api/hooks/wait", "X-Resource-Manager-Expanded-Hook");
    }
}
