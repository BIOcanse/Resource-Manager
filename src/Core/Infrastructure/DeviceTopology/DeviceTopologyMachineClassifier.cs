using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

/// <summary>The machine classifier of the device topology block (33). Its rules and compiled NonBacktracking automata
/// (several MiB once matched) exist only while that block's sampling run lasts: the run opens this owner when it starts
/// and closes it when it returns, the first classification inside the run builds the classifier, and later topology
/// reads in the same run reuse it. A stopped block holds none. A topology read outside a run (an explicit field
/// capture while the block is stopped) builds a classifier for that one read.</summary>
public sealed class DeviceTopologyMachineClassifierOwner
{
    private readonly object gate = new();
    private int openRuns;
    private DeviceTopologyMachineClassifier? runClassifier;

    internal IDisposable OpenRun()
    {
        lock (gate) openRuns++;
        return new RunLease(this);
    }

    internal bool HoldsClassifier { get { lock (gate) return runClassifier is not null; } }

    internal DeviceTopologyMachineIdentity Classify(DeviceTopologyMachineEvidence evidence)
    {
        DeviceTopologyMachineClassifier? classifier = null;
        lock (gate)
            if (openRuns > 0) classifier = runClassifier ??= DeviceTopologyMachineClassifier.Load();
        return (classifier ?? DeviceTopologyMachineClassifier.Load()).Classify(evidence);
    }

    private void CloseRun()
    {
        lock (gate)
            if (--openRuns == 0) runClassifier = null;
    }

    private sealed class RunLease(DeviceTopologyMachineClassifierOwner owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.CloseRun();
        }
    }
}

internal sealed partial class DeviceTopologyMachineClassifier
{
    private readonly Rules rules;
    private readonly (ComponentRule Rule, Regex Pattern)[] matches;

    private DeviceTopologyMachineClassifier(Rules rules)
    {
        this.rules = rules;
        // NonBacktracking matches in time linear in the input, so a wall-clock timeout guards nothing here and only
        // fails under machine load while the engine builds its automaton on first use.
        matches = rules.WorkstationRules.Select(rule => (rule, new Regex(rule.Pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            Regex.InfiniteMatchTimeout))).ToArray();
    }

    // Builds a new instance; DeviceTopologyMachineClassifierOwner decides how long one lives.
    internal static DeviceTopologyMachineClassifier Load()
    {
        using var stream = typeof(DeviceTopologyMachineClassifier).Assembly.GetManifestResourceStream(
            "ResourceManager.Configuration.DeviceTopology.form-factor-rules.json")
            ?? throw new InvalidDataException("Missing form factor rules.");
        var rules = JsonSerializer.Deserialize(stream, RulesJsonContext.Default.Rules)
            ?? throw new InvalidDataException("Empty form factor rules.");
        if (rules.PortableChassisCodes.Count == 0 || rules.DesktopChassisCodes.Count == 0
            || rules.WorkstationRules.Length == 0
            || rules.PortableChassisCodes.Intersect(rules.StationaryChassisCodes).Any()
            || rules.WorkstationRules.Any(rule => rule.Component is not ("cpu" or "gpu")
                || string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Pattern))
            || rules.WorkstationRules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.WorkstationRules.Length)
            throw new InvalidDataException("Invalid form factor rules.");
        return new(rules);
    }

    internal DeviceTopologyMachineIdentity Classify(DeviceTopologyMachineEvidence evidence)
    {
        var components = new List<DeviceTopologyWorkstationComponent>();
        AddMatches("cpu", evidence.CpuModels.Value, components);
        // Only a confirmed non-detachable GPU belongs to this machine's workstation decision.
        // External rendering adapters remain independent graphics docks; missing attachment is unknown.
        AddMatches("gpu", evidence.GpuAttachments.Value?.Where(static gpu => gpu.IsDetachable == false)
            .Select(static gpu => gpu.Model).ToArray(), components);
        evidence = evidence with { WorkstationComponents = components.ToArray() };
        var codes = evidence.ChassisTypes.Value ?? [];
        var portableChassis = codes.Any(rules.PortableChassisCodes.Contains);
        var portable = portableChassis || evidence.SystemBatteryPresent.Value == true;
        var contradictory = portableChassis && codes.Any(rules.StationaryChassisCodes.Contains);
        var form = contradictory ? "unknown"
            : portable && components.Count > 0 ? "mobile_workstation"
            : portable ? "laptop"
            : components.Count > 0 ? "workstation"
            : codes.Any(rules.DesktopChassisCodes.Contains) ? "desktop" : "unknown";
        return new(form, evidence);
    }

    private void AddMatches(string component, IReadOnlyList<string>? models,
        List<DeviceTopologyWorkstationComponent> result)
    {
        foreach (var model in (models ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            foreach (var (rule, pattern) in matches)
            {
                if (rule.Component != component || !pattern.IsMatch(model)) continue;
                result.Add(new(component, model, rule.Id));
                break; // The most specific configured rule wins for each component model.
            }
        }
    }

    internal sealed record ComponentRule(string Id, string Component, string Pattern);
    // Numeric JSON arrays use a collection contract; byte[] is a base64 blob in System.Text.Json.
    internal sealed record Rules(IReadOnlyList<byte> PortableChassisCodes, IReadOnlyList<byte> DesktopChassisCodes,
        IReadOnlyList<byte> StationaryChassisCodes, ComponentRule[] WorkstationRules);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Rules))]
    private partial class RulesJsonContext : JsonSerializerContext;
}
