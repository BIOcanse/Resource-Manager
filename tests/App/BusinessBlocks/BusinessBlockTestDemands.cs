using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

internal static class BusinessBlockTestDemands
{
    internal static Task StartAsync(this BusinessBlockScheduler scheduler, string id, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.SetDemandAsync("test:" + id, id, reason, cancellation);
    internal static Task StopAsync(this BusinessBlockScheduler scheduler, string id, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.SetDemandAsync("test:" + id, null, reason, cancellation);
    internal static Task StopDependenciesAsync(this BusinessBlockScheduler scheduler, BusinessBlockReason reason, CancellationToken cancellation = default)
        => scheduler.ShutdownAsync(reason);
}

internal static class BusinessBlockTestSupport
{
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    internal static BusinessBlockReason Reason(BusinessBlockRequestKind kind = BusinessBlockRequestKind.User, string owner = "test", string cause = "explicit", long version = 7)
        => new(kind, owner, cause, version);
    internal static BusinessBlockScheduler Create(IEnumerable<BusinessBlockRegistration> registrations,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, int historyLimit = 128, ILogger<BusinessBlockScheduler>? logger = null)
        => new(new EmptyServices(), registrations, Options.Create(new BusinessBlockOptions { HistoryLimit = historyLimit }),
            logger ?? NullLogger<BusinessBlockScheduler>.Instance, decisions: decisions);
    internal static BusinessBlockRegistration Register(string id, IBusinessBlock block, string parent = BackendBlockGraph.Program, params string[] inputs)
        => new(id, inputs, _ => block) { ParentId = parent, Outputs = [id + ":output"] };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    internal static Task State(BusinessBlockScheduler scheduler, string id, BusinessBlockState state)
        => Until(() => scheduler.ReadState(id) == state);
    internal static async Task StopAll(BusinessBlockScheduler scheduler)
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
