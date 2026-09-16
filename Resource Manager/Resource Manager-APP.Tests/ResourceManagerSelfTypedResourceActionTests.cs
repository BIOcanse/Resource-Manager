using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Reflection;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
/// <summary>
/// 没有扫描结果的磁盘占用存储。
/// 这些用例只关心工作集那两条资源；没扫过时磁盘占用不该出现在账本里，
/// 正好也顺带守住了这一点。
/// </summary>
internal sealed class EmptyDiskUsageTreeStore : IDiskUsageTreeStore
{
    public DiskUsageScanResult? Current => null;

    public void Replace(DiskUsageScanResult result) => throw new NotSupportedException();

    public void Clear()
    {
    }
}

public sealed class ResourceManagerSelfTypedResourceActionTests
{
    [Fact]
    public void LocalSelfManagerResynchronizesAgainstNativeDefinitionAuthority()
    {
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(),
            new EmptyDiskUsageTreeStore());
        var sample = InvokePrivate(manager, "CaptureProcessFamilySample");
        var rows = sample.GetType().GetProperty("Backend")!.GetValue(sample)!;
        var resourceId = (LocalResourceId)typeof(ResourceManagerSelfLocalResourceManager)
            .GetField("BackendWorkingSetId", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var synchronize = typeof(ResourceManagerSelfLocalResourceManager).GetMethod(
            "SynchronizeResource",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var arguments = new[] { (object)resourceId, rows, 3u };

        synchronize.Invoke(manager, arguments);
        var nativeManager = GetPrivateField<DefaultLocalResourceManager>(manager, "manager");
        var resources = GetPrivateField<Dictionary<LocalResourceId, LocalResourceHandle>>(
            manager,
            "resources");
        var handle = resources[resourceId];
        var expected = nativeManager.Session.ReadResource(handle);
        Assert.Equal(0UL, expected.SettledActivity);
        Assert.Equal(
            LocalResourceAccessLossImpact.UnobservableNow,
            expected.AccessLossImpact);
        var divergentSize = expected.SizeBytes == 0 ? 1UL : expected.SizeBytes - 1;
        nativeManager.UpdateResource(handle, new(
            resourceId,
            divergentSize,
            expected.RecoveryCostCoefficient,
            expected.Recoverability,
            expected.AccessLossImpact));
        Assert.Equal(divergentSize, nativeManager.Session.ReadResource(handle).SizeBytes);

        synchronize.Invoke(manager, arguments);

        Assert.Equal(expected.SizeBytes, nativeManager.Session.ReadResource(handle).SizeBytes);
    }

    [Fact]
    public async Task LocalSelfManagerNormalTickRegistersOnlyOperableWorkingSets()
    {
        var writer = new RecordingPolicyWriter();
        using var manager = new ResourceManagerSelfLocalResourceManager(
            writer,
            new EmptyDiskUsageTreeStore());

        var result = await manager.TickAsync();

        Assert.False(manager.IsBackgroundWorkerRunning);
        Assert.InRange(result.RegisteredResourceCount, 1, 2);
        Assert.Equal(result.RegisteredResourceCount, result.Capacity.OccupiedCount);
        Assert.Equal(8, result.Capacity.Capacity);
        Assert.Equal(0, result.Manager.CapacityActionCount);
        Assert.Equal(0, result.Manager.ModeActionCount);
        Assert.Equal(0, result.PendingUncertainExecutionCount);
        Assert.Empty(manager.GetPendingUncertainExecutions());
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task LocalSelfManagerOptimizePersistsUntilNormalReplacesIt()
    {
        var writer = new RecordingPolicyWriter();
        using var manager = new ResourceManagerSelfLocalResourceManager(
            writer,
            new EmptyDiskUsageTreeStore());

        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.Optimize);
        var first = await manager.TickAsync();
        var second = await manager.TickAsync();
        var optimizeRequestCount = writer.Requests.Count;
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.Normal);
        var normal = await manager.TickAsync();

        Assert.Equal(1, first.Manager.ModeActionCount);
        Assert.Equal(1, second.Manager.ModeActionCount);
        Assert.True(optimizeRequestCount >= 2);
        Assert.Equal(0, normal.Manager.ModeActionCount);
        Assert.Equal(optimizeRequestCount, writer.Requests.Count);
        Assert.All(writer.Requests, static request =>
            Assert.True(request.TrimWorkingSet));
    }

    [Fact]
    public async Task LocalSelfManagerPreservesAndReconcilesUnknownTrimEffect()
    {
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(malformedResults: true),
            new EmptyDiskUsageTreeStore());

        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var result = await manager.TickAsync();
        var execution = Assert.Single(result.Manager.UncertainExecutions);

        Assert.False(execution.IsResolved);
        Assert.Equal(1, result.PendingUncertainExecutionCount);
        Assert.Same(execution, Assert.Single(manager.GetPendingUncertainExecutions()));
        var reconciled = manager.Reconcile(
            execution,
            LocalResourceEffect.NoEffect);
        Assert.False(reconciled.EffectUncertain);
        Assert.True(execution.IsResolved);
        Assert.Empty(manager.GetPendingUncertainExecutions());

        manager.SetDesiredMode(LocalResourceCleanupMode.Normal);
        var next = await manager.TickAsync();
        Assert.Equal(0, next.Manager.ModeActionCount);
    }

    [Fact]
    public async Task LocalSelfManagerRetainsHandleUntilRecoveryCompletesAndUnregisterCanBeRetried()
    {
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(malformedResults: true),
            new EmptyDiskUsageTreeStore());
        var sample = InvokePrivate(manager, "CaptureProcessFamilySample");

        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var tick = await manager.TickAsync();
        var execution = Assert.Single(tick.Manager.UncertainExecutions);
        var resourceId = execution.Context.ResourceUid;
        var resources = GetPrivateField<Dictionary<LocalResourceId, LocalResourceHandle>>(
            manager,
            "resources");
        var retainedHandle = resources[resourceId];
        var nativeManager = GetPrivateField<DefaultLocalResourceManager>(manager, "manager");
        var memoryTable = GetPrivateField<LocalResourceTableHandle>(manager, "memoryTable");
        var capacityBefore = nativeManager.Session.ReadCapacity(memoryTable);
        var definition = nativeManager.Session.ReadResource(retainedHandle);
        var backendId = (LocalResourceId)typeof(ResourceManagerSelfLocalResourceManager)
            .GetField("BackendWorkingSetId", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var rows = sample.GetType()
            .GetProperty(resourceId == backendId ? "Backend" : "NativeUi")!
            .GetValue(sample)!;
        var factType = typeof(ResourceManagerSelfLocalResourceManager)
            .GetNestedType("SelfProcessFact", BindingFlags.NonPublic)!;
        var emptyRows = Array.CreateInstance(factType, 0);
        var synchronize = typeof(ResourceManagerSelfLocalResourceManager).GetMethod(
            "SynchronizeResource",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] MissingArguments() =>
            [resourceId, emptyRows, definition.RecoveryCostCoefficient];

        var invocation = Assert.Throws<TargetInvocationException>(() =>
            synchronize.Invoke(manager, MissingArguments()));
        var recoveryRequired = Assert.IsType<NativeLocalResourceManagerException>(
            invocation.InnerException);
        Assert.Equal(LocalResourceManagerError.RecoveryRequired, recoveryRequired.Error);
        try
        {
            Assert.Equal(retainedHandle, resources[resourceId]);
            Assert.Equal(capacityBefore, nativeManager.Session.ReadCapacity(memoryTable));

            manager.Reconcile(execution, LocalResourceEffect.NoEffect);
            synchronize.Invoke(manager, MissingArguments());
            Assert.False(resources.ContainsKey(resourceId));
            var capacityAfterUnregister = nativeManager.Session.ReadCapacity(memoryTable);
            Assert.Equal(capacityBefore.OccupiedCount - 1, capacityAfterUnregister.OccupiedCount);

            synchronize.Invoke(
                manager,
                [resourceId, rows, definition.RecoveryCostCoefficient]);
            Assert.True(resources.ContainsKey(resourceId));
            Assert.Equal(
                capacityBefore.OccupiedCount,
                nativeManager.Session.ReadCapacity(memoryTable).OccupiedCount);
        }
        finally
        {
            if (!execution.IsResolved)
            {
                manager.Reconcile(execution, LocalResourceEffect.NoEffect);
            }
        }
    }

    [Fact]
    public async Task LocalSelfManagerRetainsAuthorityBeforePropagatingTypedTickFailure()
    {
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(throwOnApply: true),
            new EmptyDiskUsageTreeStore());
        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);

        var exception = await Assert.ThrowsAsync<LocalResourceManagerTickFailedException>(
            () => manager.TickAsync());

        Assert.Equal(1, exception.PartialResult.ModeActionCount);
        var uncertain = Assert.Single(exception.PartialResult.UncertainExecutions);
        Assert.Same(uncertain, Assert.Single(manager.GetPendingUncertainExecutions()));
        Assert.Equal(LocalResourceManagerCloseResult.RecoveryRequired, manager.TryClose());

        manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.True(uncertain.IsResolved);
        Assert.Empty(manager.GetPendingUncertainExecutions());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
    }

    [Fact]
    public async Task LocalSelfManagerCloseRetainsUnknownAuthorityUntilReconciled()
    {
        using var manager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(malformedResults: true),
            new EmptyDiskUsageTreeStore());

        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var tick = await manager.TickAsync();
        var uncertain = Assert.Single(tick.Manager.UncertainExecutions);

        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            manager.TryClose());
        var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
            manager.Dispose);
        Assert.Equal(1, closeException.PendingExecutionCount);
        Assert.Same(uncertain, Assert.Single(manager.GetPendingUncertainExecutions()));
        Assert.False(uncertain.IsResolved);

        manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.True(uncertain.IsResolved);
        Assert.Empty(manager.GetPendingUncertainExecutions());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Throws<ObjectDisposedException>(
            manager.GetPendingUncertainExecutions);
    }

    [Fact]
    public async Task LocalSelfModePublishesDuringHandlerAndDisposeRetriesAfterward()
    {
        using var handlerEntered = new ManualResetEventSlim();
        using var allowHandlerToFinish = new ManualResetEventSlim();
        var writer = new RecordingPolicyWriter(
            applyEntered: handlerEntered,
            applyContinue: allowHandlerToFinish);
        var manager = new ResourceManagerSelfLocalResourceManager(
            writer,
            new EmptyDiskUsageTreeStore());
        using var cancellation = new CancellationTokenSource();

        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var tick = Task.Run(() => manager.TickAsync(cancellation.Token));
        try
        {
            Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)));
            manager.SetDesiredMode(LocalResourceCleanupMode.Normal);
            var active = Assert.Throws<InvalidOperationException>(manager.Dispose);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                active.Message);

            allowHandlerToFinish.Set();
            var first = await tick.WaitAsync(TimeSpan.FromSeconds(10));
            var next = await manager.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, first.Manager.ModeActionCount);
            Assert.Equal(0, next.Manager.ModeActionCount);
            manager.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => manager.TickAsync());
            Assert.Throws<ObjectDisposedException>(
                manager.GetPendingUncertainExecutions);
        }
        finally
        {
            allowHandlerToFinish.Set();
            if (!tick.IsCompleted)
            {
                cancellation.Cancel();
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(manager.Dispose);
            }
        }
    }

    [Fact]
    public async Task SuppressedFlowSelfCapabilityCannotReenterOwnerLifecycle()
    {
        ResourceManagerSelfLocalResourceManager? manager = null;
        (Exception? Tick, Exception? Query, Exception? Dispose) errors = default;
        var writer = new RecordingPolicyWriter(onApply: () =>
        {
            Task<(Exception Tick, Exception Query, Exception Dispose)> probe;
            using (ExecutionContext.SuppressFlow())
            {
                probe = Task.Run(() =>
                    (Record.Exception(() =>
                        manager!.TickAsync().GetAwaiter().GetResult()),
                     Record.Exception(manager!.GetPendingUncertainExecutions),
                     Record.Exception(manager!.Dispose)));
            }
            errors = probe.WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
        });
        using (manager = new ResourceManagerSelfLocalResourceManager(writer, new EmptyDiskUsageTreeStore()))
        {
            manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);

            var result = await manager.TickAsync();

            Assert.Equal(1, result.Manager.ModeActionCount);
            Assert.IsType<InvalidOperationException>(errors.Tick);
            Assert.IsType<InvalidOperationException>(errors.Query);
            Assert.IsType<InvalidOperationException>(errors.Dispose);
            Assert.Empty(manager.GetPendingUncertainExecutions());
        }
    }

    [Fact]
    public async Task LocalSelfManagerRetainsUncertainEffectBeforePropagatingCancellation()
    {
        using var handlerEntered = new ManualResetEventSlim();
        using var allowHandlerToFinish = new ManualResetEventSlim();
        var writer = new RecordingPolicyWriter(
            malformedResults: true,
            applyEntered: handlerEntered,
            applyContinue: allowHandlerToFinish);
        var manager = new ResourceManagerSelfLocalResourceManager(
            writer,
            new EmptyDiskUsageTreeStore());
        using var cancellation = new CancellationTokenSource();

        manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var tick = Task.Run(() => manager.TickAsync(cancellation.Token));
        try
        {
            Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            allowHandlerToFinish.Set();

            var tickFailure = await Record.ExceptionAsync(
                () => tick.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsAssignableFrom<OperationCanceledException>(tickFailure);
            var uncertain = Assert.Single(manager.GetPendingUncertainExecutions());
            Assert.False(uncertain.IsResolved);
            Assert.Equal(
                LocalResourceManagerCloseResult.RecoveryRequired,
                manager.TryClose());
            var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
                manager.Dispose);
            Assert.Equal(1, closeException.PendingExecutionCount);
            Assert.Same(uncertain, Assert.Single(manager.GetPendingUncertainExecutions()));
            manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
            Assert.True(uncertain.IsResolved);
            Assert.Empty(manager.GetPendingUncertainExecutions());
            Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        }
        finally
        {
            cancellation.Cancel();
            allowHandlerToFinish.Set();
            if (!tick.IsCompleted)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(() => ReconcileAll(manager));
                _ = Record.Exception(manager.Dispose);
            }
        }
    }

    [Fact]
    public async Task LocalSelfManagerCapabilityCannotReenterOwnerLifecycle()
    {
        ResourceManagerSelfLocalResourceManager? manager = null;
        Exception? tickError = null;
        Exception? modeError = null;
        Exception? queryError = null;
        Exception? disposeError = null;
        var writer = new RecordingPolicyWriter(onApply: () =>
        {
            tickError = Record.Exception(() => manager!.TickAsync().GetAwaiter().GetResult());
            modeError = Record.Exception(() =>
                manager!.SetDesiredMode(LocalResourceCleanupMode.Normal));
            queryError = Record.Exception(() =>
                manager!.GetPendingUncertainExecutions());
            disposeError = Record.Exception(manager!.Dispose);
        });
        using (manager = new ResourceManagerSelfLocalResourceManager(writer, new EmptyDiskUsageTreeStore()))
        {
            manager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
            var result = await manager.TickAsync();

            Assert.Equal(1, result.Manager.ModeActionCount);
            Assert.IsType<InvalidOperationException>(tickError);
            Assert.IsType<InvalidOperationException>(modeError);
            Assert.IsType<InvalidOperationException>(queryError);
            Assert.IsType<InvalidOperationException>(disposeError);
            Assert.Empty(manager.GetPendingUncertainExecutions());
            var next = await manager.TickAsync();
            Assert.Equal(1, next.Manager.ModeActionCount);
        }
    }

    [Fact]
    public async Task LocalSelfCapabilityCannotEnterAnotherSelfManagerLifecycle()
    {
        ResourceManagerSelfLocalResourceManager? secondManager = null;
        Exception? nestedTickError = null;
        Exception? taskRunTickError = null;
        Exception? queryError = null;
        Exception? disposeError = null;
        var firstWriter = new RecordingPolicyWriter(onApply: () =>
        {
            nestedTickError = Record.Exception(
                () => secondManager!.TickAsync().GetAwaiter().GetResult());
            taskRunTickError = Record.Exception(
                () => Task.Run(() => secondManager!.TickAsync()).GetAwaiter().GetResult());
            queryError = Record.Exception(secondManager!.GetPendingUncertainExecutions);
            disposeError = Record.Exception(secondManager.Dispose);
        });
        using var firstManager = new ResourceManagerSelfLocalResourceManager(
            firstWriter,
            new EmptyDiskUsageTreeStore());
        using (secondManager = new ResourceManagerSelfLocalResourceManager(
            new RecordingPolicyWriter(),
            new EmptyDiskUsageTreeStore()))
        {
            firstManager.SetDesiredMode(LocalResourceCleanupMode.Optimize);

            var result = await firstManager.TickAsync();

            Assert.Equal(1, result.Manager.ModeActionCount);
            Assert.IsType<InvalidOperationException>(nestedTickError);
            Assert.IsType<InvalidOperationException>(taskRunTickError);
            Assert.IsType<InvalidOperationException>(queryError);
            Assert.IsType<InvalidOperationException>(disposeError);
            Assert.Empty(secondManager.GetPendingUncertainExecutions());

            var independent = await secondManager.TickAsync();
            Assert.Equal(0, independent.Manager.ModeActionCount);
        }
    }

    [Fact]
    public async Task ActiveSelfCapabilityRejectsOtherSdkLifecycleBeforeNativeBegin()
    {
        using var selfHandlerEntered = new ManualResetEventSlim();
        using var allowSelfHandler = new ManualResetEventSlim();
        var selfWriter = new RecordingPolicyWriter(
            applyEntered: selfHandlerEntered,
            applyContinue: allowSelfHandler);
        using var selfManager = new ResourceManagerSelfLocalResourceManager(
            selfWriter,
            new EmptyDiskUsageTreeStore());
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 1,
            resourceCapacity: 1);
        using var sdkSession = new NativeLocalResourceManagerSession(configuration);
        var sdkTable = sdkSession.RegisterTable(new(
            new(901),
            1,
            LocalResourceDomain.Memory,
            1));
        var sdkHandlerCalls = 0;
        ResourceManagerSelfLocalResourceManager? unexpectedManager = null;
        sdkSession.Capabilities.Register(
            sdkTable,
            new(901, 9010, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref sdkHandlerCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        sdkSession.RegisterResource(
            sdkTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var sdkManager = new DefaultLocalResourceManager(sdkSession);
        sdkManager.SetDesiredMode(new(
            sdkTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        selfManager.SetDesiredMode(LocalResourceCleanupMode.Optimize);
        var selfTick = Task.Run(() => selfManager.TickAsync());
        Task<LocalResourceManagerTickResult>? sdkTick = null;
        try
        {
            Assert.True(selfHandlerEntered.Wait(TimeSpan.FromSeconds(10)));
            var constructorError = Record.Exception(() =>
            {
                unexpectedManager = new ResourceManagerSelfLocalResourceManager(
                    new RecordingPolicyWriter(),
                    new EmptyDiskUsageTreeStore());
            });
            sdkTick = Task.Run(() => sdkManager.TickAsync());
            var active = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sdkTick.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                active.Message);
            Assert.IsType<InvalidOperationException>(constructorError);
            Assert.Null(unexpectedManager);
            Assert.Equal(0, sdkHandlerCalls);
            Assert.False(selfTick.IsCompleted);
        }
        finally
        {
            allowSelfHandler.Set();
            _ = await Record.ExceptionAsync(
                () => selfTick.WaitAsync(TimeSpan.FromSeconds(10)));
            if (sdkTick is not null)
            {
                _ = await Record.ExceptionAsync(
                    () => sdkTick.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            unexpectedManager?.Dispose();
        }

        _ = await selfTick.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = await sdkManager.TickAsync();
        Assert.Equal(1, retry.ModeActionCount);
        Assert.Equal(1, sdkHandlerCalls);
    }

    [Fact]
    public void PartialTypedTrimIsUncertainAndOnlyExactNoEffectIsFailed()
    {
        var requests = new[]
        {
            new ProcessResourcePolicyBatchRequest(
                101,
                DateTimeOffset.UtcNow,
                TrimWorkingSet: true),
            new ProcessResourcePolicyBatchRequest(
                102,
                DateTimeOffset.UtcNow,
                TrimWorkingSet: true)
        };
        ProcessResourcePolicyBatchWriteResult Result(
            int processId,
            bool succeeded)
            => new(
                processId,
                [
                    new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.TrimWorkingSet,
                        succeeded,
                        succeeded ? "ok" : "unchanged")
                ]);

        Assert.Equal(
            AdapterResourceActionStatus.ResourceBusy,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(
                requests,
                [Result(101, true), Result(102, false)]));
        Assert.Equal(
            AdapterResourceActionStatus.Failed,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(
                requests,
                [Result(101, false), Result(102, false)]));
        Assert.Equal(
            AdapterResourceActionStatus.Completed,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(
                requests,
                [Result(101, true), Result(102, true)]));
        Assert.Equal(
            AdapterResourceActionStatus.ResourceBusy,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(
                requests,
                [Result(101, true), Result(101, true)]));
    }

    private static void ReconcileAll(ResourceManagerSelfLocalResourceManager manager)
    {
        foreach (var execution in manager.GetPendingUncertainExecutions())
        {
            if (!execution.IsResolved)
            {
                manager.Reconcile(execution, LocalResourceEffect.NoEffect);
            }
        }
    }

    private static object InvokePrivate(object target, string methodName)
        => target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, null)!;

    private static T GetPrivateField<T>(object target, string fieldName)
        => (T)target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    // 账本相关的其他用例也要用它，所以放开到程序集内可见。
    internal sealed class RecordingPolicyWriter(
        bool succeed = true,
        bool throwOnApply = false,
        bool malformedResults = false,
        ManualResetEventSlim? applyEntered = null,
        ManualResetEventSlim? applyContinue = null,
        Action? onApply = null)
        : IProcessResourcePolicyWriter
    {
        public List<ProcessResourcePolicyBatchRequest> Requests { get; } = [];

        public DateTimeOffset? TryReadProcessStartedAt(int processId)
            => processId > 0
                ? DateTimeOffset.UnixEpoch.AddSeconds(processId)
                : null;

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            if (throwOnApply)
            {
                throw new IOException("injected typed resource effect failure");
            }
            Requests.AddRange(requests);
            onApply?.Invoke();
            applyEntered?.Set();
            if (applyContinue is not null
                && !applyContinue.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("injected typed resource effect did not resume");
            }
            if (malformedResults)
            {
                return [];
            }
            return requests.Select(request =>
                new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.TrimWorkingSet,
                            succeed,
                            succeed ? "ok" : "unchanged")
                    ])).ToArray();
        }

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot>
            ReadProcessInstanceForRecovery(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot>
            ReadPlacementStateForRecovery(
                int processId,
                ProcessPlacementReadFields requiredFields)
            => throw Unexpected();

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot>
            ReadThreadPlacementStateForRecovery(
                int processId,
                int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(
            int processId,
            string priorityClass)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
            int processId,
            long affinityMask)
            => throw Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
            int processId,
            int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority)
            => throw Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask)
            => throw Unexpected();

        private static InvalidOperationException Unexpected()
            => new("The typed self action used a non-batch writer path.");
    }
}
