using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal readonly record struct NativeMetricSnapshotGpuCatalogIdentity(
    int DisplayIndex,
    string DisplayName,
    string ExactIdentityKey,
    ulong AdapterLuid,
    ulong InventoryGeneration,
    uint VendorMask,
    bool SupportsDedicatedMetrics);

internal readonly record struct NativeMetricSnapshotOrdinalCatalogIdentity(
    int DisplayIndex,
    string ExactIdentityKey,
    ulong TopologyGeneration);

internal readonly record struct NativeMetricSnapshotGpuScopeBinding(
    int DisplayIndex,
    string DisplayName,
    ulong ScopeHandle,
    ulong AdapterLuid,
    ulong InventoryGeneration,
    string ExactIdentityKey,
    bool SupportsDedicatedMetrics);

internal readonly record struct NativeMetricSnapshotRuleCatalogBinding(
    ulong RuleHandle,
    ulong MetricHandle,
    ulong SourceHandle,
    ulong ScopeHandle,
    string MetricId,
    string SourceId,
    uint MetricKind,
    uint ValueKind,
    ulong CapabilityMask,
    uint MetricFlags,
    ulong MinimumValueBits,
    ulong MaximumValueBits);

internal sealed record NativeMetricSnapshotCatalogProjection(
    ImmutableArray<NativeMetricSnapshotSourcePolicyInput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricDefinitionInput> Rules,
    ImmutableDictionary<ulong, string> MetricIds,
    ImmutableDictionary<string, ulong> MetricHandles,
    ImmutableDictionary<ulong, string> SourceIds,
    ImmutableDictionary<string, ulong> SourceHandles,
    ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding>
        RuleBindings,
    ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>
        GpuScopeBindings,
    NativeMetricSnapshotCatalogHandleMap HandleMap,
    ulong NativeRowFingerprint,
    string CatalogIdentitySha256)
{
    internal static NativeMetricSnapshotCatalogProjection Create(
        CompiledHostManagerMetricSnapshotPlan plan,
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> gpuAdapters,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> storageSensors,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> systemFans,
        NativeMetricSnapshotCatalogHandleMap handleMap)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gpuAdapters);
        ArgumentNullException.ThrowIfNull(storageSensors);
        ArgumentNullException.ThrowIfNull(systemFans);
        ArgumentNullException.ThrowIfNull(handleMap);
        if (!plan.IsPublished)
        {
            throw new ArgumentException(
                "The metric-snapshot plan must be published.",
                nameof(plan));
        }

        ValidateGpuIdentities(
            gpuAdapters,
            plan.Recreate.Capacity.MaximumGpuAdapterCount);
        ValidateOrdinalIdentities(storageSensors, "storage sensor");
        ValidateOrdinalIdentities(systemFans, "system fan");
        var allocator = handleMap.CreateAllocator();
        foreach (var adapter in gpuAdapters.OrderBy(
                     static adapter => adapter.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"gpu|{adapter.ExactIdentityKey}");
        }
        foreach (var identity in storageSensors.OrderBy(
                     static identity => identity.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"storage|{identity.ExactIdentityKey}");
        }
        foreach (var identity in systemFans.OrderBy(
                     static identity => identity.ExactIdentityKey,
                     StringComparer.Ordinal))
        {
            _ = allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"fan|{identity.ExactIdentityKey}");
        }
        var resolvedGpuAdapters = gpuAdapters
            .Select(adapter => new ResolvedGpuIdentity(
                adapter.DisplayIndex,
                adapter.DisplayName,
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Scope,
                    $"gpu|{adapter.ExactIdentityKey}"),
                adapter.ExactIdentityKey,
                adapter.AdapterLuid,
                adapter.InventoryGeneration,
                adapter.VendorMask,
                adapter.SupportsDedicatedMetrics))
            .ToArray();
        var resolvedStorageSensors = storageSensors
            .Select(identity => ResolveOrdinalIdentity(
                allocator,
                identity,
                "storage"))
            .ToArray();
        var resolvedSystemFans = systemFans
            .Select(identity => ResolveOrdinalIdentity(
                allocator,
                identity,
                "fan"))
            .ToArray();

        var sources = plan.HotPublish.SourcePolicies
            .OrderBy(static policy => policy.SourceHandle)
            .Select(static policy => new NativeMetricSnapshotSourcePolicyInput
            {
                StructSize = SizeOf<NativeMetricSnapshotSourcePolicyInput>(),
                Flags = policy.Required
                    ? (uint)NativeMetricSnapshotSourceFlags.Required
                    : 0U,
                SourceHandle = policy.SourceHandle,
                SourceRole = policy.SourceRole,
                Priority = policy.Priority,
                RetentionPolicy = policy.RetentionPolicy,
                ReservedU32 = 0,
                CapabilityMask = policy.CapabilityMask,
                SemanticFingerprint = policy.SemanticFingerprint
            })
            .ToImmutableArray();
        var expanded = plan.HotPublish.RuleTemplates
            .SelectMany(template => ExpandTemplate(
                template,
                resolvedGpuAdapters,
                resolvedStorageSensors,
                resolvedSystemFans))
            .ToArray();
        if (expanded.Length == 0
            || expanded.Length > plan.Recreate.Capacity.MaximumRuleCount)
        {
            throw new InvalidOperationException(
                "The expanded metric-snapshot catalog has an invalid rule count.");
        }
        ValidateMetricIdentities(expanded);
        var allocated = expanded.Select(rule => new AllocatedRule(
                rule,
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Metric,
                    rule.StableMetricKey),
                allocator.GetOrAdd(
                    NativeMetricSnapshotCatalogHandleKind.Rule,
                    rule.StableRuleKey)))
            .OrderBy(static rule => rule.MetricHandle)
            .ThenBy(static rule => rule.Rule.Template.SourcePriority)
            .ThenBy(static rule => rule.RuleHandle)
            .ToArray();
        var finalHandleMap = allocator.Freeze();

        var metricIds = ImmutableDictionary.CreateBuilder<ulong, string>();
        var metricHandles =
            ImmutableDictionary.CreateBuilder<string, ulong>(
                StringComparer.OrdinalIgnoreCase);
        var sourceIds = plan.HotPublish.SourcePolicies
            .ToImmutableDictionary(
                static policy => policy.SourceHandle,
                static policy => policy.SourceId);
        var sourceHandles = plan.HotPublish.SourcePolicies
            .ToImmutableDictionary(
                static policy => policy.SourceId,
                static policy => policy.SourceHandle,
                StringComparer.Ordinal);
        var ruleBindings = ImmutableDictionary.CreateBuilder<
            ulong,
            NativeMetricSnapshotRuleCatalogBinding>();
        var rules = ImmutableArray.CreateBuilder<
            NativeMetricSnapshotMetricDefinitionInput>(expanded.Length);
        foreach (var allocatedRule in allocated)
        {
            var rule = allocatedRule.Rule;
            if (metricIds.TryGetValue(
                    allocatedRule.MetricHandle,
                    out var existingMetricId))
            {
                if (!string.Equals(
                    existingMetricId,
                    rule.MetricId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A stable metric handle resolved to two display metric identifiers.");
                }
            }
            else
            {
                metricIds.Add(
                    allocatedRule.MetricHandle,
                    rule.MetricId);
            }
            if (metricHandles.TryGetValue(
                    rule.MetricId,
                    out var existingMetricHandle))
            {
                if (existingMetricHandle != allocatedRule.MetricHandle)
                {
                    throw new InvalidOperationException(
                        "A display metric identifier resolved to multiple stable metric handles.");
                }
            }
            else
            {
                metricHandles.Add(
                    rule.MetricId,
                    allocatedRule.MetricHandle);
            }
            ruleBindings.Add(
                allocatedRule.RuleHandle,
                new NativeMetricSnapshotRuleCatalogBinding(
                    allocatedRule.RuleHandle,
                    allocatedRule.MetricHandle,
                    rule.Template.SourceHandle,
                    rule.ScopeHandle,
                    rule.MetricId,
                     rule.Template.SourceId,
                     rule.Template.MetricKind,
                     rule.Template.ValueKind,
                     rule.Template.CapabilityMask,
                     rule.Template.MetricFlags,
                     rule.Template.MinimumValueBits,
                     rule.Template.MaximumValueBits));
            rules.Add(new NativeMetricSnapshotMetricDefinitionInput
            {
                StructSize =
                    SizeOf<NativeMetricSnapshotMetricDefinitionInput>(),
                Flags = rule.Template.MetricFlags,
                RuleHandle = allocatedRule.RuleHandle,
                MetricHandle = allocatedRule.MetricHandle,
                SourceHandle = rule.Template.SourceHandle,
                ScopeHandle = rule.ScopeHandle,
                CapabilityMask = rule.Template.CapabilityMask,
                MetricKind = rule.Template.MetricKind,
                ScopeKind = rule.Template.ScopeKind,
                ValueKind = rule.Template.ValueKind,
                RetentionPolicy = rule.Template.RetentionPolicy,
                SourcePriority = rule.Template.SourcePriority,
                ReservedU32 = 0,
                MinimumValueBits = rule.Template.MinimumValueBits,
                MaximumValueBits = rule.Template.MaximumValueBits,
                SemanticFingerprint = ExpandedFingerprint(
                    rule.Template.SemanticFingerprint,
                    rule.ScopeHandle)
            });
        }

        if (metricIds.Count > plan.Recreate.Capacity.MaximumMetricCount)
        {
            throw new InvalidOperationException(
                "The expanded metric-snapshot catalog has too many metrics.");
        }
        var sourceRows = sources.ToArray();
        var ruleRows = rules.ToArray();
        var nativeFingerprint =
            NativeMetricSnapshotSession.CalculateCatalogFingerprint(
                sourceRows,
                ruleRows);
        var activeTopologyKeys = resolvedGpuAdapters
            .Select(static identity => $"gpu|{identity.ExactIdentityKey}")
            .Concat(resolvedStorageSensors.Select(
                static identity => $"storage|{identity.ExactIdentityKey}"))
            .Concat(resolvedSystemFans.Select(
                static identity => $"fan|{identity.ExactIdentityKey}"))
            .ToArray();
        var metricIdMap = metricIds.ToImmutable();
        var catalogIdentitySha256 =
            NativeMetricSnapshotCatalogIdentity.ComputeSha256(
                plan.HotPublish.CatalogManifestSha256,
                nativeFingerprint,
                finalHandleMap,
                metricIdMap,
                activeTopologyKeys);
        var gpuScopeBindings = resolvedGpuAdapters.ToImmutableDictionary(
            static identity => identity.ScopeHandle,
            static identity => new NativeMetricSnapshotGpuScopeBinding(
                identity.DisplayIndex,
                identity.DisplayName,
                identity.ScopeHandle,
                identity.AdapterLuid,
                identity.InventoryGeneration,
                identity.ExactIdentityKey,
                identity.SupportsDedicatedMetrics));
        return new NativeMetricSnapshotCatalogProjection(
            sources,
            rules.MoveToImmutable(),
            metricIdMap,
            metricHandles.ToImmutable(),
            sourceIds,
            sourceHandles,
            ruleBindings.ToImmutable(),
            gpuScopeBindings,
            finalHandleMap,
            nativeFingerprint,
            catalogIdentitySha256);
    }

    private static IEnumerable<ExpandedRule> ExpandTemplate(
        CompiledHostManagerMetricSnapshotRuleTemplate template,
        IReadOnlyList<ResolvedGpuIdentity> gpuAdapters,
        IReadOnlyList<ResolvedOrdinalIdentity> storageSensors,
        IReadOnlyList<ResolvedOrdinalIdentity> systemFans)
    {
        return template.ScopeExpansion switch
        {
            1 => [CreateStatic(
                template,
                HostScopeHandle,
                "static|host")],
            2 => [CreateStatic(
                template,
                CpuScopeHandle,
                "static|cpu")],
            3 => [CreateStatic(
                template,
                MemoryScopeHandle,
                "static|memory")],
            7 => [CreateStatic(
                template,
                VirtualMemoryScopeHandle,
                "static|virtual-memory")],
            4 => ExpandBounded(
                template,
                gpuAdapters.Where(adapter =>
                    (template.GpuVendorMask & adapter.VendorMask) != 0
                    && ((template.ApplicabilityMask & DedicatedOnly) == 0
                        || adapter.SupportsDedicatedMetrics)),
                "{gpu}"),
            5 => ExpandBounded(template, storageSensors, "{disk}"),
            6 => ExpandBounded(template, systemFans, "{fan}"),
            _ => throw new InvalidOperationException(
                "The compiled metric-snapshot scope expansion is invalid.")
        };
    }

    private static ExpandedRule CreateStatic(
        CompiledHostManagerMetricSnapshotRuleTemplate template,
        ulong scopeHandle,
        string exactScopeKey)
        => new(
            template.MetricIdTemplate,
            scopeHandle,
            CreateMetricIdentity(template, scopeHandle),
            $"metric|{template.MetricIdTemplate}|{exactScopeKey}",
            $"rule|{template.TemplateId}|{exactScopeKey}",
            template);

    private static ExpandedRule CreateIndexed(
        CompiledHostManagerMetricSnapshotRuleTemplate template,
        string placeholder,
        int index,
        ulong scopeHandle,
        string exactScopeKey)
        => new(
            template.MetricIdTemplate.Replace(
                placeholder,
                index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            scopeHandle,
            CreateMetricIdentity(template, scopeHandle),
            $"metric|{template.MetricIdTemplate}|{exactScopeKey}",
            $"rule|{template.TemplateId}|{exactScopeKey}",
            template);

    private static IEnumerable<ExpandedRule> ExpandBounded<T>(
        CompiledHostManagerMetricSnapshotRuleTemplate template,
        IEnumerable<T> source,
        string placeholder)
        where T : IResolvedOrdinalIdentity
    {
        var values = source
            .OrderBy(static value => value.DisplayIndex)
            .ToArray();
        if (values.Length > template.MaximumInstanceCount)
        {
            throw new InvalidOperationException(
                $"Metric-snapshot template '{template.TemplateId}' exceeds its maximum instance count.");
        }
        return values.Select(identity => CreateIndexed(
            template,
            placeholder,
            identity.DisplayIndex,
            identity.ScopeHandle,
            identity.ExactIdentityKey));
    }

    private static ResolvedOrdinalIdentity ResolveOrdinalIdentity(
        NativeMetricSnapshotCatalogHandleMap.Allocator allocator,
        NativeMetricSnapshotOrdinalCatalogIdentity identity,
        string kind)
        => new(
            identity.DisplayIndex,
            allocator.GetOrAdd(
                NativeMetricSnapshotCatalogHandleKind.Scope,
                $"{kind}|{identity.ExactIdentityKey}"),
            identity.ExactIdentityKey,
            identity.TopologyGeneration);

    private static NativeMetricIdentityKey CreateMetricIdentity(
        CompiledHostManagerMetricSnapshotRuleTemplate template,
        ulong scopeHandle)
        => new(
            scopeHandle,
            template.MetricKind,
            template.ScopeKind,
            template.ValueKind,
            template.MetricFlags,
            template.MinimumValueBits,
            template.MaximumValueBits);

    private static void ValidateMetricIdentities(
        IReadOnlyList<ExpandedRule> expanded)
    {
        foreach (var group in expanded.GroupBy(
            static rule => rule.MetricId,
            StringComparer.Ordinal))
        {
            var expected = group.First().MetricIdentity;
            var conflict = group.FirstOrDefault(
                rule => rule.MetricIdentity != expected);
            if (conflict is not null)
            {
                var templates = string.Join(
                    ", ",
                    group.Select(static rule => rule.Template.TemplateId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal));
                throw new InvalidOperationException(
                    $"Metric-snapshot metric '{group.Key}' has conflicting native identity across templates: {templates}.");
            }
        }
        foreach (var group in expanded.GroupBy(
            static rule => rule.StableMetricKey,
            StringComparer.Ordinal))
        {
            if (group.Select(static rule => rule.MetricId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                != 1)
            {
                throw new InvalidOperationException(
                    "A stable metric identity resolved to multiple display identifiers.");
            }
        }
    }

    private static void ValidateGpuIdentities(
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> values,
        uint maximumCount)
    {
        if (values.Count > maximumCount
            || values.Any(static value =>
                value.DisplayIndex < 0
                || string.IsNullOrWhiteSpace(value.DisplayName)
                || !IsExactIdentityKey(value.ExactIdentityKey)
                || value.AdapterLuid == 0
                || value.InventoryGeneration == 0
                || value.VendorMask is 0 or > 0x08U
                || (value.VendorMask & (value.VendorMask - 1)) != 0)
            || values.Select(static value => value.DisplayIndex)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.ExactIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != values.Count
            || values.Select(static value => value.AdapterLuid)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.InventoryGeneration)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                "The metric-snapshot GPU catalog identities are invalid.");
        }
    }

    private static void ValidateOrdinalIdentities(
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> values,
        string name)
    {
        if (values.Any(static value =>
                value.DisplayIndex < 0
                || !IsExactIdentityKey(value.ExactIdentityKey)
                || value.TopologyGeneration == 0)
            || values.Select(static value => value.DisplayIndex)
                .Distinct()
                .Count() != values.Count
            || values.Select(static value => value.ExactIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != values.Count
            || values.Select(static value => value.TopologyGeneration)
                .Distinct()
                .Count() > 1)
        {
            throw new InvalidOperationException(
                $"The metric-snapshot {name} catalog identities are invalid.");
        }
    }

    private static ulong ExpandedFingerprint(
        ulong templateFingerprint,
        ulong scopeHandle)
    {
        var result = templateFingerprint
            ^ (scopeHandle + 0x9E37_79B9_7F4A_7C15UL
                + (templateFingerprint << 6)
                + (templateFingerprint >> 2));
        return result == 0 ? 1UL : result;
    }

    private static uint SizeOf<T>()
        where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static bool IsExactIdentityKey(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value == value.Trim()
            && !value.Contains('\0')
            && System.Text.Encoding.UTF8.GetByteCount(value)
                <= NativeMetricSnapshotCatalogHandleMap
                    .MaximumExactKeyByteCount;

    private const uint DedicatedOnly = 1U;
    private const ulong HostScopeHandle = 0x1000_0000_0000_0001UL;
    private const ulong CpuScopeHandle = 0x1000_0000_0000_0002UL;
    private const ulong MemoryScopeHandle = 0x1000_0000_0000_0003UL;
    private const ulong VirtualMemoryScopeHandle = 0x1000_0000_0000_0004UL;

    private sealed record ExpandedRule(
        string MetricId,
        ulong ScopeHandle,
        NativeMetricIdentityKey MetricIdentity,
        string StableMetricKey,
        string StableRuleKey,
        CompiledHostManagerMetricSnapshotRuleTemplate Template);

    private sealed record AllocatedRule(
        ExpandedRule Rule,
        ulong MetricHandle,
        ulong RuleHandle);

    private interface IResolvedOrdinalIdentity
    {
        int DisplayIndex { get; }

        ulong ScopeHandle { get; }

        string ExactIdentityKey { get; }
    }

    private readonly record struct ResolvedGpuIdentity(
        int DisplayIndex,
        string DisplayName,
        ulong ScopeHandle,
        string ExactIdentityKey,
        ulong AdapterLuid,
        ulong InventoryGeneration,
        uint VendorMask,
        bool SupportsDedicatedMetrics) : IResolvedOrdinalIdentity;

    private readonly record struct ResolvedOrdinalIdentity(
        int DisplayIndex,
        ulong ScopeHandle,
        string ExactIdentityKey,
        ulong TopologyGeneration) : IResolvedOrdinalIdentity;

    private readonly record struct NativeMetricIdentityKey(
        ulong ScopeHandle,
        uint MetricKind,
        uint ScopeKind,
        uint ValueKind,
        uint Flags,
        ulong MinimumValueBits,
        ulong MaximumValueBits);
}
