using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerMetricSnapshotBuildPlan CompileMetricSnapshotBuild(
        uint abiVersion,
        HostManagerMetricSnapshotCapacityProfile source,
        IReadOnlySet<string> nativeModules)
    {
        if (abiVersion != NativeMetricSnapshotAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.metric_snapshot_abi_version must match the native metric-snapshot ABI.");
        }
        if (!nativeModules.Contains("metric_snapshot"))
        {
            throw new InvalidDataException(
                "build_specialize.native_modules must include metric_snapshot.");
        }

        return new CompiledHostManagerMetricSnapshotBuildPlan(
            abiVersion,
            "metric_snapshot",
            CompileMetricSnapshotCapacity(
                source,
                "build_specialize.capacity_limits.metric_snapshot"));
    }

    private static CompiledHostManagerMetricSnapshotRecreatePlan
        CompileMetricSnapshotRecreate(
            HostManagerMetricSnapshotRecreateProfile source,
            CompiledHostManagerMetricSnapshotBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileMetricSnapshotCapacity(
            source.Capacity
                ?? throw Missing("host_recreate.metric_snapshot.capacity"),
            "host_recreate.metric_snapshot.capacity");
        if (!capacity.FitsWithin(build.CapacityLimits))
        {
            throw new InvalidDataException(
                "host_recreate.metric_snapshot.capacity exceeds its build-specialize limits.");
        }
        if (source.CatalogContractVersion != NativeMetricSnapshotAbi.CatalogContractVersion
            || source.ValueContractVersion != NativeMetricSnapshotAbi.ValueContractVersion
            || source.ObservationContractVersion
                != NativeMetricSnapshotAbi.ObservationContractVersion
            || source.InventoryContractVersion
                != NativeMetricSnapshotAbi.InventoryContractVersion
            || source.PersistenceContractVersion
                != NativeMetricSnapshotAbi.PersistenceContractVersion
            || source.CpuCounterContractVersion
                != NativeMetricSnapshotAbi.CpuCounterContractVersion)
        {
            throw new InvalidDataException(
                "host_recreate.metric_snapshot contract versions must exactly match the native ABI.");
        }

        return new CompiledHostManagerMetricSnapshotRecreatePlan(
            capacity,
            NormalizeStrictRelativePath(
                source.PersistenceRelativePath,
                "host_recreate.metric_snapshot.persistence_relative_path"),
            source.CatalogContractVersion,
            source.ValueContractVersion,
            source.ObservationContractVersion,
            source.InventoryContractVersion,
            source.PersistenceContractVersion,
            source.CpuCounterContractVersion);
    }

    private static CompiledHostManagerMetricSnapshotHotPublishPlan
        CompileMetricSnapshotHotPublish(
            HostManagerMetricSnapshotHotPublishProfile source,
            CompiledHostManagerMetricSnapshotRecreatePlan recreate,
            int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.SourcePolicies);
        ArgumentNullException.ThrowIfNull(source.RuleTemplates);
        var policies = source.SourcePolicies
            .Select(CompileMetricSnapshotSourcePolicy)
            .OrderBy(static policy => policy.SourceHandle)
            .ToImmutableArray();
        if (policies.IsDefaultOrEmpty
            || policies.Select(static policy => policy.SourceId)
                .Distinct(StringComparer.Ordinal)
                .Count() != policies.Length
            || policies.Select(static policy => policy.SourceHandle)
                .Distinct()
                .Count() != policies.Length
            || policies.Count(static policy => policy.SourceRole == 2) != 1)
        {
            throw new InvalidDataException(
                "hot_publish.metric_snapshot source policies contain duplicate or incomplete identities.");
        }
        var policyById = policies.ToDictionary(
            static policy => policy.SourceId,
            StringComparer.Ordinal);
        var templates = source.RuleTemplates
            .Select(template => CompileMetricSnapshotRuleTemplate(
                template,
                policyById))
            .OrderBy(static template => template.TemplateId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (templates.IsDefaultOrEmpty
            || templates.Select(static template => template.TemplateId)
                .Distinct(StringComparer.Ordinal)
                .Count() != templates.Length)
        {
            throw new InvalidDataException(
                "hot_publish.metric_snapshot rule templates contain duplicate or missing identities.");
        }
        ValidateMetricSnapshotExpansionCapacity(
            templates,
            recreate.Capacity);
        var configurationGeneration = checked((ulong)profileRevision << 32);
        var manifestSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            source.CatalogManifestVersion,
            SourcePolicies = policies,
            RuleTemplates = templates
        });
        var result = new CompiledHostManagerMetricSnapshotHotPublishPlan(
            configurationGeneration,
            configurationGeneration | 1UL,
            source.CatalogManifestVersion,
            manifestSha256,
            source.MaximumFutureSkewMilliseconds,
            source.ResidentByteBudget,
            policies,
            templates);
        if (!result.IsPublished
            || policies.Length > recreate.Capacity.MaximumSourceCount
            || templates.Length > recreate.Capacity.MaximumRuleCount
            || result.ResidentByteBudget > recreate.Capacity.ResidentByteBudget)
        {
            throw new InvalidDataException(
                "hot_publish.metric_snapshot contains an invalid policy or budget.");
        }

        return result;
    }

    private static CompiledHostManagerMetricSnapshotPlan CompileMetricSnapshotPlan(
        CompiledHostManagerMetricSnapshotBuildPlan build,
        CompiledHostManagerMetricSnapshotRecreatePlan recreate,
        CompiledHostManagerMetricSnapshotHotPublishPlan hotPublish)
    {
        var result = new CompiledHostManagerMetricSnapshotPlan(
            build,
            recreate,
            hotPublish,
            HostManagerPlanIdentity.ComputeDigest(new
            {
                Module = "metric_snapshot",
                Build = build,
                Recreate = recreate
            }),
            HostManagerPlanIdentity.ComputeDigest(new
            {
                Module = "metric_snapshot",
                HotPublish = hotPublish
            }));
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                "The compiled metric-snapshot plan is invalid.");
        }

        return result;
    }

    private static CompiledHostManagerMetricSnapshotCapacityPlan
        CompileMetricSnapshotCapacity(
            HostManagerMetricSnapshotCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new CompiledHostManagerMetricSnapshotCapacityPlan(
            source.MaximumSourceCount,
            source.MaximumMetricCount,
            source.MaximumRuleCount,
            source.MaximumRequestedCount,
            source.MaximumObservationCount,
            source.MaximumGpuAdapterCount,
            source.MaximumPersistenceSourceCount,
            source.MaximumPersistenceRuleCount,
            source.MaximumPersistenceGpuCount,
            source.SourceIndexCapacity,
            source.MetricIndexCapacity,
            source.RuleIndexCapacity,
            source.GpuIndexCapacity,
            source.GpuLuidIndexCapacity,
            source.GpuKeyIndexCapacity,
            source.MaximumPlanMetricCount,
            source.MaximumSourceModeCount,
            source.MaximumSourcePlanCount,
            source.MaximumMetricPlanCount,
            source.ResidentByteBudget);
        if (!result.IsPublished
            || !IsPowerOfTwo(result.SourceIndexCapacity)
            || !IsPowerOfTwo(result.MetricIndexCapacity)
            || !IsPowerOfTwo(result.RuleIndexCapacity)
            || !IsPowerOfTwo(result.GpuIndexCapacity)
            || !IsPowerOfTwo(result.GpuLuidIndexCapacity)
            || !IsPowerOfTwo(result.GpuKeyIndexCapacity))
        {
            throw new InvalidDataException(
                $"{path} contains an invalid metric-snapshot capacity.");
        }

        return result;
    }

    private static CompiledHostManagerMetricSnapshotSourcePolicy
        CompileMetricSnapshotSourcePolicy(
            HostManagerMetricSnapshotSourcePolicyProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new CompiledHostManagerMetricSnapshotSourcePolicy(
            RequireCanonicalToken(
                source.SourceId,
                "hot_publish.metric_snapshot.source_policies.source_id"),
            source.SourceHandle,
            source.SourceRole switch
            {
                "metrics" => 1,
                "gpu_inventory" => 2,
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot source role '{source.SourceRole}'.")
            },
            source.Priority,
            source.Retention switch
            {
                "retain_last_good" => 1,
                "mark_unavailable" => 2,
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot retention policy '{source.Retention}'.")
            },
            source.Required,
            source.CapabilityMask,
            source.SemanticFingerprint);
        return result.IsPublished
            ? result
            : throw new InvalidDataException(
                "hot_publish.metric_snapshot contains an invalid source policy.");
    }

    private static CompiledHostManagerMetricSnapshotRuleTemplate
        CompileMetricSnapshotRuleTemplate(
            HostManagerMetricSnapshotRuleTemplateProfile source,
            IReadOnlyDictionary<
                string,
                CompiledHostManagerMetricSnapshotSourcePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.MetricFlags);
        var sourceId = RequireCanonicalToken(
            source.SourceId,
            "hot_publish.metric_snapshot.rule_templates.source_id");
        if (!policies.TryGetValue(sourceId, out var policy)
            || policy.SourceRole != 1)
        {
            throw new InvalidDataException(
                $"Metric-snapshot rule template source '{sourceId}' is not a metrics source.");
        }

        var (scopeExpansion, scopeKind, placeholder) =
            source.ScopeExpansion switch
            {
                "host" => (1U, 1U, string.Empty),
                "cpu" => (2U, 2U, string.Empty),
                "memory" => (3U, 3U, string.Empty),
                "virtual_memory" => (7U, 3U, string.Empty),
                "gpu_adapter" => (4U, 4U, "{gpu}"),
                "storage_sensor" => (5U, 1U, "{disk}"),
                "system_fan" => (6U, 1U, "{fan}"),
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot scope expansion '{source.ScopeExpansion}'.")
            };
        ValidateMetricIdTemplate(
            source.MetricIdTemplate,
            placeholder,
            "hot_publish.metric_snapshot.rule_templates.metric_id_template");
        var valueKind = source.ValueKind switch
        {
            "float64" => 1U,
            "signed64" => 2U,
            "unsigned64" => 3U,
            _ => throw new InvalidDataException(
                $"Unknown metric-snapshot value kind '{source.ValueKind}'.")
        };
        ValidateMetricValueRange(
            valueKind,
            source.MinimumValueBits,
            source.MaximumValueBits);
        var flags = CompileMetricFlags(source.MetricFlags);
        if ((flags & 1U) != 0 && valueKind != 1U)
        {
            throw new InvalidDataException(
                "Metric-snapshot percentage rules must use float64 values.");
        }
        if ((flags & 0x0CU) == 0x0CU)
        {
            throw new InvalidDataException(
                "Metric-snapshot used_value and total_value flags are mutually exclusive.");
        }

        var result = new CompiledHostManagerMetricSnapshotRuleTemplate(
            RequireCanonicalToken(
                source.TemplateId,
                "hot_publish.metric_snapshot.rule_templates.template_id"),
            source.MetricIdTemplate,
            sourceId,
            policy.SourceHandle,
            policy.Priority,
            scopeExpansion,
            source.MetricKind switch
            {
                "cpu_usage" => 1,
                "cpu_frequency" => 2,
                "cpu_sensor" => 3,
                "memory_used" => 4,
                "memory_total" => 5,
                "virtual_memory_used" => 6,
                "virtual_memory_total" => 7,
                "gpu_usage" => 8,
                "gpu_clock" => 9,
                "gpu_vram_used" => 10,
                "gpu_vram_total" => 11,
                "gpu_sensor" => 12,
                "custom_numeric" => 13,
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot metric kind '{source.MetricKind}'.")
            },
            scopeKind,
            valueKind,
            source.Retention switch
            {
                "retain_last_good" => 1,
                "mark_unavailable" => 2,
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot retention policy '{source.Retention}'.")
            },
            source.CapabilityMask,
            flags,
            source.MinimumValueBits,
            source.MaximumValueBits,
            source.GpuVendorMask,
            source.ApplicabilityMask,
            source.MaximumInstanceCount,
            source.SemanticFingerprint);
        return result.IsPublished
            && (result.CapabilityMask & ~policy.CapabilityMask) == 0
                ? result
                : throw new InvalidDataException(
                    "hot_publish.metric_snapshot contains an invalid rule template.");
    }

    private static uint CompileMetricFlags(IEnumerable<string> values)
    {
        uint result = 0;
        foreach (var value in values)
        {
            var flag = value switch
            {
                "percentage" => 1U,
                "nonnegative" => 2U,
                "used_value" => 4U,
                "total_value" => 8U,
                _ => throw new InvalidDataException(
                    $"Unknown metric-snapshot metric flag '{value}'.")
            };
            if ((result & flag) != 0)
            {
                throw new InvalidDataException(
                    $"Duplicate metric-snapshot metric flag '{value}'.");
            }
            result |= flag;
        }
        return result;
    }

    private static void ValidateMetricIdTemplate(
        string value,
        string requiredPlaceholder,
        string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-' or '_' or '{' or '}')))
        {
            throw new InvalidDataException($"{path} is not canonical.");
        }
        var placeholders = new[] { "{gpu}", "{disk}", "{fan}" };
        foreach (var placeholder in placeholders)
        {
            var count = value.Split(
                placeholder,
                StringSplitOptions.None).Length - 1;
            if (count != (placeholder == requiredPlaceholder ? 1 : 0))
            {
                throw new InvalidDataException(
                    $"{path} does not match scope expansion '{requiredPlaceholder}'.");
            }
        }
        var withoutExpectedPlaceholder = string.IsNullOrEmpty(requiredPlaceholder)
            ? value
            : value.Replace(
                requiredPlaceholder,
                string.Empty,
                StringComparison.Ordinal);
        if (withoutExpectedPlaceholder.Contains('{', StringComparison.Ordinal)
            || withoutExpectedPlaceholder.Contains('}', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{path} contains an unknown placeholder.");
        }
    }

    private static void ValidateMetricSnapshotExpansionCapacity(
        ImmutableArray<CompiledHostManagerMetricSnapshotRuleTemplate> templates,
        CompiledHostManagerMetricSnapshotCapacityPlan capacity)
    {
        ulong ruleCount = 0;
        var metricIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            var instanceCount = template.ScopeExpansion switch
            {
                1 or 2 or 3 or 7 => 1U,
                4 => Math.Min(
                    template.MaximumInstanceCount,
                    capacity.MaximumGpuAdapterCount),
                5 or 6 => template.MaximumInstanceCount,
                _ => throw new InvalidDataException(
                    "Metric-snapshot template has an invalid scope expansion.")
            };
            if (instanceCount == 0 || instanceCount > int.MaxValue)
            {
                throw new InvalidDataException(
                    "Metric-snapshot maximum_instance_count is not representable.");
            }

            ruleCount = checked(ruleCount + instanceCount);
            if (ruleCount > capacity.MaximumRuleCount)
            {
                throw new InvalidDataException(
                    "Metric-snapshot worst-case rule expansion exceeds the recreated capacity.");
            }

            var placeholder = template.ScopeExpansion switch
            {
                4 => "{gpu}",
                5 => "{disk}",
                6 => "{fan}",
                _ => string.Empty
            };
            for (uint index = 0; index < instanceCount; index++)
            {
                var metricId = string.IsNullOrEmpty(placeholder)
                    ? template.MetricIdTemplate
                    : template.MetricIdTemplate.Replace(
                        placeholder,
                        index.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture),
                        StringComparison.Ordinal);
                metricIds.Add(metricId);
                if (metricIds.Count > capacity.MaximumMetricCount)
                {
                    throw new InvalidDataException(
                        "Metric-snapshot worst-case metric expansion exceeds the recreated capacity.");
                }
            }
        }
    }

    private static void ValidateMetricValueRange(
        uint valueKind,
        ulong minimumBits,
        ulong maximumBits)
    {
        var valid = valueKind switch
        {
            1 => double.IsFinite(BitConverter.UInt64BitsToDouble(minimumBits))
                && double.IsFinite(BitConverter.UInt64BitsToDouble(maximumBits))
                && BitConverter.UInt64BitsToDouble(minimumBits)
                    <= BitConverter.UInt64BitsToDouble(maximumBits),
            2 => unchecked((long)minimumBits) <= unchecked((long)maximumBits),
            3 => minimumBits <= maximumBits,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "Metric-snapshot rule template contains an invalid value range.");
        }
    }

    private static string RequireCanonicalToken(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException($"{path} is not a canonical token.");
        }
        return value;
    }
}
