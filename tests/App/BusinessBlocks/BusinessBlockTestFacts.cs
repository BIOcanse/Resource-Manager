using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;

namespace Resource_Manager_APP.Tests;

internal static class BusinessBlockTestFacts
{
    internal static Task StartAsync(this TestBlockScheduler scheduler, int id, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.PublishExecutionAsync(id, true, reason, cancellation);
    internal static Task StopAsync(this TestBlockScheduler scheduler, int id, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.PublishExecutionAsync(id, false, reason, cancellation);
    internal static Task StopDependenciesAsync(this SharedScheduler scheduler, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.ShutdownAsync(reason);
}

internal static class BusinessBlockTestSupport
{
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    internal static BusinessBlockReason Reason(BusinessBlockRequestKind kind = BusinessBlockRequestKind.User, string owner = "test", string cause = "explicit", long version = 7)
        => new(kind, owner, cause, version);
    internal static TestBlockScheduler Create(IEnumerable<TestBlockRegistration> registrations,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, int historyLimit = 128, ILogger? logger = null)
    {
        var rows = registrations.ToArray();
        var graph = TestBlockScheduler.CreateGraph(rows);
        var authority = CreateAuthority(graph.BlockIds);
#if RM_STATE_NAMES
        Func<int, string?> nameLookup = ResourceManager.App.StateItems.StateNames.Name;
#else
        Func<int, string?> nameLookup = static _ => null;
#endif
        return new(rows, authority, historyLimit, logger ?? NullLogger<SharedScheduler>.Instance,
            decisions: decisions, nameLookup: nameLookup);
    }
    internal static StateItemKey ExecutionKey(int id) => new(TestStateUids.ExecutionEnabled, id);
    internal static StateItemKey GateKey(int id) => new(TestStateUids.Unfrozen, id);
    internal static StateAuthority CreateAuthority(IEnumerable<int>? blocks = null)
        => new(ResourceManager.App.StateItems.StateRegistry.Items.Concat(TestStateUids.Declarations),
            ResourceManager.App.StateItems.StateRegistry.Blocks.Concat(blocks ?? Enumerable.Range(101, 59)).Distinct());
    internal static void AddStateFixtures(IServiceCollection services)
        => services.AddSingleton(_ => CreateAuthority());
    internal static TestBlockRegistration Register(int id, IBusinessBlock block, int parent = BackendUids.Program, params int[] inputs)
        => new(id, inputs, _ => block) { ParentId = parent };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    internal static Task State(SharedScheduler scheduler, int id, BusinessBlockState state)
        => Until(() => scheduler.ReadState(id) == state);
    internal static async Task StopAll(SharedScheduler scheduler)
    {
        await scheduler.ShutdownAsync(Reason()).WaitAsync(TestTimeout);
        await Until(() => scheduler.Read().Blocks.All(value => value.State == BusinessBlockState.Stopped));
    }
    internal sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
    internal sealed class Block(Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context) => run(context);
    }
    internal sealed class Probe(string id = "worker", ConcurrentQueue<string>? events = null, bool reportRunning = true, Task? releaseStop = null) : IBusinessBlock
    {
        private int runs;
        private int returns;
        public int Runs => Volatile.Read(ref runs);
        public int Returns => Volatile.Read(ref returns);
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Notified { get; } = Signal();
        public TaskCompletionSource Returned { get; } = Signal();
        public BusinessBlockRunContext? Context { get; private set; }
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Context = context;
            Interlocked.Increment(ref runs);
            events?.Enqueue(id + ":start");
            if (reportRunning) context.ReportRunning();
            Entered.TrySetResult();
            using var notification = context.StopNotification.Register(() => Notified.TrySetResult());
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            if (releaseStop is not null) await releaseStop;
            events?.Enqueue(id + ":return");
            Interlocked.Increment(ref returns);
            Returned.TrySetResult();
        }
    }
}
