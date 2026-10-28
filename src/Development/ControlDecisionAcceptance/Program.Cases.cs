using System.Diagnostics;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using BusinessBlockScheduler = ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private sealed record AcceptanceCaseResult(string Name, string Status, double ElapsedMilliseconds,
        string? Error, string? Message, string? StackTrace);
    private sealed record TopologyCaseEvidence(TopologyPublication First, TopologyPublication Resumed);

    private static async Task<bool> RunCaseAsync(List<AcceptanceCaseResult> cases, string evidence, string name,
        TimeSpan budget, Func<CancellationToken, Task> action, CancellationToken parent,
        Func<BusinessBlockScheduler?>? readScheduler = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(parent);
        deadline.CancelAfter(budget);
        var started = Stopwatch.GetTimestamp();
        AcceptanceCaseResult result;
        try
        {
            await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            result = new(name, "passed", Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, null, null);
        }
        catch (Exception error)
        {
            result = new(name, deadline.IsCancellationRequested ? "timed-out" : "failed",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, error.GetType().FullName,
                error.Message, error.ToString());
            Console.Error.WriteLine($"Acceptance case {name} failed: {error}");
            if (readScheduler?.Invoke() is { } scheduler)
            {
                try { await SaveAsync(evidence, $"case-{name}-controller.json", scheduler.Read()); }
                catch (Exception diagnosticError)
                { await SaveAsync(evidence, $"case-{name}-snapshot-error.json", new { diagnosticError.Message }); }
            }
        }
        cases.Add(result);
        await SaveAsync(evidence, $"case-{name}.json", result);
        return result.Status == "passed";
    }

    private static async Task RecordNotRunAsync(List<AcceptanceCaseResult> cases, string evidence,
        string name, string reason)
    {
        var result = new AcceptanceCaseResult(name, "not-run", 0, null, reason, null);
        cases.Add(result);
        await SaveAsync(evidence, $"case-{name}.json", result);
    }

    private static async Task RecordFailureAsync(List<AcceptanceCaseResult> cases, string evidence,
        string name, Exception error)
    {
        var result = new AcceptanceCaseResult(name, "failed", 0, error.GetType().FullName,
            error.Message, error.ToString());
        cases.Add(result);
        Console.Error.WriteLine(error);
        await SaveAsync(evidence, $"case-{name}.json", result);
    }

    private static async Task<TopologyCaseEvidence> VerifyTopologyAsync(IServiceProvider services,
        BusinessBlockScheduler scheduler, string evidence, CancellationToken token)
    {
        var topology = services.GetRequiredService<CpuTopologySnapshotProvider>();
        if (!ReferenceEquals(topology, services.GetRequiredService<ICpuTopologyReader>()))
            throw new InvalidOperationException("The probe must use the registered production CPU topology reader.");
        var rolePolicy = services.GetRequiredService<HostManagerSamplingSubscriptionOwner>().GetRoleSettings(7)
            ?? throw new InvalidOperationException("Role 7 policy is not published.");
        var interval = TimeSpan.FromMilliseconds(rolePolicy.MinimumIntervalMilliseconds);
        await SaveAsync(evidence, "topology-role-policy.json", rolePolicy);
        await WaitAsync(() => scheduler.Read().Blocks.Single(block => block.Id == BlockUids.CpuTopologySnapshotProvider)
            .State == BusinessBlockState.Stopped && ReadResources(topology).Returned, token);
        var baseline = ReadResources(topology);
        if (!baseline.Returned) throw new InvalidOperationException("Role 7 has baseline sampling resources.");
        await SaveAsync(evidence, "topology-baseline-resources.json", baseline);
        await SaveAsync(evidence, "topology-controller-before.json", scheduler.Read());
        var first = await CaptureTopologyAsync(topology, "real-copy:first", interval, null, token);
        await SaveAsync(evidence, "topology-first.json", first);
        await WaitAsync(() => scheduler.Read().Blocks.Single(block => block.Id == BlockUids.CpuTopologySnapshotProvider)
            .State == BusinessBlockState.Stopped && ReadResources(topology).Returned, token);
        await SaveAsync(evidence, "topology-released-resources.json", ReadResources(topology));
        await SaveAsync(evidence, "topology-controller-after-release.json", scheduler.Read());
        var resumed = await CaptureTopologyAsync(topology, "real-copy:resumed", interval, first.Snapshot.CapturedAt, token);
        await SaveAsync(evidence, "topology-resumed.json", resumed);
        await WaitAsync(() => scheduler.Read().Blocks.Single(block => block.Id == BlockUids.CpuTopologySnapshotProvider)
            .State == BusinessBlockState.Stopped && ReadResources(topology).Returned, token);
        await SaveAsync(evidence, "topology-final-resources.json", ReadResources(topology));
        await SaveAsync(evidence, "topology-controller-final.json", scheduler.Read());
        return new(first, resumed);
    }
}