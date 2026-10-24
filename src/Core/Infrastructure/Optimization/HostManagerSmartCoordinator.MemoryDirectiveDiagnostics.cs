using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    // Only the current cycle's execution diagnostics; ownership remains in the existing ledgers.
    private readonly Dictionary<ulong, MemoryDirectiveOutcome> memoryDirectiveOutcomes = [];
    private readonly HashSet<string> loggedMemoryDirectiveSkipReasons = new(StringComparer.Ordinal);

    private readonly record struct MemoryDirectiveOutcome(bool TransactionPrepared, string? SkipReason);

    private MemoryDirectiveOutcome GetMemoryDirectiveOutcome(HostManagerNonAdaptedMemoryProcessDirective directive)
        => memoryDirectiveOutcomes.TryGetValue(directive.MemoryPolicyTargetKey, out var outcome)
            ? outcome : new(false, "memory_stage_not_reached");

    private void RecordMemoryDirectiveSkip(HostManagerNonAdaptedMemoryProcessDirective directive, string reason)
        => memoryDirectiveOutcomes[directive.MemoryPolicyTargetKey] = new(false, reason);

    private void RecordMemoryDirectivePrepared(HostManagerNonAdaptedMemoryProcessDirective directive)
        => memoryDirectiveOutcomes[directive.MemoryPolicyTargetKey] = new(true, null);

    private void RecordRemainingMemoryDirectiveSkips(string reason)
    {
        if (NonAdaptedMemoryModeProjection is not { } projection) return;
        foreach (var directive in projection.Processes)
            if (!memoryDirectiveOutcomes.ContainsKey(directive.MemoryPolicyTargetKey))
                RecordMemoryDirectiveSkip(directive, reason);
    }

    private IReadOnlyList<HostManagerMemoryDirectiveSkipDiagnostics> ProjectMemoryDirectiveSkipCounts()
        => NonAdaptedMemoryModeProjection is not { } projection ? [] : projection.Processes
            .Select(directive => (Directive: directive, Outcome: GetMemoryDirectiveOutcome(directive)))
            .Where(item => item.Outcome.SkipReason is not null)
            .GroupBy(item => item.Outcome.SkipReason!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new HostManagerMemoryDirectiveSkipDiagnostics(group.Key, group.Count(),
                group.Take(5).Select(item => HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                    item.Directive.ProcessId, item.Directive.ProcessStartKey)).ToArray())).ToArray();

    private void CompleteMemoryDirectiveDiagnostics()
    {
        var counts = ProjectMemoryDirectiveSkipCounts();
        var active = counts.Select(count => count.Reason).ToHashSet(StringComparer.Ordinal);
        loggedMemoryDirectiveSkipReasons.IntersectWith(active);
        foreach (var count in counts)
            if (loggedMemoryDirectiveSkipReasons.Add(count.Reason))
                logger.LogInformation("Host Manager memory directives skipped: {Reason}; count {Count}; targets {Targets}.",
                    count.Reason, count.Count, string.Join(", ", count.ExampleTargetIds));
    }
}
