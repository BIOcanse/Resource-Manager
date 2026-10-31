using System.Reflection;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class PortableSoftwareDiscoveryLossTests
{
    [Fact]
    public async Task MissingCurrentIdentityDoesNotInventAPathAndLaterEventFallbackStillWorks()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        using var factory = new DiscoveryRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromHours(1));
        var registry = new RecordingRegistry();
        var logger = new RecordingLogger();
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => [new(Environment.ProcessId)], candidate =>
            {
                if (candidate.ExecutablePath is null) { missing.TrySetResult(); return null; }
                return new RuntimeProcessIdentity(candidate.ProcessId, candidate.ParentProcessId,
                    candidate.ProcessName!, candidate.ExecutablePath, false);
            });
        await service.StartAsync(default);
        try
        {
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, registry.Observations);
            Assert.False(Worker(service).IsCompleted);
            Assert.Single(factory.Runtimes).EmitProcessStart(service);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await registry.Observed.Reader.ReadAsync(deadline.Token);
            Assert.Equal(1, registry.Observations);
            Assert.Single(logger.Warnings);
            Assert.Empty(logger.Errors);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task NativeBatchPreservesFallbackIdentityAndRundownAndRejectsNonStartRows()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        using var factory = new DiscoveryRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromHours(1));
        var registry = new RecordingRegistry();
        var candidateRead = new TaskCompletionSource<PortableProcessCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, new RecordingLogger(), () => [], candidate =>
            {
                candidateRead.TrySetResult(candidate);
                // The fallback candidate remains usable after the native batch lease has returned.
                Assert.Equal(@"D:\便携\fixture.exe", candidate.ExecutablePath);
                return new RuntimeProcessIdentity(candidate.ProcessId, candidate.ParentProcessId,
                    candidate.ProcessName!, candidate.ExecutablePath!, false);
            });
        await service.StartAsync(default);
        try
        {
            var runtime = Assert.Single(factory.Runtimes);
            runtime.EmitProcessStart(service, kind: 8, publish: false);
            Assert.False(candidateRead.Task.IsCompleted);
            runtime.EmitProcessStart(service, @"D:\便携\fixture.exe", flags: 1, publish: false);
            runtime.EmitProcessStart(service, @"D:\便携\fixture.exe");
            var candidate = await candidateRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Environment.ProcessId, candidate.ProcessId);
            Assert.Equal(2, candidate.ParentProcessId);
            Assert.Equal("fixture", candidate.ProcessName);
            Assert.Equal(133_000_000_000_000_000, candidate.StartedNoLaterThan);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await registry.Observed.Reader.ReadAsync(deadline.Token);
            await registry.Observed.Reader.ReadAsync(deadline.Token);
            Assert.Equal(2, registry.Observations);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task NativeProcessStartConsumerCountsSystemRejectionAndPreservesItsReason()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        using var factory = new DiscoveryRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromHours(1));
        var registry = new RecordingRegistry();
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, new RecordingLogger(), () => [], ReadFixtureIdentity);
        await service.StartAsync(default);
        try
        {
            var runtime = Assert.Single(factory.Runtimes);
            runtime.EmitProcessStart(service, processId: 4, publish: false);
            runtime.EmitProcessStart(service);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await registry.Observed.Reader.ReadAsync(deadline.Token);
            var consumed = runtime.Source.ConsumerCounter("process-start").Read();
            Assert.Equal(2, consumed.Received);
            Assert.Equal(1, consumed.Dropped);
            var rejected = (EtwEventDropCounter)typeof(KernelEtwEventSource)
                .GetMethod("Counter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(runtime.Source, ["process-start", "system-process"])!;
            Assert.Equal(1, rejected.Read().Received);
            Assert.Equal(1, rejected.Read().Dropped);
            Assert.Equal(1, registry.Observations);
            Assert.Equal(KernelTraceEventParser.Keywords.Process, broker.GetSnapshot().EnabledKeywords);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task NativePublicationGapRefreshesInventoryAndInactiveSourceCannotPublish()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        using var broker = new Broker(keepSiblingLease: true);
        var inventories = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CatalogProvider(catalog) { BeforeAcquire = release.Task, AcquisitionStarted = entered };
        using var service = new PortableSoftwareDiscoveryService(broker, provider, new MatchingCatalog(),
            new RecordingRegistry(), new RecordingLogger(),
            () => { Interlocked.Increment(ref inventories); return [new(Environment.ProcessId, 2)]; }, ReadFixtureIdentity);
        await service.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, inventories);
            Assert.Equal(KernelEtwNativeOutputs.Notifications, broker.Request!.NativeOutputs);
            Assert.Equal(KernelTraceEventParser.Keywords.Process, broker.Request.Keywords);
            await WaitForNativePublication(service, broker.PublishedSequence);
            Assert.Equal(1, inventories);
            // The running business worker is blocked in its existing catalog acquire, so a real
            // process start superseded by the next publication is a notification it never reads.
            broker.ProcessChanged(broker.Source, 77, processStart: true);
            broker.Publish();
            release.TrySetResult();
            await WaitForNativePublication(service, broker.PublishedSequence);
            Assert.Equal(2, inventories);
            broker.EventsLost = 1;
            await WaitForNativePublication(service, broker.PublishedSequence);
            Assert.Equal(3, inventories);
            // The same loss seen by the ordinary health check is the same refill,
            // rather than a second, independent inventory refresh.
            service.RefreshInventoryAfterLoss(broker.GetSnapshot());
            Assert.Equal(3, inventories);
            broker.Publish();
            await WaitForNativePublication(service, broker.PublishedSequence);
            Assert.Equal(3, inventories);
            await service.StopAsync(default);
            Assert.False(broker.Source.IsActive);
            broker.EventsLost = 2; // The explicit sibling still owns the actual resource.
            Assert.Equal(3, inventories);
        }
        finally { release.TrySetResult(); await service.StopAsync(default); }
        Assert.Equal(1, broker.SubscribeCount);
        Assert.Equal(1, broker.ReleaseCount);
    }

    [Fact]
    public async Task RunningDiscoveryReceivesProcessStartsAfterItsHeldRuntimeIsRebuilt()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        using var factory = new DiscoveryRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromHours(1));
        var registry = new RecordingRegistry();
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, new RecordingLogger(), () => [], ReadFixtureIdentity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(default);
        var discoveryWorker = Worker(service);
        try
        {
            var firstRuntime = Assert.Single(factory.Runtimes);
            firstRuntime.EmitProcessStart(service);
            await registry.Observed.Reader.ReadAsync(deadline.Token);

            firstRuntime.StopProcessingUnexpectedly();
            broker.Owner.Reconcile();
            Assert.True(firstRuntime.Disposed);
            Assert.False(discoveryWorker.IsCompleted);
            // Rebuilding the held resource retains discovery's active reader.
            // An independent reader cannot restore discovery or enumerate inventory.
            using var independentReader = broker.Acquire(new("independent-process-reader",
                KernelTraceEventParser.Keywords.Process, static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Notifications });
            var secondRuntime = factory.Runtimes[1];
            secondRuntime.EmitProcessStart(service);
            await registry.Observed.Reader.ReadAsync(deadline.Token);

            Assert.Equal(2, registry.Observations);
            Assert.Same(discoveryWorker, Worker(service));
            Assert.False(discoveryWorker.IsCompleted);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task SameDiscoveryServiceRestartsAndProcessesFreshInventoryAfterStop()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var registry = new RecordingRegistry();
        using var broker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, new RecordingLogger(),
            () => [new(Environment.ProcessId, 2)], ReadFixtureIdentity);
        for (var run = 1; run <= 2; run++)
        {
            await service.StartAsync(default);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (Volatile.Read(ref registry.Observations) < run)
                    await Task.Delay(1, deadline.Token);
                Assert.Equal(run, registry.Observations);
            }
            finally { await service.StopAsync(default); }
            Assert.True(Worker(service).IsCompletedSuccessfully);
            Assert.Equal(run, broker.ReleaseCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRegistryAdmissionIsCanceledByWorkerStopOrScanCancellation(bool explicitScan)
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var provider = new CatalogProvider(catalog);
        var registry = new RecordingRegistry { BlockAdmission = true };
        using var broker = new Broker();
        var logger = new RecordingLogger();
        using var service = new PortableSoftwareDiscoveryService(broker, provider,
            new MatchingCatalog(), registry, logger,
            () => [new(Environment.ProcessId, 2)], ReadFixtureIdentity);
        using var cancellation = new CancellationTokenSource();
        if (explicitScan)
        {
            var scan = service.ScanRunningProcessesAsync(cancellation.Token);
            try
            {
                await registry.AdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(scan.IsCompleted);
            }
            finally { cancellation.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            await service.StartAsync(default);
            try
            {
                await registry.AdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(Worker(service).IsCompleted);
            }
            finally { await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.True(Worker(service).IsCompletedSuccessfully);
            Assert.Equal(1, broker.ReleaseCount);
        }
        Assert.Equal(0, registry.Observations);
        Assert.Equal(0, provider.ActiveReaders);
        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task InvalidAndExitedCandidatesAreDroppedAndNewCandidateCanBeObserved()
    {
        var resolver = new CandidateResolver();
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var registry = new RecordingRegistry();
        var logger = new RecordingLogger();
        var exited = new PortableProcessCandidate(Environment.ProcessId, 2);
        PortableProcessCandidate candidate = exited;
        var reads = 0;
        using var candidateBroker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(candidateBroker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => [new(0), new(-1), new(4), candidate], item =>
            {
                reads++;
                if (ReferenceEquals(item, exited)) { item.HasExited = true; return null; }
                return ReadFixtureIdentity(item);
            });
        for (var i = 0; i < 20; i++) await service.ScanRunningProcessesAsync(default);
        Assert.Equal(1, reads); Assert.Empty(logger.Warnings); Assert.Equal(0, registry.Observations);
        candidate = new(Environment.ProcessId, 2);
        await service.ScanRunningProcessesAsync(default);
        Assert.Equal(2, reads); Assert.Equal(1, registry.Observations);
    }

    [Fact]
    public void NativeIdentityReaderTreatsDisappearedPidAsLifetimeAndAcceptsNewBirth()
    {
        var reader = new WindowsPortableProcessIdentityReader();
        var missing = new PortableProcessCandidate(int.MaxValue);
        for (var i = 0; i < 20; i++) Assert.Null(reader.TryRead(missing));
        Assert.True(missing.HasExited);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var birth = current.StartTime.ToFileTimeUtc();
        var old = new PortableProcessCandidate(current.Id, StartKey: birth - 1);
        Assert.Null(reader.TryRead(old)); Assert.True(old.HasExited);
        var delayedEvent = new PortableProcessCandidate(current.Id, StartedNoLaterThan: birth - 1);
        Assert.Null(reader.TryRead(delayedEvent)); Assert.True(delayedEvent.HasExited);
        Assert.Equal(birth, reader.TryRead(new(current.Id, StartKey: birth))!.StartKey);
    }

    [Fact]
    public async Task ConcurrentScanAndWorkerObserveEveryCandidateAndCancellationStopsWorker()
    {
        var resolver = new CandidateResolver();
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var registry = new RecordingRegistry();
        var logger = new RecordingLogger();
        using var candidateBroker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(candidateBroker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger,
            () => Enumerable.Range(0, 24).Select(_ => new PortableProcessCandidate(Environment.ProcessId, 2)).ToArray(), ReadFixtureIdentity);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cancellation.Token);
        await service.ScanRunningProcessesAsync(cancellation.Token);
        while (Volatile.Read(ref registry.Observations) < 48) await Task.Delay(10, cancellation.Token);
        cancellation.Cancel();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Worker(service).IsCompletedSuccessfully);
        Assert.Equal(48, resolver.Attempts);
        Assert.Equal(48, registry.Observations);
        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Theory]
    [InlineData(false, "native-StaleFrame")]
    [InlineData(true, "native-StaleFrame")]
    [InlineData(false, "ObjectDisposedException")]
    [InlineData(true, "OperationCanceledException")]
    public async Task FailedResolutionDoesNotStopLaterWorkerOrScanCandidates(bool scan, string failureKind)
    {
        var resolver = new CandidateResolver(failureKind);
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var logger = new RecordingLogger();
        var registry = new RecordingRegistry();
        var candidates = new[] { new PortableProcessCandidate(Environment.ProcessId, 1),
            new PortableProcessCandidate(Environment.ProcessId, 1), new PortableProcessCandidate(Environment.ProcessId, 2) };
        using var candidateBroker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(candidateBroker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => candidates, ReadFixtureIdentity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (scan) await service.ScanRunningProcessesAsync(timeout.Token);
        else
        {
            await service.StartAsync(timeout.Token);
            await resolver.Succeeded.Task.WaitAsync(timeout.Token);
            await service.StopAsync(timeout.Token);
            Assert.True(Worker(service).IsCompletedSuccessfully);
        }
        Assert.Equal(3, resolver.Attempts);
        Assert.Equal(1, registry.Observations);
        Assert.Single(logger.Warnings);
        Assert.Contains(failureKind, logger.Warnings.Single());
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task MissingResolutionSessionStopsWorkerWithVisibleError()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new MissingSessionResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var logger = new RecordingLogger();
        using var candidateBroker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(candidateBroker, new CatalogProvider(catalog),
            new MatchingCatalog(), null!, logger, () => [new(Environment.ProcessId)]);
        await service.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Worker(service).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(logger.Errors);
        Assert.Contains("worker stopped unexpectedly", logger.Errors.Single());
        Assert.Empty(logger.Warnings);
        var worker = service.ExecutionTask;
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(worker, service.ExecutionTask);
        Assert.True(worker!.IsFaulted);
    }

    [Fact]
    public async Task TerminalWorkerFailureIsReportedAndFaultsItsExecutionTask()
    {
        var error = new InvalidOperationException("terminal catalog fixture");
        var diagnostics = new RecordingBlockDiagnostics();
        var logger = new RecordingLogger();
        using var broker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(
            broker,
            new FailingCatalogProvider(error),
            new MatchingCatalog(),
            new RecordingRegistry(),
            logger,
            () => [new(Environment.ProcessId, 1)],
            ReadFixtureIdentity,
            diagnostics);

        await service.StartAsync(CancellationToken.None);
        var worker = service.ExecutionTask!;
        var reported = await diagnostics.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Worker(service).WaitAsync(TimeSpan.FromSeconds(5))));
        // The worker has returned: no process event consumer remains, even
        // though the hosted service has not yet received its StopAsync call.
        Assert.Equal(1, broker.ReleaseCount);
        Assert.Null(typeof(PortableSoftwareDiscoveryService)
            .GetField("nativeBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(worker, service.ExecutionTask);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => worker));

        Assert.Equal(BackendUids.PortableSoftwareDiscoveryService, reported.BlockId);
        Assert.Same(error, reported.Error);
        Assert.Equal("portable-discovery-loop", reported.Operation);
        Assert.True(Worker(service).IsFaulted);
        Assert.Single(logger.Errors);
        Assert.Contains("worker stopped unexpectedly", logger.Errors.Single());
        Assert.Equal(1, broker.ReleaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryWriteResumingOnAnotherThreadDoesNotEndTheMeasuredDiscovery(bool explicitScan)
    {
        // Installed 0.3.7-beta.20261028.6: the worker held a synchronous measurement scope across the registry
        // write; when the write resumed on another thread, the scope threw and discovery stopped while block 22
        // stayed running with a reported InvalidOperationException.
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new CandidateResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var diagnostics = new ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticsRegistry(
            [], NullLogger<ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticsRegistry>.Instance,
            [new ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticRegistration(
                BackendUids.PortableSoftwareDiscoveryService)]);
        var registry = new RecordingRegistry { ResumeOnAnotherThread = true };
        var logger = new RecordingLogger();
        var broker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(broker, new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => [new(Environment.ProcessId, 2)], ReadFixtureIdentity, diagnostics);
        if (explicitScan)
        {
            await service.ScanRunningProcessesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, registry.Observations);
        }
        else
        {
            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(await registry.Observed.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(100);
                Assert.False(Worker(service).IsCompleted);
            }
            finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.True(Worker(service).IsCompletedSuccessfully);
        }
        Assert.NotEqual(0, registry.ResumedOnThread);
        Assert.Null(diagnostics.Read(BackendUids.PortableSoftwareDiscoveryService).Result);
        Assert.Empty(logger.Errors);
    }

    private static Task Worker(PortableSoftwareDiscoveryService service) => (Task)typeof(PortableSoftwareDiscoveryService)
        .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static async Task WaitForNativePublication(PortableSoftwareDiscoveryService service, ulong sequence)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var binding = typeof(PortableSoftwareDiscoveryService)
                .GetField("nativeBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
            if (binding is not null && ((KernelEtwNativeNotifications.Cursor)binding.GetType()
                    .GetField("Cursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!).Publication >= sequence) return;
            await Task.Delay(1, timeout.Token);
        }
    }

    private static RuntimeProcessIdentity ReadFixtureIdentity(PortableProcessCandidate candidate)
        => new(candidate.ProcessId, candidate.ParentProcessId, "fixture", @"D:\Fixture\fixture.exe", false);

    private sealed class CandidateResolver(string failureKind = "native-StaleFrame") : IRuntimePackageIdentityResolver
    {
        internal int Attempts;
        internal TaskCompletionSource Succeeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
        {
            Interlocked.Increment(ref Attempts);
            if (process.ParentProcessId == 1)
            {
                if (failureKind == "ObjectDisposedException") throw new ObjectDisposedException("process");
                if (failureKind == "OperationCanceledException") throw new OperationCanceledException("provider canceled this process");
                throw new ResourceManager.App.Infrastructure.NativeCore.NativeSoftwareIdentityException("resolution",
                    ResourceManager.App.Infrastructure.NativeCore.NativeSoftwareIdentityStatus.StaleFrame);
            }
            Succeeded.TrySetResult();
            return RuntimeAttributionObservation.Matched(new("catalog:fixture", "Fixture", "other", "other",
                [Path.GetDirectoryName(Environment.ProcessPath!)!]));
        }
    }

    private sealed class MissingSessionResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => throw new ObjectDisposedException("NativeSoftwareIdentityResolutionSession");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceFailureGateAppliesOnlyToTheOriginalCatalogException(bool cleanupFails)
    {
        var original = new IOException("catalog acquisition fixture");
        var broker = new Broker { FailRelease = cleanupFails };
        using var service = new PortableSoftwareDiscoveryService(broker,
            new FailingCatalogProvider(original, resourceFailureHandled: true),
            new MatchingCatalog(), new RecordingRegistry(), new RecordingLogger(),
            () => [new(Environment.ProcessId, 1)], ReadFixtureIdentity);
        await service.StartAsync(default);
        var worker = service.ExecutionTask!;
        var actual = await Assert.ThrowsAsync<IOException>(() => worker.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(original, actual);
        Assert.Equal(!cleanupFails, service.CatalogFailureHandled);
        if (cleanupFails)
        {
            var cleanup = await Assert.ThrowsAsync<IOException>(() => service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotSame(original, cleanup);
            Assert.Equal("release fixture", cleanup.Message);
        }
        else await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(!cleanupFails, service.CatalogFailureHandled);
        Assert.Same(worker, service.ExecutionTask);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => worker));
        Assert.Equal(1, broker.ReleaseCount);
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FailingCatalogProvider(Exception error, bool resourceFailureHandled = false) : IRuntimeProcessAttributionCatalogProvider
    {
        public long SoftwareSnapshotGeneration => 0;
        public bool ReportFailure(Exception failure) => resourceFailureHandled && ReferenceEquals(error, failure);

        public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(
            string? windowsSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<RuntimeProcessAttributionCatalogLease>(error);
        }

        public Task InvalidateSoftwareSnapshotAsync() => Task.CompletedTask;
    }

    private sealed class RecordingBlockDiagnostics : IBusinessBlockDiagnostics
    {
        internal TaskCompletionSource<(int BlockId, Exception Error, string Operation)> FailureObserved { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReportFailure(int blockId, Exception error, string operation)
            => FailureObserved.TrySetResult((blockId, error, operation));

        public IDisposable MeasureSegment(int blockId) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class MatchingCatalog : ISoftwareIdentityCatalog
    {
        public string Version => "fixture";
        public IReadOnlyList<ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry> Entries => [];
        public ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry? MatchInstalledSoftware(
            ResourceManager.App.Domain.Software.InstalledSoftwareEntry software) => null;
        public ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process) => null;
        public ResourceManager.App.Domain.SoftwareIdentity.PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process)
            => new(new("fixture", "Fixture", "other", "fixture"),
                ResourceManager.App.Domain.SoftwareIdentity.PortableSoftwareIdentityConfidence.Confirmed, []);
    }

    private sealed class RecordingRegistry : ResourceManager.App.Application.SoftwareDiscovery.IPortableSoftwareRegistry
    {
        internal int Observations;
        internal bool BlockAdmission;
        internal bool ResumeOnAnotherThread;
        internal int ResumedOnThread;
        internal TaskCompletionSource AdmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Channel<bool> Observed { get; } = Channel.CreateUnbounded<bool>();
        public async Task ObserveAsync(ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareObservation observation, CancellationToken cancellationToken)
        {
            if (BlockAdmission)
            {
                AdmissionStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (ResumeOnAnotherThread)
            {
                // Completing from a new thread runs the awaiting callers inline there, as an I/O completion can.
                var resumed = new TaskCompletionSource();
                new Thread(() => { Thread.Sleep(100); resumed.SetResult(); }) { IsBackground = true }.Start();
                await resumed.Task;
                ResumedOnThread = Environment.CurrentManagedThreadId;
            }
            Interlocked.Increment(ref Observations);
            Observed.Writer.TryWrite(true);
        }
        public IReadOnlyList<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRegistration> GetSnapshot() => [];
        public Task<IReadOnlyList<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRegistration>> RefreshAsync(CancellationToken token)
            => Task.FromResult(GetSnapshot());
        public Task<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
            ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRootConfirmationRequest request, CancellationToken token)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<PortableSoftwareDiscoveryService>
    {
        internal System.Collections.Concurrent.ConcurrentBag<string> Warnings = [];
        internal System.Collections.Concurrent.ConcurrentBag<string> Errors = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, error));
            if (level == Microsoft.Extensions.Logging.LogLevel.Error) Errors.Add(formatter(state, error));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitScanReturnsItsCatalogLeaseOnInventoryFailureOrCancellation(bool canceled)
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new NullResolvers(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var provider = new CatalogProvider(catalog);
        using var cancellation = new CancellationTokenSource();
        using var service = new PortableSoftwareDiscoveryService(null!, provider, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () =>
            {
                Assert.Equal(1, provider.ActiveReaders);
                if (!canceled) throw new IOException("inventory failed after acquiring the catalog");
                cancellation.Cancel();
                return [];
            });
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanRunningProcessesAsync(cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => service.ScanRunningProcessesAsync(cancellation.Token));
        Assert.Equal(0, provider.ActiveReaders);
        Assert.Equal(1, provider.ReturnedLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleWorkerDetectsNewLossAndStopStillCancelsItWhenLeaseReleaseFails(bool workerReleases)
    {
        using var broker = new Broker();
        var scans = 0;
        using var service = new PortableSoftwareDiscoveryService(broker, null!, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () => { Interlocked.Increment(ref scans); return []; });
        using var runCancellation = new CancellationTokenSource();
        await service.StartAsync(runCancellation.Token);
        Assert.Equal(1, Volatile.Read(ref scans));
        broker.EventsLost = 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref scans) < 2) await Task.Delay(20, timeout.Token);
        Assert.Equal(2, Volatile.Read(ref scans));
        broker.FailRelease = true;
        if (workerReleases)
        {
            // Force the worker's own finally to win release ownership, rather
            // than relying on its race with StopAsync after cancellation.
            runCancellation.Cancel();
            await Assert.ThrowsAsync<IOException>(() => Worker(service).WaitAsync(timeout.Token));
        }

        await Assert.ThrowsAsync<IOException>(() => service.StopAsync(timeout.Token));

        Assert.False(service.CatalogFailureHandled);
        Assert.True(((Task)typeof(PortableSoftwareDiscoveryService)
            .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).IsCompleted);
        Assert.Equal(1, broker.ReleaseCount);
        Assert.Equal(1, broker.SubscribeCount);
        service.RefreshInventoryAfterLoss(Snapshot(1, 2));
        Assert.Equal(2, Volatile.Read(ref scans));
        await service.StopAsync(timeout.Token);
        service.Dispose();
        Assert.Equal(1, broker.ReleaseCount);
    }

    [Fact]
    public async Task InitialInventoryFailureReleasesItsLeaseWithoutStartingAWorker()
    {
        using var broker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(broker, null!, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () => throw new IOException("inventory fixture"));
        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(1, broker.ReleaseCount);
        Assert.Null(typeof(PortableSoftwareDiscoveryService)
            .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
    }

    [Fact]
    public void NewLossOrSessionRecoveryRequeuesCurrentInventoryOnce()
    {
        var inventory = new[] { new PortableProcessCandidate(42), new PortableProcessCandidate(73) };
        var scans = 0;
        using var service = Create(() => { scans++; return inventory; });
        var queue = Queue(service);
        service.RefreshInventoryAfterLoss(Snapshot(1, 8));
        Assert.Equal(new[] { 42, 73 }, Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(1, 8));
        Assert.Empty(Drain(queue));
        Assert.Equal(1, scans);

        inventory = [new(99)];
        service.RefreshInventoryAfterLoss(Snapshot(1, 9));
        Assert.Equal(new[] { 99 }, Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(1, 9));
        Assert.Empty(Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(2, 0));
        Assert.Equal(new[] { 99 }, Drain(queue));
        Assert.Equal(3, scans);
    }

    [Fact]
    public void SkippedPublicationWithoutNewLossRequeuesOnlyProcessesMissingFromTheLastInventory()
    {
        var inventory = new[] { new PortableProcessCandidate(42, StartKey: 1), new PortableProcessCandidate(73, StartKey: 2) };
        using var service = Create(() => inventory);
        var queue = Queue(service);
        service.RefreshInventoryAfterLoss(Snapshot(1, 8));
        Assert.Equal(new[] { 42, 73 }, Drain(queue));

        // 73 exited, 99 started and PID 42 was reused by a new process; the skipped publication hid both starts.
        inventory = [new(42, StartKey: 3), new(99, StartKey: 4)];
        service.RefreshInventoryAfterLoss(Snapshot(1, 8), force: true);
        Assert.Equal(new[] { 42, 99 }, Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(1, 8), force: true);
        Assert.Empty(Drain(queue));

        // A new loss still requeues the whole inventory.
        service.RefreshInventoryAfterLoss(Snapshot(1, 9), force: true);
        Assert.Equal(new[] { 42, 99 }, Drain(queue));
    }

    [Fact]
    public void UnavailableOrDisposedSourceDoesNotEnumerateProcesses()
    {
        var scans = 0;
        var service = Create(() => { scans++; return []; });
        service.RefreshInventoryAfterLoss(Snapshot(1, 0, "Unavailable"));
        Assert.Equal(0, scans);
        service.RefreshInventoryAfterLoss(Snapshot(2, 0));
        Assert.Equal(1, scans);
        service.Dispose();
        service.RefreshInventoryAfterLoss(Snapshot(3, 1));
        Assert.Equal(1, scans);
    }

    [Fact]
    public void RecoveryUsesTheExistingBoundedQueue()
    {
        using var service = Create(() => Enumerable.Range(1, 1200).Select(id => new PortableProcessCandidate(id)).ToArray());
        service.RefreshInventoryAfterLoss(Snapshot(1, 1));
        var candidates = Drain(Queue(service));
        Assert.Equal(1024, candidates.Length);
        Assert.Equal(177, candidates[0]);
        Assert.Equal(1200, candidates[^1]);
    }

    private static PortableSoftwareDiscoveryService Create(Func<PortableProcessCandidate[]> capture)
        => new(null!, null!, null!, null!, NullLogger<PortableSoftwareDiscoveryService>.Instance, capture);

    private static KernelEtwSessionSnapshot Snapshot(long generation, int loss, string state = "Running")
        => new(state, "fixture", generation, DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.Process, 1, loss);

    private static Channel<PortableProcessCandidate> Queue(PortableSoftwareDiscoveryService service)
        => (Channel<PortableProcessCandidate>)typeof(PortableSoftwareDiscoveryService)
            .GetField("processStarts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static int[] Drain(Channel<PortableProcessCandidate> queue)
    {
        var ids = new List<int>();
        while (queue.Reader.TryRead(out var candidate)) ids.Add(candidate.ProcessId);
        return ids.ToArray();
    }

    private sealed class DiscoveryRuntimeFactory : IKernelEtwSessionRuntimeFactory, IDisposable
    {
        private readonly NativeKernelEtwTestResource resource = new(
            NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000),
            new DateTimeOffset(DateTime.FromFileTimeUtc(133_000_000_000_000_000)));
        internal List<DiscoveryRuntime> Runtimes { get; } = [];
        public void ValidateRequest(KernelEtwSubscriptionRequest request) => resource.RuntimeFactory.ValidateRequest(request);
        public bool TryGetAvailability(out string state, out string message)
            => resource.RuntimeFactory.TryGetAvailability(out state, out message);
        public IKernelEtwSessionRuntime Create(string sessionName, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            var runtime = new DiscoveryRuntime(resource);
            runtime.Inner = resource.RuntimeFactory.Create(sessionName, keywords, context,
                subscriptions.Select(subscription => subscription with
                {
                    Bind = (source, bindingContext) =>
                    {
                        if (subscription.Id == "portable-software-discovery") runtime.Source = source;
                        subscription.Bind(source, bindingContext);
                    }
                }).ToArray());
            Runtimes.Add(runtime);
            return runtime;
        }
        public void Dispose() => resource.Dispose();
    }

    private sealed class DiscoveryRuntime(NativeKernelEtwTestResource resource) : IKernelEtwSessionRuntime
    {
        internal IKernelEtwSessionRuntime Inner { get; set; } = null!;
        internal KernelEtwEventSource Source { get; set; } = null!;
        internal bool Disposed { get; private set; }
        public bool IsProcessing => Inner.IsProcessing;
        public int EventsLost => Inner.EventsLost;
        public void StartProcessing(Action<Exception?> completed) => Inner.StartProcessing(completed);
        internal void StopProcessingUnexpectedly() => resource.Fail(6);
        public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
            KernelEtwSessionContext context) => Inner.UpdateSubscriptions(subscriptions, context);
        public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout) => Inner.Stop(waitTimeout);
        public void Dispose() { Inner.Dispose(); Disposed = true; }

        internal void EmitProcessStart(PortableSoftwareDiscoveryService service,
            string path = @"D:\Fixture\fixture.exe", uint flags = 0, uint kind = 7, bool publish = true,
            uint? processId = null)
        {
            var opcode = kind == 8 ? (byte)2 : (flags & 1) != 0 ? (byte)3 : (byte)1;
            resource.RawProcess(0, processId ?? checked((uint)Environment.ProcessId), 2, path, opcode: opcode);
            if (publish) resource.Publish(0);
            // Consumption belongs to the running service's existing native publication worker.
            GC.KeepAlive(service);
        }
    }

    private sealed class Broker : IKernelEtwSessionBroker, IDisposable
    {
        private readonly NativeRendererTestBroker native;
        internal Broker(bool keepSiblingLease = false) => native = new(keepSiblingLease: keepSiblingLease);
        internal KernelEtwEventSource Source => native.Source;
        internal KernelEtwSubscriptionRequest? Request => native.Request;
        internal int EventsLost
        {
            get => native.GetSnapshot().EventsLost;
            set => native.SetEventsLost(value);
        }
        internal ulong PublishedSequence => native.PublishedSequence;
        internal int ReleaseCount, SubscribeCount;
        internal bool FailRelease;
        public KernelEtwSessionSnapshot GetSnapshot() => native.GetSnapshot();
        internal void Publish() => native.Publish();
        internal void ProcessChanged(KernelEtwEventSource source, int pid, bool processStart = false)
            => native.ProcessChanged(source, pid, processStart);
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            var lease = native.Acquire(request);
            SubscribeCount++;
            return new Lease(this, lease);
        }
        public void Dispose() => native.Dispose();
        private sealed class Lease(Broker broker, IKernelEtwSubscription lease) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                broker.ReleaseCount++;
                lease.Dispose();
                if (broker.FailRelease) throw new IOException("release fixture");
            }
        }
    }
    private sealed class CatalogProvider(RuntimeProcessAttributionCatalog catalog) : IRuntimeProcessAttributionCatalogProvider
    {
        internal int ActiveReaders;
        internal int ReturnedLeases;
        internal Task? BeforeAcquire;
        internal TaskCompletionSource? AcquisitionStarted;
        public long SoftwareSnapshotGeneration => 0;
        public async Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquisitionStarted?.TrySetResult();
            if (BeforeAcquire is { } wait) await wait.WaitAsync(cancellationToken);
            Interlocked.Increment(ref ActiveReaders);
            return new RuntimeProcessAttributionCatalogLease(catalog, () =>
            {
                Interlocked.Decrement(ref ActiveReaders);
                Interlocked.Increment(ref ReturnedLeases);
            });
        }
        public Task InvalidateSoftwareSnapshotAsync() => Task.CompletedTask;
    }

    private sealed class NullResolvers : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process) => RuntimeAttributionObservation.NoMatch;
    }
}
