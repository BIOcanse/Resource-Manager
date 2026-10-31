using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

// User report-rule values (settings page 「报告判定」, docs/optimization_reports.md).
// The profile names each rule and holds its defaults; a user override replaces single
// editable fields. Values that do not fit are rejected with the rule, field and reason.
public sealed partial class HostManagerPlanCompiler
{
    private const string ActivationField = "activationThreshold";
    private const string ClearField = "clearThreshold";
    private const string HitsField = "requiredConsecutiveHits";
    private const string MissesField = "requiredConsecutiveMisses";
    private const string ZeroActivityField = "zeroActivityThreshold";
    private const string ExecutableNamesField = "executableNames";
    // Read on use: a static field here would run the embedded declaration compilation inside this type's initializer.
    private static ReportRuleLimits RuleLimits =>
        ResourceManager.App.Infrastructure.Optimization.OptimizationSettings.ProductDefaults.ReportRuleLimits;
    private static uint ReportRuleCountMaximum => RuleLimits.MaximumConsecutiveCount;
    private static double ReportRuleZeroActivityMaximumPercent => RuleLimits.MaximumZeroActivityPercent;
    private static int ReportRuleExecutableNameCountMaximum => RuleLimits.MaximumExecutableNames;
    private static int ReportRuleExecutableNameLengthMaximum => RuleLimits.MaximumExecutableNameCharacters;

    private static readonly ImmutableArray<string> ThresholdAndCountFields =
        [ActivationField, ClearField, HitsField, MissesField];

    private static Dictionary<string, AppReportRuleOverride> IndexReportRuleOverrides(
        IReadOnlyList<HostManagerReportCoordinatorRuleProfile> source,
        IReadOnlyList<AppReportRuleOverride> userOverrides)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.Name))
                throw Missing("host_recreate.report_coordinator.rules.name");
            if (!names.Add(item.Name))
                throw new InvalidDataException("host_recreate.report_coordinator.rules contains a duplicate name.");
        }

        var result = new Dictionary<string, AppReportRuleOverride>(StringComparer.Ordinal);
        foreach (var user in userOverrides)
        {
            var name = user?.Rule ?? string.Empty;
            if (!names.Contains(name))
                throw new ReportRuleSettingException(name, "rule", "unknown-rule");
            if (!result.TryAdd(name, user!))
                throw new ReportRuleSettingException(name, "rule", "duplicate-rule");
        }

        return result;
    }

    private static CompiledHostManagerReportRuleSetting CompileReportRuleSetting(
        HostManagerReportCoordinatorRuleProfile item)
    {
        // Ranges are in the stored unit of each fact; display unit, scale and step serve the settings page.
        ImmutableArray<string> fields =
            item.FactKind switch
            {
                HostManagerReportFactKind.CpuTemperatureCelsius
                    or HostManagerReportFactKind.CpuUsagePercent
                    or HostManagerReportFactKind.CpuFrequencyPercent
                    or HostManagerReportFactKind.InterruptMaximumSingleDurationMilliseconds
                    or HostManagerReportFactKind.InterruptCpuCapacityPercent
                    or HostManagerReportFactKind.SoftwareMemorySystemPercent
                    or HostManagerReportFactKind.SoftwareMemoryBytes => ThresholdAndCountFields,
                HostManagerReportFactKind.ProcessMemoryBytes => [ActivationField, ClearField],
                HostManagerReportFactKind.ProcessIdleMilliseconds => [ActivationField, ZeroActivityField, ExecutableNamesField],
                _ => throw new InvalidDataException(
                    $"host_recreate.report_coordinator.rules fact_kind {item.FactKind} has no settings range.")
            };
        if (!RuleLimits.ThresholdRanges.TryGetValue(item.FactKind.ToString(), out var range))
            throw new InvalidDataException($"The report threshold range for {item.FactKind} is not declared.");
        if (fields.Contains(ClearField) && item.Comparison is not (1 or 2))
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules with an editable clear line must compare at-or-above or at-or-below.");
        }

        return new CompiledHostManagerReportRuleSetting(
            item.Name!,
            item.FactKind,
            item.Comparison,
            range.Unit,
            range.Scale,
            range.Step,
            range.Minimum,
            range.Maximum,
            ReportRuleCountMaximum,
            fields.Contains(ZeroActivityField) ? ReportRuleZeroActivityMaximumPercent : null,
            fields,
            new CompiledHostManagerReportRuleValues(
                fields.Contains(ActivationField) ? item.ActivationThreshold : null,
                fields.Contains(ClearField) ? item.ClearThreshold : null,
                fields.Contains(HitsField) ? item.RequiredConsecutiveHits : null,
                fields.Contains(MissesField) ? item.RequiredConsecutiveMisses : null,
                fields.Contains(ZeroActivityField) ? item.ZeroActivityThreshold : null,
                fields.Contains(ExecutableNamesField) ? item.ExecutableNames : null));
    }

    // The effective rule is a fresh profile without Name: the generation hashes only rule content,
    // so an untouched rule keeps the generation (and the persisted state) it had before.
    private static HostManagerReportCoordinatorRuleProfile WithUserValues(
        HostManagerReportCoordinatorRuleProfile item,
        AppReportRuleOverride? user,
        CompiledHostManagerReportRuleSetting setting)
    {
        if (user is not null)
        {
            RequireEditable(user.ActivationThreshold is not null, ActivationField);
            RequireEditable(user.ClearThreshold is not null, ClearField);
            RequireEditable(user.RequiredConsecutiveHits is not null, HitsField);
            RequireEditable(user.RequiredConsecutiveMisses is not null, MissesField);
            RequireEditable(user.ZeroActivityThreshold is not null, ZeroActivityField);
            RequireEditable(user.ExecutableNames is not null, ExecutableNamesField);
        }

        return new HostManagerReportCoordinatorRuleProfile
        {
            ExecutableNames = user?.ExecutableNames ?? item.ExecutableNames,
            ZeroActivityThreshold = user?.ZeroActivityThreshold ?? item.ZeroActivityThreshold,
            FactKind = item.FactKind,
            RuleHandle = item.RuleHandle,
            SourceHandle = item.SourceHandle,
            CoverageScopeHandle = item.CoverageScopeHandle,
            FamilyHandle = item.FamilyHandle,
            ReportTypeHandle = item.ReportTypeHandle,
            ResourceKindHandle = item.ResourceKindHandle,
            PayloadHandle = item.PayloadHandle,
            MetricSelector = item.MetricSelector,
            Comparison = item.Comparison,
            Priority = item.Priority,
            Severity = item.Severity,
            RequiredConsecutiveHits = user?.RequiredConsecutiveHits ?? item.RequiredConsecutiveHits,
            RequiredConsecutiveMisses = user?.RequiredConsecutiveMisses ?? item.RequiredConsecutiveMisses,
            MinimumSampleDurationMilliseconds = item.MinimumSampleDurationMilliseconds,
            ActivationThreshold = user?.ActivationThreshold ?? item.ActivationThreshold,
            ClearThreshold = user?.ClearThreshold ?? item.ClearThreshold,
            StaleAfterMilliseconds = item.StaleAfterMilliseconds,
            RetentionMilliseconds = item.RetentionMilliseconds,
            Rolling = item.Rolling,
            PredicateGroupHandle = item.PredicateGroupHandle,
            PredicateIndex = item.PredicateIndex,
            PredicateCount = item.PredicateCount
        };

        void RequireEditable(bool present, string field)
        {
            if (present && !setting.EditableFields.Contains(field))
                throw new ReportRuleSettingException(setting.Name, field, "not-editable");
        }
    }

    // Defaults pass through the same checks. A failure the user's values caused is a settings
    // rejection the page can show; one that only involves profile defaults is a profile error.
    private static void ValidateReportRuleUserValues(
        HostManagerReportCoordinatorRuleProfile rule,
        AppReportRuleOverride? user,
        CompiledHostManagerReportRuleSetting setting)
    {
        var fields = setting.EditableFields;
        if (fields.Contains(ActivationField)
            && !InRange(rule.ActivationThreshold, setting.ThresholdMinimum, setting.ThresholdMaximum))
            throw Invalid(ActivationField, "out-of-range", user?.ActivationThreshold is not null);
        if (fields.Contains(ClearField))
        {
            if (!InRange(rule.ClearThreshold, setting.ThresholdMinimum, setting.ThresholdMaximum))
                throw Invalid(ClearField, "out-of-range", user?.ClearThreshold is not null);
            // Clearing needs a value on the safe side of the activation line; equal means no hysteresis.
            if (rule.Comparison == 1 ? rule.ClearThreshold > rule.ActivationThreshold
                    : rule.ClearThreshold < rule.ActivationThreshold)
                throw Invalid(ClearField, "clear-on-wrong-side",
                    user?.ClearThreshold is not null || user?.ActivationThreshold is not null);
        }

        if (fields.Contains(HitsField) && (rule.RequiredConsecutiveHits < 1 || rule.RequiredConsecutiveHits > ReportRuleCountMaximum))
            throw Invalid(HitsField, "out-of-range", user?.RequiredConsecutiveHits is not null);
        if (fields.Contains(MissesField) && (rule.RequiredConsecutiveMisses < 1 || rule.RequiredConsecutiveMisses > ReportRuleCountMaximum))
            throw Invalid(MissesField, "out-of-range", user?.RequiredConsecutiveMisses is not null);
        if (fields.Contains(ZeroActivityField)
            && !InRange(rule.ZeroActivityThreshold ?? double.NaN, 0, ReportRuleZeroActivityMaximumPercent))
            throw Invalid(ZeroActivityField, "out-of-range", user?.ZeroActivityThreshold is not null);
        if (fields.Contains(ExecutableNamesField) && !AreValidExecutableNames(rule.ExecutableNames))
            throw Invalid(ExecutableNamesField, "invalid-executable-names", user?.ExecutableNames is not null);

        Exception Invalid(string field, string reason, bool userValue) => userValue
            ? new ReportRuleSettingException(setting.Name, field, reason)
            : new InvalidDataException(
                $"host_recreate.report_coordinator.rules '{setting.Name}' default {field} is invalid: {reason}.");
    }

    private static bool InRange(double value, double minimum, double maximum)
        => double.IsFinite(value) && value >= minimum && value <= maximum;

    // Names are matched against the file name without extension, so "node.exe" would never match.
    private static bool AreValidExecutableNames(IReadOnlyList<string>? names)
        => names is not null
            && names.Count <= ReportRuleExecutableNameCountMaximum
            && names.All(static name => name is { Length: > 0 } && name.Length <= ReportRuleExecutableNameLengthMaximum
                && name == name.Trim()
                && name.IndexOfAny(['/', '\\', ':']) < 0
                && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
}
