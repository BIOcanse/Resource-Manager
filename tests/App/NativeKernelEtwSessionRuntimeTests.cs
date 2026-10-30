using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwSessionRuntimeTests
{
    [Fact]
    public void StandaloneRegionsAreExplicitlyBoundedAndCountRetainedAllocations()
    {
        var provider = new BoundedKernelEtwRegionProvider(1024);
        using var first = provider.Allocate("fixture", 768);
        first.Complete();
        Assert.Throws<InvalidOperationException>(() => provider.Allocate("fixture", 257));
        Assert.Equal(new KernelEtwRegionAllocationUsage(768, 768, 1024), provider.GetUsage());
        first.Dispose();
        Assert.Equal(new KernelEtwRegionAllocationUsage(0, 768, 1024), provider.GetUsage());
        using var second = provider.Allocate("fixture", 1024);
        Assert.Equal(new KernelEtwRegionAllocationUsage(1024, 1024, 1024), provider.GetUsage());
    }

    [Fact]
    public void SharedConfigurationMatchesTheNativeAbiLayout()
    {
        Assert.Equal(56, System.Runtime.InteropServices.Marshal.SizeOf<NativeKernelEtwResidencyConfig>());
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<NativeKernelEtwInterruptConfig>());
        Assert.Equal(272, System.Runtime.InteropServices.Marshal.SizeOf<NativeKernelEtwSessionConfig>());
    }

    [Fact]
    public void StandaloneSdkUsesTheExplicitRuntimeFactoryOnlyAtItsCaptureRunEntry()
    {
        var factory = new UnavailableFactory();
        using var owner = ResourceManager.ReadWrite.Windows.Processes.WindowsProcessFieldOwner.CreateDefault(
            kernelEtwRuntimeFactory: factory);
        Assert.Null(owner.ReadCachedSnapshot());
        Assert.Equal(0, factory.AvailabilityChecks);
        owner.StartNetworkCapture();
        Assert.Equal(1, factory.AvailabilityChecks); // One native network-output hold at run entry.
        owner.ReadCachedSnapshot();
        Assert.Equal(1, factory.AvailabilityChecks);
        owner.StopNetworkCapture();
    }

    [Fact]
    public void FailedProviderStopDoesNotUnblockJoinOrReleaseAndCanBeRetried()
    {
        var session = new Session { StopFailure = new UnauthorizedAccessException("denied") };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        var failure = runtime.Stop(Timeout.InfiniteTimeSpan);
        Assert.False(failure.IsReleased);
        Assert.True(failure.ControlDenied);
        Assert.Equal(new[] { "provider" }, session.Calls);
        session.StopFailure = null;
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);
        Assert.Equal(new[] { "provider", "provider", "close", "dispose" }, session.Calls);
        runtime.Dispose();
        Assert.Equal(4, session.Calls.Count);
    }

    [Fact]
    public async Task FailedUnblockDoesNotJoinOrReleaseAndDoesNotRepeatSuccessfulProviderStop()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var session = new Session
        {
            ProcessBody = () => { entered.Set(); finish.Wait(); },
            UnblockFailure = new InvalidOperationException("close failed"),
            UnblockBody = () => finish.Set()
        };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        runtime.StartProcessing(static _ => { });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task<KernelEtwSessionStopResult>? failedRetry = null;
        try
        {
            Assert.False(runtime.Stop(TimeSpan.Zero).IsReleased);
            Assert.Equal(new[] { "provider", "unblock" }, session.Calls);
            // Once force-close has failed, even an unlimited retry must fail
            // before joining the still-blocked consumer. Bound the assertion,
            // and release the consumer in finally if this contract regresses.
            failedRetry = Task.Run(() => runtime.Stop(Timeout.InfiniteTimeSpan));
            Assert.False((await failedRetry.WaitAsync(TimeSpan.FromSeconds(5))).IsReleased);
            Assert.Equal(new[] { "provider", "unblock", "unblock" }, session.Calls);
            session.UnblockFailure = null;
            Assert.True(runtime.Stop(TimeSpan.FromSeconds(5)).IsReleased);
            Assert.Equal(new[] { "provider", "unblock", "unblock", "unblock", "close", "dispose" }, session.Calls);
        }
        finally
        {
            finish.Set();
            session.UnblockFailure = null;
            if (failedRetry is not null) await failedRetry.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.Dispose();
        }
    }

    [Fact]
    public void ProviderStopDrainsTheConsumerAndObserverBeforeClosingWithoutCancellation()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var observer = new Observer();
        var session = new Session
        {
            ProcessBody = () => { entered.Set(); finish.Wait(); },
            StopBody = () => finish.Set(),
            CloseBody = () => Assert.True(observer.Returned)
        };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session, observer: observer);
        runtime.StartProcessing(static _ => { });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(runtime.Stop(TimeSpan.FromSeconds(5)).IsReleased);
            Assert.Equal(new[] { "provider", "close", "dispose" }, session.Calls);
            Assert.True(observer.Returned);
        }
        finally { finish.Set(); runtime.Dispose(); }
    }

    [Fact]
    public void OutstandingConsumerRetainsStateUntilObservedThreadHasExited()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var callback = new ManualResetEventSlim();
        var observer = new Observer();
        var session = new Session { ProcessBody = () => { entered.Set(); finish.Wait(); } };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session, observer: observer);
        runtime.StartProcessing(_ => callback.Set());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(runtime.Stop(TimeSpan.Zero).IsReleased);
            Assert.Equal(new[] { "provider", "unblock" }, session.Calls);
            Assert.False(observer.Returned);
        }
        finally { finish.Set(); }
        Assert.True(callback.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(runtime.Stop(TimeSpan.FromSeconds(5)).IsReleased);
        Assert.True(observer.Returned);
        Assert.NotEqual(Environment.CurrentManagedThreadId, observer.ManagedThreadId);
        Assert.Equal(new[] { "provider", "unblock", "close", "dispose" }, session.Calls);
    }

    [Fact]
    public void FailedNativeReleaseIsRetriedWithoutRepeatingStopUnblockOrClose()
    {
        var session = new Session { DisposeFailure = new InvalidOperationException("view retained") };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        Assert.False(runtime.Stop(TimeSpan.Zero).IsReleased);
        Assert.Equal(new[] { "provider", "close", "dispose" }, session.Calls);
        session.DisposeFailure = null;
        runtime.Dispose();
        Assert.Equal(new[] { "provider", "close", "dispose", "dispose" }, session.Calls);
    }

    [Fact]
    public void PublishedReadersRetainBackingAfterThreadJoinUntilTheyReturn()
    {
        var session = new Session { ReadersReturned = false };
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        var blocked = runtime.Stop(TimeSpan.Zero);
        Assert.True(blocked.ProcessingCompleted);
        Assert.False(blocked.SessionDisposeSucceeded);
        Assert.Equal(new[] { "provider", "close" }, session.Calls);
        session.ReadersReturned = true;
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);
        Assert.Equal(new[] { "provider", "close", "dispose" }, session.Calls);
    }

    [Fact]
    public async Task LastHolderCleanupRetainsReconciliationUntilTheNativeReleaseCanFinish()
    {
        using var finish = new ManualResetEventSlim();
        var session = new Session
        {
            DisposeFailure = new InvalidOperationException("view retained"),
            ProcessBody = () => finish.Wait(), StopBody = () => finish.Set()
        };
        using var broker = new KernelEtwSessionBroker(new Factory(session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        var held = broker.Acquire(new("fixture", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => held.Dispose());
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        session.DisposeFailure = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (broker.GetSnapshot().State != "Idle" && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Equal(1, session.Calls.Count(static call => call == "provider"));
        Assert.Equal(0, session.Calls.Count(static call => call == "unblock"));
        Assert.Equal(1, session.Calls.Count(static call => call == "close"));
    }

    [Fact]
    public void NativeRuntimeRejectsLegacyCallbackOnlyHoldersBeforeChangingTheProvider()
    {
        var session = new Session();
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        Assert.Throws<NotSupportedException>(() => runtime.UpdateSubscriptions(
            [new(1, "legacy", KernelTraceEventParser.Keywords.Process, static (_, _) => { })],
            new(1, DateTimeOffset.UtcNow)));
        Assert.Empty(session.Calls);
        runtime.Dispose();
    }

    [Fact]
    public void NativeRuntimeRejectsUnknownOutputBitsBeforeChangingTheProvider()
    {
        var session = new Session();
        var runtime = new NativeKernelEtwSessionRuntime("fixture", session);
        Assert.Throws<NotSupportedException>(() => runtime.UpdateSubscriptions(
            [new(1, "invalid-output", KernelTraceEventParser.Keywords.Process, static (_, _) => { })
                { NativeOutputs = KernelEtwNativeOutputs.Residency | (KernelEtwNativeOutputs)128 }],
            new(1, DateTimeOffset.UtcNow)));
        Assert.Empty(session.Calls);
        runtime.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(129)]
    public void InvalidNativeHoldIsRejectedBeforeRegistrationWithoutDisturbingAnExistingHolder(int outputs)
    {
        using var finish = new ManualResetEventSlim();
        var session = new Session { ProcessBody = () => finish.Wait(), StopBody = () => finish.Set() };
        var factory = new NativeValidatingFactory(session);
        using var owner = new KernelEtwSessionOwner(factory);
        using var existing = owner.Acquire(new("valid", KernelTraceEventParser.Keywords.Process, static (_, _) => { })
            { NativeOutputs = KernelEtwNativeOutputs.Residency });
        var before = existing.GetSnapshot();
        Assert.True(before.IsRunning);

        Assert.Throws<NotSupportedException>(() => owner.Acquire(
            new("invalid", KernelTraceEventParser.Keywords.ImageLoad, static (_, _) => { })
                { NativeOutputs = (KernelEtwNativeOutputs)outputs }));

        var after = existing.GetSnapshot();
        Assert.True(after.IsRunning);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.EnabledKeywords, after.EnabledKeywords);
        Assert.Equal(1, after.SubscriptionCount);
        Assert.Equal(1, factory.Creations);
        Assert.Empty(session.Calls);
    }

    private sealed class Session : IKernelEtwNativeRuntimeOperations, IKernelEtwNativeReadDrain
    {
        internal List<string> Calls { get; } = [];
        internal volatile Exception? StopFailure, UnblockFailure, DisposeFailure;
        internal Action ProcessBody = static () => { };
        internal Action StopBody = static () => { };
        internal Action CloseBody = static () => { };
        internal Action UnblockBody = static () => { };
        internal bool ReadersReturned = true;
        public int EventsLost => 0;
        public void Process() => ProcessBody();
        public void StopProvider() { Calls.Add("provider"); if (StopFailure is not null) throw StopFailure; StopBody(); }
        public void UnblockProcessing() { Calls.Add("unblock"); if (UnblockFailure is not null) throw UnblockFailure; UnblockBody(); }
        public void CloseTrace() { Calls.Add("close"); CloseBody(); }
        public void Dispose() { Calls.Add("dispose"); if (DisposeFailure is not null) throw DisposeFailure; }
        public void UpdateKeywords(KernelTraceEventParser.Keywords keywords) => Calls.Add("keywords");
        public bool WaitForReaders(TimeSpan timeout) => ReadersReturned;
    }

    private sealed class Factory(Session session) : IKernelEtwSessionRuntimeFactory
    {
        public bool TryGetAvailability(out string state, out string message) { state = "Available"; message = "fixture"; return true; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
            => new NativeKernelEtwSessionRuntime(name, session);
    }

    private sealed class NativeValidatingFactory(Session session) : IKernelEtwSessionRuntimeFactory
    {
        private readonly NativeKernelEtwSessionRuntimeFactory nativePolicy = new(new BoundedKernelEtwRegionProvider(1), static () => default);
        internal int Creations;
        public void ValidateRequest(KernelEtwSubscriptionRequest request) => nativePolicy.ValidateRequest(request);
        public bool TryGetAvailability(out string state, out string message)
        { state = "Available"; message = "fixture"; return true; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        { Creations++; return new NativeKernelEtwSessionRuntime(name, session); }
    }

    private sealed class UnavailableFactory : IKernelEtwSessionRuntimeFactory
    {
        internal int AvailabilityChecks;
        public bool TryGetAvailability(out string state, out string message)
        { AvailabilityChecks++; state = "Unavailable"; message = "fixture native host unavailable"; return false; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
            => throw new InvalidOperationException("Unavailable factory must not create a session.");
    }

    private sealed class Observer : IKernelEtwConsumerThreadObserver
    {
        internal int ManagedThreadId;
        internal bool Returned;
        public IDisposable RegisterCurrentThread(string name)
        {
            Assert.Equal("fixture", name);
            ManagedThreadId = Environment.CurrentManagedThreadId;
            return new Return(() => Returned = true);
        }
        public void ReportFailure(Exception error) => throw new InvalidOperationException("Unexpected consumer failure", error);
        private sealed class Return(Action action) : IDisposable { public void Dispose() => action(); }
    }
}
