using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal static class NativeReportCoordinatorPersistenceMigration
{
    private const uint PreviousCheckpointSchemaVersion = 0x0005_0000;
    private const uint FirstCompatibleLegacyProfileRevision = 41;
    private const uint LastCompatibleLegacyProfileRevision = 46;
    private static readonly IReadOnlyDictionary<ulong, ulong>
        CompatibleLegacyRuleGenerations = new Dictionary<ulong, ulong>
        {
            [1031] = 0xD0221F6491937889,
            [1032] = 0x8C7FE13F93CF7F06,
            [1033] = 0x38CB00A190C89F87,
            [1034] = 0xF79467B129C4BCB7,
            [1035] = 0x0A4F410DD8E4FC56,
            [1036] = 0xA3540B7221989A08
        };

    internal static NativeReportPersistenceOperation UpgradeLoadedRow(
        NativeReportPersistenceOperation row)
    {
        if (row.OperationKind != (uint)NativeReportPersistenceKind.Metadata
            || row.CheckpointSchemaVersion == NativeReportCoordinatorAbi.Version)
        {
            return row;
        }

        if (row.CheckpointSchemaVersion != PreviousCheckpointSchemaVersion)
        {
            throw new InvalidDataException(
                "The persisted report metadata uses an unsupported checkpoint schema.");
        }

        return row with
        {
            CheckpointSchemaVersion = NativeReportCoordinatorAbi.Version
        };
    }

    internal static NativeReportPersistenceOperation[] RebindLoadedRows(
        IReadOnlyList<NativeReportPersistenceOperation> rows,
        CompiledHostManagerReportCoordinatorPlan plan)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidDataException(
                "The report coordinator plan is not published.");
        }

        var rules = plan.Recreate.Rules.ToDictionary(
            static rule => rule.RuleHandle);
        var sources = rows
            .Where(static row =>
                row.OperationKind == (uint)NativeReportPersistenceKind.Source)
            .Select(static row => (row.SourceHandle, row.CoverageScopeHandle))
            .ToHashSet();
        var rebound = new List<NativeReportPersistenceOperation>(rows.Count);
        foreach (var row in rows)
        {
            var kind = (NativeReportPersistenceKind)row.OperationKind;
            if (kind is NativeReportPersistenceKind.Source
                or NativeReportPersistenceKind.Trust
                or NativeReportPersistenceKind.Metadata)
            {
                rebound.Add(row);
                continue;
            }
            if (kind is not NativeReportPersistenceKind.Observation
                and not NativeReportPersistenceKind.Bucket
                and not NativeReportPersistenceKind.Report)
            {
                throw new InvalidDataException(
                    "The persisted report row has an unsupported kind.");
            }
            if (!rules.TryGetValue(row.RuleHandle, out var rule)
                || !StaticIdentityMatches(row, kind, rule)
                || (kind == NativeReportPersistenceKind.Observation
                    && !sources.Contains((
                        row.SourceHandle,
                        row.CoverageScopeHandle))))
            {
                continue;
            }
            if (row.RuleGeneration == rule.RuleGeneration)
            {
                rebound.Add(row);
                continue;
            }
            if (IsCompatibleLegacyProfileGeneration(row, rule))
            {
                rebound.Add(row with
                {
                    RuleGeneration = rule.RuleGeneration
                });
            }
        }

        var observations = rebound
            .Where(static row =>
                row.OperationKind == (uint)NativeReportPersistenceKind.Observation)
            .Select(static row => (
                row.SourceHandle,
                row.CoverageScopeHandle,
                row.RuleHandle,
                row.TargetHandle))
            .ToHashSet();
        return rebound
            .Where(row =>
            {
                if (row.OperationKind != (uint)NativeReportPersistenceKind.Bucket)
                {
                    return true;
                }
                var rule = rules[row.RuleHandle];
                return sources.Contains((
                        row.SourceHandle,
                        rule.CoverageScopeHandle))
                    && observations.Contains((
                        row.SourceHandle,
                        rule.CoverageScopeHandle,
                        row.RuleHandle,
                        row.TargetHandle));
            })
            .ToArray();
    }

    private static bool IsCompatibleLegacyProfileGeneration(
        NativeReportPersistenceOperation row,
        CompiledHostManagerReportCoordinatorRulePlan rule)
    {
        if ((row.RuleGeneration & uint.MaxValue) != 0)
        {
            return false;
        }
        var revision = row.RuleGeneration >> 32;
        return revision is >= FirstCompatibleLegacyProfileRevision
                and <= LastCompatibleLegacyProfileRevision
            && CompatibleLegacyRuleGenerations.TryGetValue(
                row.RuleHandle,
                out var compatibleGeneration)
            && rule.RuleGeneration == compatibleGeneration;
    }

    private static bool StaticIdentityMatches(
        NativeReportPersistenceOperation row,
        NativeReportPersistenceKind kind,
        CompiledHostManagerReportCoordinatorRulePlan rule)
        => kind switch
        {
            NativeReportPersistenceKind.Observation =>
                row.SourceHandle == rule.SourceHandle
                    && row.CoverageScopeHandle == rule.CoverageScopeHandle,
            NativeReportPersistenceKind.Bucket =>
                row.SourceHandle == rule.SourceHandle
                    && row.CoverageScopeHandle == 0,
            NativeReportPersistenceKind.Report =>
                row.SourceHandle == rule.SourceHandle
                    && row.CoverageScopeHandle == rule.CoverageScopeHandle
                    && row.FamilyHandle == rule.FamilyHandle
                    && row.ReportTypeHandle == rule.ReportTypeHandle
                    && row.ResourceKindHandle == rule.ResourceKindHandle
                    && row.PayloadHandle == rule.PayloadHandle,
            _ => false
        };
}
