using ResourceManager.Adapter.LocalResources;

namespace ResourceManager.Adapter.SharedMemory.Tests;

public sealed class LocalResourceManagerTests
{
    [Fact]
    public void NativeContractMatchesManagedLayoutAndStartsNoWorker()
    {
        using var session = CreateSession(4);
        Assert.NotEqual(0UL, NativeLocalResourceManagerSession.PublishedLayoutFingerprint);
        Assert.False(session.StartsBackgroundWorker);
        using (var pump = new LocalResourceManagerPump(session))
        {
            Assert.False(pump.IsBackgroundWorkerRunning);
        }
        using (var manager = new DefaultLocalResourceManager(session))
        {
            Assert.False(manager.IsBackgroundWorkerRunning);
        }
        Assert.Equal(10U, session.Configuration.EffectiveCapacityPolicy.ConcentratedTriggerFreePercent);
        Assert.Equal(20U, session.Configuration.EffectiveCapacityPolicy.ConcentratedTargetFreePercent);
        Assert.Equal(15U, session.Configuration.EffectiveCapacityPolicy.SmoothTriggerFreePercent);
        Assert.Equal(5U, session.Configuration.EffectiveCapacityPolicy.SmoothEmergencyFreePercent);
        Assert.Equal(session.Configuration.ResourceCapacity, session.Configuration.EffectivePartitionCapacity);
    }

    [Fact]
    public void CloseContractHasStablePublicValuesAndValidatedPendingCount()
    {
        Assert.Equal(1, (byte)LocalResourceManagerCloseResult.Closed);
        Assert.Equal(2, (byte)LocalResourceManagerCloseResult.RecoveryRequired);
        var exception = new LocalResourceRecoveryRequiredException(3);
        Assert.Equal(3, exception.PendingExecutionCount);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalResourceRecoveryRequiredException(0));
    }

    [Fact]
    public void DefaultManagerRejectsUnknownCapacityStrategy()
    {
        using var session = CreateSession(4);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DefaultLocalResourceManager(
                session,
                (LocalResourceCapacityStrategy)byte.MaxValue));

        Assert.Equal("capacityStrategy", exception.ParamName);
    }

    [Fact]
    public void FactoryPreservesConstructionFailureAfterPrivateSessionCleanup()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DefaultLocalResourceManager.Create(
                LocalResourceManagerConfiguration.CreateDefault(1, 4),
                (LocalResourceCapacityStrategy)byte.MaxValue));

        Assert.Equal("capacityStrategy", exception.ParamName);
    }

    [Theory]
    [InlineData(10U, 10U, 15U, 20U)]
    [InlineData(11U, 10U, 15U, 20U)]
    [InlineData(5U, 10U, 10U, 20U)]
    [InlineData(5U, 11U, 10U, 20U)]
    [InlineData(5U, 10U, 20U, 20U)]
    [InlineData(5U, 10U, 21U, 20U)]
    public void CapacityPolicyRejectsEqualOrInvertedAdjacentThresholds(
        uint smoothEmergency,
        uint concentratedTrigger,
        uint smoothTrigger,
        uint concentratedTarget)
    {
        var policy = new LocalResourceCapacityPolicy(
            concentratedTrigger,
            concentratedTarget,
            smoothTrigger,
            smoothEmergency,
            SmoothMaximumReleasesPerInterval: 1);
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 1,
            resourceCapacity: 4,
            capacityPolicy: policy);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NativeLocalResourceManagerSession(configuration));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public async Task ConfiguredFactoryCleanupIgnoresAnotherSessionHandler()
    {
        using var activeSession = CreateSession(1);
        var activeTable = activeSession.RegisterTable(
            new(new(105), 1, LocalResourceDomain.Memory, 1));
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        activeSession.Capabilities.Register(
            activeTable,
            new(105, 1050, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        activeSession.RegisterResource(
            activeTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var activeManager = new DefaultLocalResourceManager(activeSession);
        activeManager.SetDesiredMode(new(
            activeTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        var initializationFailure = new InvalidOperationException(
            "configured initialization failed");
        NativeLocalResourceManagerSession? failedSession = null;
        Task<LocalResourceManagerTickResult>? activeTick = null;

        InvalidOperationException observed;
        try
        {
            observed = Assert.Throws<InvalidOperationException>(() =>
                DefaultLocalResourceManager.CreateConfigured(
                    LocalResourceManagerConfiguration.CreateDefault(1, 1),
                    LocalResourceCapacityStrategy.Concentrated,
                    candidate =>
                    {
                        failedSession = candidate.Session;
                        activeTick = activeManager.TickAsync();
                        handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))
                            .GetAwaiter()
                            .GetResult();
                        throw initializationFailure;
                    }));
        }
        finally
        {
            releaseHandler.TrySetResult();
            if (activeTick is not null)
            {
                _ = await Record.ExceptionAsync(
                    () => activeTick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        Assert.Same(initializationFailure, observed);
        Assert.NotNull(failedSession);
        Assert.Throws<ObjectDisposedException>(() => failedSession.RegisterTable(
            new(new(106), 1, LocalResourceDomain.Memory, 1)));
    }

    [Fact]
    public void DefaultManagerFactoryOwnsItsSession()
    {
        var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var session = manager.Session;

        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.RegisterTable(
            new(new(901), 1, LocalResourceDomain.Memory, 1)));
    }

    [Fact]
    public void DefaultManagerConstructorBorrowsItsSession()
    {
        using var session = CreateSession(4);
        var manager = new DefaultLocalResourceManager(session);

        manager.Dispose();

        _ = session.RegisterTable(new(new(902), 1, LocalResourceDomain.Memory, 1));
    }

    [Fact]
    public void SessionAllowsOnlyOneManagerOrPumpOwnerAndTransfersAfterClose()
    {
        using var session = CreateSession(4);
        var first = new DefaultLocalResourceManager(session);

        var managerError = Assert.Throws<InvalidOperationException>(
            () => new DefaultLocalResourceManager(session));
        var pumpError = Assert.Throws<InvalidOperationException>(
            () => new LocalResourceManagerPump(session));

        Assert.Contains("active manager or pump owner", managerError.Message);
        Assert.Equal(managerError.Message, pumpError.Message);
        first.Dispose();

        using (var pump = new LocalResourceManagerPump(session))
        {
            Assert.Throws<InvalidOperationException>(
                () => new DefaultLocalResourceManager(session));
        }

        using var replacement = new DefaultLocalResourceManager(session);
        Assert.False(replacement.IsBackgroundWorkerRunning);
    }

    [Fact]
    public async Task ClaimedSessionRejectsCapturedRawControlAndPublicExecutorBeforeMutation()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(903), 1, LocalResourceDomain.Memory, 4));
        var calls = 0;
        var capability = session.Capabilities.Register(
            table,
            new(903, 9030, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        var resource = session.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourceIntent intent;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            intent = Assert.Single(operation.PlanMode(
                new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
        }
        var capturedRegistry = session.Capabilities;
        var capturedExecutor = new LocalResourceIntentExecutor(session);
        var before = session.ReadResource(resource);
        using var manager = new DefaultLocalResourceManager(session);

        var touchError = Assert.Throws<InvalidOperationException>(() => session.Touch(resource));
        var epochError = Assert.Throws<InvalidOperationException>(
            () => session.AdvanceActivityEpoch());
        var capabilityError = Assert.Throws<InvalidOperationException>(() =>
            capturedRegistry.Replace(
                capability,
                new(903, 9031, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
                static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect)));
        var planError = Assert.Throws<InvalidOperationException>(() =>
            session.BeginOperation(LocalResourceOperationKind.Cleanup));
        var recoveryError = Assert.Throws<InvalidOperationException>(
            session.GetRecoveryRequiredExecutions);
        var closeError = Assert.Throws<InvalidOperationException>(session.Dispose);
        var executeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => capturedExecutor.ExecuteAsync(intent).AsTask());

        foreach (var error in new[]
        {
            touchError,
            epochError,
            capabilityError,
            planError,
            recoveryError,
            closeError
        })
        {
            Assert.Contains("active manager or pump owner", error.Message);
        }
        Assert.Contains("requires the LocalResourceOperation", executeError.Message);
        Assert.Equal(0, calls);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(1, capturedRegistry.RegisteredCount);
        Assert.Equal(before, session.ReadResource(resource));

        manager.Touch(resource);
        Assert.True(session.ReadResource(resource).ActivityRevision > before.ActivityRevision);
    }

    [Fact]
    public async Task CapabilityHandlerCannotMutateClaimedRawSessionEvenWithoutExecutionContext()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var session = manager.Session;
        var table = manager.RegisterTable(new(new(904), 1, LocalResourceDomain.Memory, 4));
        LocalResourceCapabilityHandle capability = default;
        LocalResourceHandle resource = default;
        Exception? touchError = null;
        Exception? capabilityError = null;
        Exception? disposeError = null;
        Exception? detachedTouchError = null;
        capability = manager.RegisterCapability(
            table,
            new(904, 9040, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, _) =>
            {
                touchError = Record.Exception(() => session.Touch(resource));
                capabilityError = Record.Exception(() => session.Capabilities.Replace(
                    capability,
                    new(904, 9041, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
                    static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect)));
                disposeError = Record.Exception(session.Dispose);
                Task<Exception?> detached;
                using (ExecutionContext.SuppressFlow())
                {
                    detached = Task.Run(() =>
                        (Exception?)Record.Exception(() => session.Touch(resource)));
                }
                detachedTouchError = await detached;
                return LocalResourceEffect.NoEffect;
            });
        resource = manager.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        var before = session.ReadResource(resource);

        var result = await manager.TickAsync();

        foreach (var error in new[] { touchError, capabilityError, disposeError })
        {
            var lifecycleError = Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                lifecycleError.Message);
        }
        var detachedOwnerError = Assert.IsType<InvalidOperationException>(detachedTouchError);
        Assert.Equal(
            "A local resource capability handler cannot enter any local resource manager lifecycle.",
            detachedOwnerError.Message);
        Assert.Equal(1, result.ModeActionCount);
        Assert.Empty(result.UncertainExecutions);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(before.ActivityRevision, session.ReadResource(resource).ActivityRevision);
        Assert.Equal(1, session.Capabilities.RegisteredCount);
    }

    [Fact]
    public async Task SuppressedFlowHandlerRejectsGateEnteringLifecyclesWithoutDeadlock()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(911), 1, LocalResourceDomain.Memory, 4));
        var resource = default(LocalResourceHandle);
        var capturedIntent = default(LocalResourceIntent);
        var capturedExecutor = new LocalResourceIntentExecutor(session);
        DefaultLocalResourceManager? manager = null;
        (Exception? Execute, Exception? Close, Exception? Owner, Exception? Touch,
            Exception? Recovery) detachedErrors = default;
        _ = session.Capabilities.Register(
            table,
            new(911, 9110, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, _) =>
            {
                Task<(Exception Execute, Exception Close, Exception Owner, Exception Touch,
                    Exception Recovery)> detached;
                using (ExecutionContext.SuppressFlow())
                {
                    detached = Task.Run(async () =>
                    {
                        var execute = await Record.ExceptionAsync(
                            () => capturedExecutor.ExecuteAsync(capturedIntent).AsTask());
                        var close = Record.Exception(() => manager!.TryClose());
                        var owner = Record.Exception(() =>
                        {
                            using var unexpected = new LocalResourceManagerPump(session);
                        });
                        var touch = Record.Exception(() => manager!.Touch(resource));
                        var recovery = Record.Exception(
                            session.GetRecoveryRequiredExecutions);
                        return (execute, close, owner, touch, recovery);
                    });
                }
                detachedErrors = await detached.WaitAsync(TimeSpan.FromSeconds(5));
                return LocalResourceEffect.NoEffect;
            });
        resource = session.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            capturedIntent = Assert.Single(operation.PlanMode(
                new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
        }
        using (manager = new DefaultLocalResourceManager(session))
        {
            manager.SetDesiredMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                MaximumIntentsPerTick: 1,
                Generation: 1));

            var result = await manager.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));

            foreach (var error in new[]
            {
                detachedErrors.Execute,
                detachedErrors.Close,
                detachedErrors.Owner,
                detachedErrors.Touch,
                detachedErrors.Recovery
            })
            {
                var active = Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(
                    "A local resource capability handler cannot enter any local resource manager lifecycle.",
                    active.Message);
            }
            Assert.Equal(1, result.ModeActionCount);
            manager.Touch(resource);
        }
    }

    [Fact]
    public async Task QueuedOperationIsRejectedWhenCapabilityHandlerStartsAfterAdmission()
    {
        using var session = CreateSession(4);
        using var pump = new LocalResourceManagerPump(session);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var ownerField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField("_owner", nonPublicInstance));
        var owner = ownerField.GetValue(pump);
        Assert.NotNull(owner);
        var wait = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
            "WaitForManagerOperation",
            nonPublicInstance));
        var waitAsync = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
            "WaitForManagerOperationAsync",
            nonPublicInstance));
        var enterHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
            "EnterCapabilityHandler",
            nonPublicInstance));
        var exitHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
            "ExitCapabilityHandler",
            nonPublicInstance));
        var release = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
            "ReleaseOperation",
            nonPublicInstance));

        wait.Invoke(session, [owner, LocalResourceOperationKind.Maintenance]);
        try
        {
            var queued = Assert.IsAssignableFrom<Task>(waitAsync.Invoke(
                session,
                [owner, CancellationToken.None, LocalResourceOperationKind.Maintenance]));
            enterHandler.Invoke(session, [owner]);
            try
            {
                var active = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => queued.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("capability handler is active", active.Message);
            }
            finally
            {
                exitHandler.Invoke(session, null);
            }
        }
        finally
        {
            release.Invoke(session, [owner]);
        }

        wait.Invoke(session, [owner, LocalResourceOperationKind.Maintenance]);
        release.Invoke(session, [owner]);
    }

    [Fact]
    public async Task QueuedOperationIsRejectedWhenAnotherSessionHandlerStartsAfterAdmission()
    {
        using var sourceSession = CreateSession(4);
        using var targetSession = CreateSession(4);
        using var sourcePump = new LocalResourceManagerPump(sourceSession);
        using var targetPump = new LocalResourceManagerPump(targetSession);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var ownerField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField("_owner", nonPublicInstance));
        var sourceOwner = ownerField.GetValue(sourcePump);
        var targetOwner = ownerField.GetValue(targetPump);
        Assert.NotNull(sourceOwner);
        Assert.NotNull(targetOwner);
        var wait = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "WaitForManagerOperation",
                nonPublicInstance));
        var waitAsync = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "WaitForManagerOperationAsync",
                nonPublicInstance));
        var enterHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "EnterCapabilityHandler",
                nonPublicInstance));
        var exitHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ExitCapabilityHandler",
                nonPublicInstance));
        var release = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ReleaseOperation",
                nonPublicInstance));

        wait.Invoke(sourceSession, [sourceOwner, LocalResourceOperationKind.Maintenance]);
        wait.Invoke(targetSession, [targetOwner, LocalResourceOperationKind.Maintenance]);
        try
        {
            var queued = Assert.IsAssignableFrom<Task>(waitAsync.Invoke(
                targetSession,
                [
                    targetOwner,
                    CancellationToken.None,
                    LocalResourceOperationKind.Maintenance,
                ]));
            enterHandler.Invoke(sourceSession, [sourceOwner]);
            try
            {
                var active = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => queued.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("capability handler is active", active.Message);
            }
            finally
            {
                exitHandler.Invoke(sourceSession, null);
                release.Invoke(sourceSession, [sourceOwner]);
            }
        }
        finally
        {
            release.Invoke(targetSession, [targetOwner]);
        }

        wait.Invoke(targetSession, [targetOwner, LocalResourceOperationKind.Maintenance]);
        release.Invoke(targetSession, [targetOwner]);
    }

    [Fact]
    public void OwnedRecoverySnapshotSurvivesAnotherSessionHandlerWhilePublicQueriesFail()
    {
        using var admittedSession = CreateSession(4);
        using var handlerSession = CreateSession(4);
        using var admittedPump = new LocalResourceManagerPump(admittedSession);
        using var handlerPump = new LocalResourceManagerPump(handlerSession);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var ownerField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField("_owner", nonPublicInstance));
        var admittedOwner = ownerField.GetValue(admittedPump);
        var handlerOwner = ownerField.GetValue(handlerPump);
        Assert.NotNull(admittedOwner);
        Assert.NotNull(handlerOwner);
        var wait = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "WaitForManagerOperation",
                nonPublicInstance));
        var release = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ReleaseOperation",
                nonPublicInstance));
        var enterHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "EnterCapabilityHandler",
                nonPublicInstance));
        var exitHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ExitCapabilityHandler",
                nonPublicInstance));
        var ownedSnapshot = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "GetRecoveryRequiredExecutionsOwned",
                nonPublicInstance));

        wait.Invoke(handlerSession, [handlerOwner, LocalResourceOperationKind.Maintenance]);
        wait.Invoke(
            admittedSession,
            [admittedOwner, LocalResourceOperationKind.Maintenance]);
        try
        {
            enterHandler.Invoke(handlerSession, [handlerOwner]);
            try
            {
                foreach (var query in new Func<IReadOnlyList<LocalResourceUncertainExecution>>[]
                {
                    admittedSession.GetRecoveryRequiredExecutions,
                    handlerSession.GetRecoveryRequiredExecutions
                })
                {
                    var active = Assert.Throws<InvalidOperationException>(() => query());
                    Assert.Equal(
                        "A local resource capability handler cannot enter any local resource manager lifecycle.",
                        active.Message);
                }

                var recovery = Assert.IsAssignableFrom<
                    IReadOnlyList<LocalResourceUncertainExecution>>(
                    ownedSnapshot.Invoke(admittedSession, [admittedOwner]));
                Assert.Empty(recovery);
            }
            finally
            {
                exitHandler.Invoke(handlerSession, null);
                release.Invoke(handlerSession, [handlerOwner]);
            }
        }
        finally
        {
            release.Invoke(admittedSession, [admittedOwner]);
        }
    }

    [Fact]
    public void OperationsRemainRejectedUntilEveryCapabilityHandlerExits()
    {
        using var session = CreateSession(4);
        using var pump = new LocalResourceManagerPump(session);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var owner = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField("_owner", nonPublicInstance))
            .GetValue(pump);
        Assert.NotNull(owner);
        var wait = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "WaitForManagerOperation",
                nonPublicInstance));
        var enterHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "EnterCapabilityHandler",
                nonPublicInstance));
        var exitHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ExitCapabilityHandler",
                nonPublicInstance));
        var release = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ReleaseOperation",
                nonPublicInstance));
        var entered = 0;
        var operationHeld = false;
        try
        {
            wait.Invoke(session, [owner, LocalResourceOperationKind.Maintenance]);
            operationHeld = true;
            enterHandler.Invoke(session, [owner]);
            entered++;
            enterHandler.Invoke(session, [owner]);
            entered++;
            exitHandler.Invoke(session, null);
            entered--;

            var active = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => wait.Invoke(
                    session,
                    [owner, LocalResourceOperationKind.Maintenance]));
            Assert.Contains(
                "capability handler is active",
                Assert.IsType<InvalidOperationException>(active.InnerException).Message);

            exitHandler.Invoke(session, null);
            entered--;
            release.Invoke(session, [owner]);
            operationHeld = false;
            wait.Invoke(session, [owner, LocalResourceOperationKind.Maintenance]);
            release.Invoke(session, [owner]);
        }
        finally
        {
            while (entered-- > 0)
            {
                exitHandler.Invoke(session, null);
            }
            if (operationHeld)
            {
                release.Invoke(session, [owner]);
            }
        }
    }

    [Fact]
    public async Task DefaultManagerDisposeRejectsInflightHandlerAndSucceedsAfterward()
    {
        var session = CreateSession(4);
        var table = session.RegisterTable(new(new(101), 1, LocalResourceDomain.Memory, 1));
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var trim = session.Capabilities.Register(
            table,
            new(101, 101, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                handlerEntered.TrySetResult();
                await allowHandlerToFinish.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        session.RegisterResource(
            table,
            new(
                new(101),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.ObservableNow));
        var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        using var cancellation = new CancellationTokenSource();

        var tick = manager.TickAsync(cancellation.Token);
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var active = Assert.Throws<InvalidOperationException>(manager.Dispose);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                active.Message);

            allowHandlerToFinish.TrySetResult();
            _ = await tick.WaitAsync(TimeSpan.FromSeconds(10));
            manager.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => manager.TickAsync());
            Assert.Throws<ObjectDisposedException>(
                () => manager.Reconcile(null!, LocalResourceEffect.NoEffect));
        }
        finally
        {
            allowHandlerToFinish.TrySetResult();
            if (!tick.IsCompleted)
            {
                cancellation.Cancel();
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(manager.Dispose);
                _ = Record.Exception(session.Dispose);
            }
        }
    }

    [Fact]
    public async Task FailedConstructionCleanupClosesPrivateSessionDuringAnotherHandler()
    {
        var privateSession = CreateSession(1);
        using var activeSession = CreateSession(1);
        var activeTable = activeSession.RegisterTable(
            new(new(102), 1, LocalResourceDomain.Memory, 1));
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        activeSession.Capabilities.Register(
            activeTable,
            new(102, 1020, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        activeSession.RegisterResource(
            activeTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var activeManager = new DefaultLocalResourceManager(activeSession);
        activeManager.SetDesiredMode(new(
            activeTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = activeManager.TickAsync();
        var cleanupCompleted = false;
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            privateSession.CloseAfterFailedManagerConstruction();
            privateSession.CloseAfterFailedManagerConstruction();
            cleanupCompleted = true;
        }
        finally
        {
            releaseHandler.TrySetResult();
            _ = await Record.ExceptionAsync(
                () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            if (!cleanupCompleted)
            {
                _ = Record.Exception(privateSession.Dispose);
            }
        }

        Assert.Throws<ObjectDisposedException>(() =>
            privateSession.RegisterTable(
                new(new(103), 1, LocalResourceDomain.Memory, 1)));
    }

    [Fact]
    public async Task OwnedCloseContinuationIgnoresAnotherSessionHandler()
    {
        var ownedSession = CreateSession(1);
        var owner = ownedSession.AcquireManagerOwner();
        ownedSession.WaitForOwnedCloseAdmission(owner);
        var closeAdmissionHeld = true;
        var ownedSessionClosed = false;
        using var activeSession = CreateSession(1);
        var activeTable = activeSession.RegisterTable(
            new(new(104), 1, LocalResourceDomain.Memory, 1));
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        activeSession.Capabilities.Register(
            activeTable,
            new(104, 1040, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        activeSession.RegisterResource(
            activeTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var activeManager = new DefaultLocalResourceManager(activeSession);
        activeManager.SetDesiredMode(new(
            activeTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = activeManager.TickAsync();
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                LocalResourceManagerCloseResult.Closed,
                ownedSession.TryCloseOwned(owner, out var pendingExecutionCount));
            Assert.Equal(0, pendingExecutionCount);
            ownedSessionClosed = true;
        }
        finally
        {
            if (closeAdmissionHeld)
            {
                ownedSession.ReleaseOwnedCloseAdmission();
                closeAdmissionHeld = false;
            }
            releaseHandler.TrySetResult();
            _ = await Record.ExceptionAsync(
                () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            if (!ownedSessionClosed)
            {
                _ = Record.Exception(() => ownedSession.ReleaseManagerOwner(owner));
                _ = Record.Exception(ownedSession.Dispose);
            }
        }

        Assert.Equal(LocalResourceManagerCloseResult.Closed, ownedSession.TryClose());
    }

    [Fact]
    public void RegistrationRequiresARealCapability()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(1), 1, LocalResourceDomain.Memory, 1));
        var exception = Assert.Throws<NativeLocalResourceManagerException>(() => session.RegisterResource(
            table,
            new(
                new(1),
                0,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow)));
        Assert.Equal(LocalResourceManagerError.InvalidArgument, exception.Error);
    }

    [Fact]
    public void TableCapacityReservationsCannotOvercommitGlobalResourceSlots()
    {
        using var session = CreateSession(10);
        var first = session.RegisterTable(new(new(301), 1, LocalResourceDomain.Memory, 6));

        var exception = Assert.Throws<NativeLocalResourceManagerException>(() =>
            session.RegisterTable(new(new(302), 1, LocalResourceDomain.Memory, 5)));

        Assert.Equal(LocalResourceManagerError.ResourceFull, exception.Error);
        session.UnregisterTable(first);
        _ = session.RegisterTable(new(new(302), 1, LocalResourceDomain.Memory, 5));
    }

    [Fact]
    public async Task DangerousTableWithoutSlotReleaseCandidateReportsExactStoppedIdentity()
    {
        using var session = CreateSession(10);
        var table = session.RegisterTable(new(new(0xA1, 0xB2), 7, LocalResourceDomain.Memory, 10));
        var trim = session.Capabilities.Register(
            table,
            new(1, 10, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        for (ulong resourceId = 1; resourceId <= 10; resourceId++)
        {
            session.RegisterResource(
                table,
                new(
                    new(resourceId),
                    4096,
                    1,
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var manager = new DefaultLocalResourceManager(session);

        var result = await manager.TickAsync();

        Assert.Equal(1, result.CapacityPlan.TriggeredTableCount);
        Assert.Equal(1, result.CapacityPlan.StalledTableCount);
        Assert.Equal((table.TableId, table.TableIncarnation), Assert.Single(result.StoppedTables));
        var tableResult = Assert.Single(result.Tables);
        Assert.Equal(table.TableId, tableResult.TableId);
        Assert.Equal(table.TableIncarnation, tableResult.TableIncarnation);
        Assert.True(tableResult.Stopped);
        Assert.False(tableResult.CapacityGoalReached);
        Assert.Equal(0, tableResult.AfterCapacity.FreeCount);
    }

    [Fact]
    public async Task ByteChangeDoesNotReleaseLedgerSlot()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(2), 1, LocalResourceDomain.Memory, 1));
        var trim = session.Capabilities.Register(
            table,
            new(1, 10, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Applied(
                LocalResourceEffects.ChangesSizeBytes,
                sizeBytesAfter: 1024,
                releasedBytes: 3072)));
        var resource = session.RegisterResource(
            table,
            new(
                new(1),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.ObservableNow));
        LocalResourceExecutionResult receipt;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var plan = operation.PlanMode(
                new(table, LocalResourceCleanupMode.Optimize, 1));
            Assert.Single(plan.Intents);
            receipt = await operation.ExecuteAsync(plan.Intents[0]);
        }

        Assert.False(receipt.ResourceSlotReleased);
        Assert.Equal(0, session.ReadCapacity(table).FreeCount);
        Assert.Equal(1024UL, session.ReadResource(resource).SizeBytes);
    }

    [Fact]
    public void ModeMaximumIntentsBoundsNativeOutputAndKeepsBestRankedResource()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(201), 1, LocalResourceDomain.Memory, 4));
        _ = session.Capabilities.Register(
            table,
            new(201, 2010, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        foreach (var resourceUid in new ulong[] { 30, 20, 10 })
        {
            _ = session.RegisterResource(
                table,
                new(
                    new(resourceUid),
                    1,
                    1,
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }

        LocalResourceIntent intent;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            intent = Assert.Single(operation.PlanMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                MaximumIntents: 1)).Intents);
        }

        Assert.Equal(new LocalResourceId(10), intent.ResourceUid);
    }

    [Fact]
    public async Task MissingManagedCapabilityAbortsWithoutCountingInvocation()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(107), 1, LocalResourceDomain.Memory, 1));
        _ = session.Capabilities.Register(
            table,
            new(107, 1070, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        session.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var owner = session.AcquireManagerOwner();
        session.WaitForManagerOperation(owner, LocalResourceOperationKind.Cleanup);
        try
        {
            var intent = Assert.Single(session.PlanModeOwned(
                owner,
                new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
            session.Capabilities.RemoveTable(table);
            var executor = new LocalResourceIntentExecutor(session, owner);

            var outcome = await executor.ExecuteOwnedAsync(intent, CancellationToken.None);

            Assert.False(outcome.CapabilityInvoked);
            Assert.False(outcome.Result.EffectUncertain);
            Assert.Null(outcome.Result.UncertainExecution);
            Assert.Equal(0, session.PendingExecutionCount);
        }
        finally
        {
            session.ReleaseOperation(owner);
            session.ReleaseManagerOwner(owner);
        }
    }

    [Fact]
    public async Task StandaloneExecutorRejectsNewWorkDuringHandlerAndAllowsRetry()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 8) with
        {
            MaximumConcurrentTables = 1
        };
        using var session = new NativeLocalResourceManagerSession(configuration);
        var firstTable = session.RegisterTable(new(new(905), 1, LocalResourceDomain.Memory, 4));
        var secondTable = session.RegisterTable(new(new(906), 1, LocalResourceDomain.Memory, 4));
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var secondCalls = 0;
        _ = session.Capabilities.Register(
            firstTable,
            new(905, 9050, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                RecordMaximum(ref maximumActive, current);
                firstEntered.TrySetResult();
                try
                {
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                    return LocalResourceEffect.NoEffect;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        _ = session.Capabilities.Register(
            secondTable,
            new(906, 9060, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                var current = Interlocked.Increment(ref active);
                RecordMaximum(ref maximumActive, current);
                Interlocked.Increment(ref secondCalls);
                Interlocked.Decrement(ref active);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            firstTable,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(2), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var firstIntent = Assert.Single(operation.PlanMode(
            new(firstTable, LocalResourceCleanupMode.Optimize, 1)).Intents);
        var secondIntent = Assert.Single(operation.PlanMode(
            new(secondTable, LocalResourceCleanupMode.Optimize, 1)).Intents);

        var first = operation.ExecuteAsync(firstIntent).AsTask();
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = operation.ExecuteAsync(secondIntent).AsTask();
            var activeError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                activeError.Message);
            Assert.Equal(1, session.PendingExecutionCount);
            Assert.Equal(0, secondCalls);
        }
        finally
        {
            releaseFirst.TrySetResult();
            _ = await Record.ExceptionAsync(
                () => first.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        _ = await first.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await operation.ExecuteAsync(secondIntent).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maximumActive);
        Assert.Equal(1, secondCalls);
        Assert.Equal(0, session.PendingExecutionCount);
    }

    [Fact]
    public async Task RecoveryRequiredOperationRejectsTouchWithoutChangingItsSnapshot()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(907), 1, LocalResourceDomain.Memory, 4));
        _ = session.Capabilities.Register(
            table,
            new(907, 9070, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        var resource = session.RegisterResource(
            table,
            new(
                new(1),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourceExecutionResult result;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var intent = Assert.Single(operation.PlanMode(
                new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
            result = await operation.ExecuteAsync(intent);
        }
        var executor = new LocalResourceIntentExecutor(session);
        var execution = Assert.IsType<LocalResourceUncertainExecution>(result.UncertainExecution);
        var before = session.ReadResource(resource);

        var rejected = Assert.Throws<NativeLocalResourceManagerException>(
            () => session.Touch(resource));

        Assert.Equal(LocalResourceManagerError.RecoveryRequired, rejected.Error);
        Assert.Equal(before, session.ReadResource(resource));
        _ = executor.Reconcile(execution, LocalResourceEffect.NoEffect);
        Assert.Equal(0, session.PendingExecutionCount);
    }

    [Fact]
    public async Task SequentialOperationsRejectPriorIntentBeforeHandlerInvocation()
    {
        using var session = CreateSession(2);
        var table = session.RegisterTable(new(new(202), 1, LocalResourceDomain.Memory, 2));
        var handlerCalls = 0;
        _ = session.Capabilities.Register(
            table,
            new(202, 2020, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));

        LocalResourceIntent priorIntent;
        LocalResourceOperationToken priorToken;
        using (var prior = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            priorToken = prior.Token;
            priorIntent = Assert.Single(prior.PlanMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                1)).Intents);
        }

        using var current = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        Assert.True(current.OperationId > priorToken.OperationId);
        Assert.True(current.OperationGeneration > priorToken.OperationGeneration);
        var stale = await Assert.ThrowsAsync<NativeLocalResourceManagerException>(
            () => current.ExecuteAsync(priorIntent).AsTask());
        Assert.Equal(LocalResourceManagerError.StaleOperation, stale.Error);
        Assert.Equal(0, handlerCalls);

        var freshIntent = Assert.Single(current.PlanMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            1)).Intents);
        Assert.Equal(current.OperationId, freshIntent.OperationId);
        Assert.Equal(current.OperationGeneration, freshIntent.OperationGeneration);
        _ = await current.ExecuteAsync(freshIntent);
        Assert.Equal(1, handlerCalls);
    }

    [Fact]
    public void OperationKindsRejectCrossDomainActionsWithoutChangingTheSnapshot()
    {
        using var session = CreateSession(2);
        var table = session.RegisterTable(new(new(203), 1, LocalResourceDomain.Memory, 1));

        using (var cleanup = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var before = session.ReadOperation();
            Assert.Throws<InvalidOperationException>(() => cleanup.RegisterTable(
                new(new(204), 1, LocalResourceDomain.Memory, 1)));
            Assert.Throws<InvalidOperationException>(() =>
                cleanup.PlanPartitionAdmission(table, 1));
            var after = session.ReadOperation();
            Assert.Equal(before.CurrentSnapshotGeneration, after.CurrentSnapshotGeneration);
            Assert.Equal(before.Token, after.Token);
            Assert.Equal(LocalResourceOperationPhase.Planning, after.Phase);
        }

        using var maintenance = session.BeginOperation(LocalResourceOperationKind.Maintenance);
        var maintenanceBefore = session.ReadOperation();
        Assert.Throws<InvalidOperationException>(() => maintenance.PlanMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            1)));
        Assert.Equal(
            maintenanceBefore.CurrentSnapshotGeneration,
            session.ReadOperation().CurrentSnapshotGeneration);
    }

    [Fact]
    public async Task QueuedPartitionOperationWaitsForCleanupAndCanceledWaitConsumesNoIdentity()
    {
        using var session = CreateSession(1);
        var cleanup = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var cleanupToken = cleanup.Token;
        using var canceled = new CancellationTokenSource();
        var canceledWait = session.BeginOperationAsync(
            LocalResourceOperationKind.PartitionAdmission,
            canceled.Token).AsTask();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.Equal(cleanupToken, session.ReadOperation().Token);

        var queued = session.BeginOperationAsync(
            LocalResourceOperationKind.PartitionAdmission).AsTask();
        await Task.Yield();
        Assert.False(queued.IsCompleted);
        Assert.Equal(cleanupToken, session.ReadOperation().Token);

        cleanup.Dispose();
        cleanup.Dispose();
        using var partition = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalResourceOperationKind.PartitionAdmission, partition.Kind);
        Assert.Equal(cleanupToken.OperationId + 1, partition.OperationId);
        Assert.Equal(cleanupToken.OperationGeneration + 1, partition.OperationGeneration);
        Assert.Throws<ObjectDisposedException>(() =>
            cleanup.PlanCapacity(LocalResourceCapacityStrategy.Concentrated));
    }

    [Fact]
    public async Task RecoveryRetainsOriginalOperationIdentityUntilFinalReconciliation()
    {
        using var session = CreateSession(1);
        var table = session.RegisterTable(new(new(205), 1, LocalResourceDomain.Memory, 1));
        _ = session.Capabilities.Register(
            table,
            new(205, 2050, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        session.RegisterResource(
            table,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));

        var cleanup = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var cleanupToken = cleanup.Token;
        var intent = Assert.Single(cleanup.PlanCapacity(
            LocalResourceCapacityStrategy.Concentrated).Intents);
        var result = await cleanup.ExecuteAsync(intent);
        cleanup.Dispose();
        var uncertain = Assert.IsType<LocalResourceUncertainExecution>(
            result.UncertainExecution);
        var recovery = session.ReadOperation();
        Assert.Equal(LocalResourceOperationPhase.RecoveryRequired, recovery.Phase);
        Assert.Equal(cleanupToken, recovery.Token);
        Assert.Equal(1, recovery.PendingCount);
        Assert.Equal(1, recovery.UncertainExecutionCount);

        var blocked = Assert.Throws<NativeLocalResourceManagerException>(() =>
            session.BeginOperation(LocalResourceOperationKind.Maintenance));
        Assert.Equal(LocalResourceManagerError.RecoveryRequired, blocked.Error);
        Assert.Equal(cleanupToken, session.ReadOperation().Token);

        var executor = new LocalResourceIntentExecutor(session);
        _ = executor.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        var completed = session.ReadOperation();
        Assert.Equal(LocalResourceOperationPhase.Inactive, completed.Phase);
        Assert.Null(completed.Token);
        Assert.Equal(0, completed.PendingCount);

        using var maintenance = session.BeginOperation(LocalResourceOperationKind.Maintenance);
        Assert.Equal(cleanupToken.OperationId + 1, maintenance.OperationId);
        Assert.Equal(cleanupToken.OperationGeneration + 1, maintenance.OperationGeneration);
    }

    [Fact]
    public void MergePlansPreservesSameSessionCapacityPriorityAndProducerStamp()
    {
        using var session = CreateSession(4);
        var (table, _) = RegisterMergeFixture(session, 908);
        using var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var capacity = operation.PlanCapacity(LocalResourceCapacityStrategy.Concentrated);
        var mode = operation.PlanMode(new(table, LocalResourceCleanupMode.Optimize, 1));

        var merged = operation.MergePlans(capacity, mode);

        Assert.NotEqual(0UL, capacity.ManagerInstanceId);
        Assert.Equal(capacity.ManagerInstanceId, mode.ManagerInstanceId);
        Assert.Equal(capacity.ManagerInstanceId, merged.ManagerInstanceId);
        Assert.Equal(capacity.SnapshotGeneration, merged.SnapshotGeneration);
        Assert.Empty(typeof(LocalResourcePlan).GetConstructors());
        Assert.All(
            typeof(LocalResourcePlan).GetProperties(),
            static property => Assert.Null(property.SetMethod));
        Assert.IsNotType<LocalResourceIntent[]>(capacity.Intents);
        var intent = Assert.Single(merged.Intents);
        Assert.Equal(
            LocalResourceIntentReason.Capacity | LocalResourceIntentReason.Mode,
            intent.Reason);
    }

    [Fact]
    public void PlanSnapshotsCloneInputsRejectListMutationAndSurviveScratchReuse()
    {
        using var session = CreateSession(4);
        var (_, resource) = RegisterMergeFixture(session, 916);
        LocalResourcePlan capacity;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            capacity = operation.PlanCapacity(LocalResourceCapacityStrategy.Concentrated);
        }
        var constructorInput = capacity.Intents.ToArray();
        var snapshot = ForgePlanForTest(capacity, "Capacity", constructorInput);
        var frozen = Assert.Single(snapshot.Intents);

        constructorInput[0] = default;
        var list = Assert.IsAssignableFrom<IList<LocalResourceIntent>>(snapshot.Intents);
        Assert.Throws<NotSupportedException>(() => list[0] = default);
        session.Touch(resource);
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            _ = operation.PlanCapacity(LocalResourceCapacityStrategy.Concentrated);
        }

        Assert.Equal(frozen, Assert.Single(snapshot.Intents));
    }

    [Fact]
    public void MergePlansRejectsForeignEmptyForeignIntentAndDifferentSnapshotPlans()
    {
        using var first = CreateSession(4);
        using var second = CreateSession(4);
        var (firstTable, firstResource) = RegisterMergeFixture(first, 909);
        var (secondTable, _) = RegisterMergeFixture(second, 909);
        LocalResourcePlan firstCapacity;
        LocalResourcePlan secondMode;
        LocalResourcePlan firstEmpty;
        LocalResourcePlan secondEmpty;
        using (var firstOperation = first.BeginOperation(LocalResourceOperationKind.Cleanup))
        using (var secondOperation = second.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            firstCapacity = firstOperation.PlanCapacity(
                LocalResourceCapacityStrategy.Concentrated);
            secondMode = secondOperation.PlanMode(
                new(secondTable, LocalResourceCleanupMode.Optimize, 1));
            firstEmpty = firstOperation.PlanMode(
                new(firstTable, LocalResourceCleanupMode.Normal, 0));
            secondEmpty = secondOperation.PlanMode(
                new(secondTable, LocalResourceCleanupMode.Normal, 0));
            var firstEmptyCapacity = ForgePlanForTest(
                firstEmpty,
                "Capacity",
                []);

            var emptyMerged = firstOperation.MergePlans(firstEmptyCapacity, firstEmpty);

            var foreign = Assert.Throws<ArgumentException>(
                () => firstOperation.MergePlans(firstCapacity, secondMode));
            var foreignEmpty = Assert.Throws<ArgumentException>(
                () => firstOperation.MergePlans(firstEmptyCapacity, secondEmpty));
            var wrongKind = Assert.Throws<ArgumentException>(
                () => firstOperation.MergePlans(firstEmpty, firstEmpty));
            var forgedForeignIntent = ForgePlanForTest(
                firstCapacity,
                "Mode",
                secondMode.Intents.ToArray());
            var foreignIntent = Assert.Throws<ArgumentException>(
                () => firstOperation.MergePlans(firstCapacity, forgedForeignIntent));

            Assert.Equal("mode", foreign.ParamName);
            Assert.Equal("mode", foreignEmpty.ParamName);
            Assert.Equal("capacity", wrongKind.ParamName);
            Assert.Equal("mode", foreignIntent.ParamName);
            Assert.NotEqual(firstEmpty.ManagerInstanceId, secondEmpty.ManagerInstanceId);
            Assert.Empty(emptyMerged.Intents);
            Assert.Equal(0, emptyMerged.AffectedTableCount);
            Assert.Equal(firstEmpty.SnapshotGeneration, emptyMerged.SnapshotGeneration);
        }

        first.Touch(firstResource);
        using (var newerOperation = first.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var newerMode = newerOperation.PlanMode(
                new(firstTable, LocalResourceCleanupMode.Optimize, 1));
            Assert.NotEqual(firstCapacity.SnapshotGeneration, newerMode.SnapshotGeneration);
            Assert.Throws<ArgumentException>(
                () => newerOperation.MergePlans(firstCapacity, newerMode));
        }
    }

    [Fact]
    public void MergePlansAcceptsGenuineEmptyCapacityAndModePlans()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(
            new(new(917), 1, LocalResourceDomain.Memory, 4));
        _ = session.Capabilities.Register(
            table,
            new(
                917,
                9170,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        _ = session.RegisterResource(
            table,
            new(
                new(917),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var capacity = operation.PlanCapacity(LocalResourceCapacityStrategy.Concentrated);
        var mode = operation.PlanMode(
            new(table, LocalResourceCleanupMode.Normal, 0));

        var merged = operation.MergePlans(capacity, mode);

        Assert.Empty(capacity.Intents);
        Assert.Empty(mode.Intents);
        Assert.Empty(merged.Intents);
        Assert.Equal(0, merged.AffectedTableCount);
        Assert.Equal(capacity.SnapshotGeneration, merged.SnapshotGeneration);
    }

    [Fact]
    public void ManagedMergePostconditionsRejectMalformedSuccessfulNativeShapes()
    {
        using var session = CreateSession(8);
        var (firstTable, _) = RegisterMergeFixture(session, 918);
        _ = RegisterMergeFixture(session, 919);
        var spareTable = session.RegisterTable(
            new(new(920), 1, LocalResourceDomain.Memory, 2));
        _ = session.Capabilities.Register(
            spareTable,
            new(
                920,
                9200,
                LocalResourceEffects.ReleasesLedgerSlot |
                    LocalResourceEffects.ChangesSizeBytes,
                Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        _ = session.RegisterResource(
            spareTable,
            new(
                new(920),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var capacity = operation.PlanCapacity(LocalResourceCapacityStrategy.Concentrated);
        var mode = operation.PlanMode(
            new(firstTable, LocalResourceCleanupMode.Optimize, 1));
        var extraMode = operation.PlanMode(
            new(spareTable, LocalResourceCleanupMode.Optimize, 1));
        var merged = operation.MergePlans(capacity, mode);
        var modeOnlyMerged = operation.MergePlans(capacity, extraMode);
        Assert.Equal(2, capacity.Intents.Count);
        Assert.Single(mode.Intents);
        Assert.Single(extraMode.Intents);
        Assert.Equal(2, merged.Intents.Count);
        Assert.Equal(3, modeOnlyMerged.Intents.Count);
        Assert.Equal(
            LocalResourceIntentReason.Mode,
            modeOnlyMerged.Intents[^1].Reason);
        var validator = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ValidateMergedIntents",
                System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic));

        void Validate(
            IReadOnlyList<LocalResourceIntent> output,
            int? affectedTableCount = null)
            => validator.Invoke(
                session,
                [
                    capacity.Intents,
                    mode.Intents,
                    output,
                    capacity.OperationId,
                    capacity.OperationGeneration,
                    capacity.SnapshotGeneration,
                    affectedTableCount ?? merged.AffectedTableCount,
                ]);

        void Reject(IReadOnlyList<LocalResourceIntent> output, int? affectedTableCount = null)
        {
            var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => Validate(output, affectedTableCount));
            Assert.IsType<InvalidOperationException>(invocation.InnerException);
        }

        Validate(merged.Intents);
        Reject(merged.Intents.Take(1).ToArray());
        Reject([.. merged.Intents, merged.Intents[0]]);
        Reject([.. merged.Intents, extraMode.Intents[0]]);
        Reject(merged.Intents.Reverse().ToArray());
        Reject(merged.Intents, merged.AffectedTableCount + 1);

        var overlapIndex = Array.FindIndex(
            merged.Intents.ToArray(),
            static intent => intent.ResourceUid == new LocalResourceId(918));
        Assert.True(overlapIndex >= 0);
        var wrongReason = merged.Intents.ToArray();
        wrongReason[overlapIndex] = RewriteNativeIntentForTest(
            wrongReason[overlapIndex],
            "ReasonFlags",
            (byte)LocalResourceIntentReason.Capacity);
        Reject(wrongReason);

        var wrongPayload = merged.Intents.ToArray();
        wrongPayload[overlapIndex] = RewriteNativeIntentForTest(
            wrongPayload[overlapIndex],
            "ActionCode",
            checked(wrongPayload[overlapIndex].ActionCode + 1));
        Reject(wrongPayload);

        var shapeMutations = new (string FieldPath, object Value)[]
        {
            ("AbiVersion", LocalResourceManagerProtocol.Version + 1),
            ("StructSize", 0U),
            ("Resource.ManagerInstanceId", 0UL),
            ("Resource.TableSlotGeneration", 0UL),
            ("Resource.ResourceSlotGeneration", 0UL),
            ("Resource.TableId.Low", 0UL),
            ("Resource.TableIncarnation", 0UL),
            ("Resource.ResourceUid.Low", 0UL),
            ("Resource.TableSlotIndex", checked((uint)session.Configuration.TableCapacity)),
            ("Resource.ResourceSlotIndex", checked((uint)session.Configuration.ResourceCapacity)),
            ("SnapshotGeneration", 0UL),
            ("TableRevision", 0UL),
            ("RowRevision", 0UL),
            ("ActivityRevision", 0UL),
            ("UseGeneration", 0UL),
            ("CapabilityId", 0UL),
            ("CapabilityGeneration", 0UL),
            ("ActionCode", 0U),
            ("CapabilitySlotIndex", checked((uint)session.Configuration.CapabilityCapacity)),
            ("ReasonFlags", (byte)0x80),
            ("ExpectedEffects", (byte)0),
            ("Destructive", (byte)2),
            ("CapacityPhase", byte.MaxValue),
            ("AccessLossImpact", byte.MaxValue),
            ("Reserved0.FixedElementField", (byte)1),
        };
        foreach (var mutation in shapeMutations)
        {
            var invalidShape = merged.Intents.ToArray();
            invalidShape[overlapIndex] = RewriteNativeIntentForTest(
                invalidShape[overlapIndex],
                mutation.FieldPath,
                mutation.Value);
            Reject(invalidShape);
        }

        const System.Reflection.BindingFlags allInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public;
        var copyPlan = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "CopyPlan",
                System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic));
        var copyParameters = copyPlan.GetParameters();
        var mergedKind = Enum.Parse(copyParameters[0].ParameterType, "Merged");
        var summaryType = copyParameters[1].ParameterType;
        var nativeIntentType = copyParameters[2].ParameterType.GetElementType();
        Assert.NotNull(nativeIntentType);
        var nativeProperty = Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(
            typeof(LocalResourceIntent).GetProperty("Native", allInstance));
        var nativeOutput = Array.CreateInstance(nativeIntentType, merged.Intents.Count);
        for (var index = 0; index < merged.Intents.Count; index++)
        {
            nativeOutput.SetValue(nativeProperty.GetValue(merged.Intents[index]), index);
        }

        object MakeSummary()
        {
            var summary = Activator.CreateInstance(summaryType);
            Assert.NotNull(summary);
            RewriteBoxedField(summary, ["OperationId"], 0,
                merged.OperationId, allInstance);
            RewriteBoxedField(summary, ["OperationGeneration"], 0,
                merged.OperationGeneration, allInstance);
            RewriteBoxedField(summary, ["SnapshotGeneration"], 0,
                merged.SnapshotGeneration, allInstance);
            RewriteBoxedField(summary, ["IntentCount"], 0,
                checked((uint)merged.Intents.Count), allInstance);
            RewriteBoxedField(summary, ["AffectedTableCount"], 0,
                checked((uint)merged.AffectedTableCount), allInstance);
            return summary;
        }

        object? Copy(object summary)
            => copyPlan.Invoke(
                session,
                [
                    mergedKind,
                    summary,
                    nativeOutput,
                    merged.SnapshotGeneration,
                    capacity.Intents,
                    mode.Intents,
                ]);

        void RejectSummary(string fieldPath, object value)
        {
            var summary = MakeSummary();
            RewriteBoxedField(summary, fieldPath.Split('.'), 0, value, allInstance);
            var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => Copy(summary));
            Assert.IsType<InvalidOperationException>(invocation.InnerException);
        }

        Assert.IsType<LocalResourcePlan>(Copy(MakeSummary()));
        RejectSummary("OperationId", 0UL);
        RejectSummary("OperationId", merged.OperationId + 1);
        RejectSummary("OperationGeneration", 0UL);
        RejectSummary("OperationGeneration", merged.OperationGeneration + 1);
        RejectSummary("SnapshotGeneration", 0UL);
        RejectSummary("SnapshotGeneration", merged.SnapshotGeneration + 1);
        RejectSummary("IntentCount", checked((uint)merged.Intents.Count + 1));
        RejectSummary("AffectedTableCount", checked((uint)merged.AffectedTableCount + 1));
        RejectSummary("TriggeredTableCount", 1U);
        RejectSummary("StalledTableCount", 1U);
        RejectSummary("EmergencyTableCount", 1U);
        RejectSummary("Reserved0", 1U);
    }

    [Fact]
    public async Task ConcentratedPumpReleasesToTwentyPercentAndRunsNoExtraModeAction()
    {
        using var session = CreateSession(16);
        var table = session.RegisterTable(new(new(3), 1, LocalResourceDomain.Memory, 10));
        var calls = 0;
        var release = session.Capabilities.Register(
            table,
            new(2, 20, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot));
            });
        for (ulong index = 1; index <= 10; index++)
        {
            session.RegisterResource(
                table,
                new(
                    new(index),
                    index * 1024,
                    checked((uint)index),
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }

        using (var pump = new LocalResourceManagerPump(session))
        {
            await pump.TickAsync(
                LocalResourceCapacityStrategy.Concentrated,
                Array.Empty<LocalResourceModeRequest>());
        }

        Assert.Equal(2, calls);
        Assert.Equal(2, session.ReadCapacity(table).FreeCount);
    }

    [Fact]
    public async Task PublicPumpRejectsOlderAndConflictingExplicitModeGenerations()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(912), 1, LocalResourceDomain.Memory, 4));
        var calls = 0;
        _ = session.Capabilities.Register(
            table,
            new(912, 9120, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var pump = new LocalResourceManagerPump(session);

        _ = await pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Normal, 0, Generation: 5)]);
        var older = await Assert.ThrowsAsync<InvalidOperationException>(() => pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Optimize, 1, Generation: 4)]));
        var rebound = await Assert.ThrowsAsync<InvalidOperationException>(() => pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Optimize, 1, Generation: 5)]));
        _ = await pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Normal, 0, Generation: 5)]);
        var newer = await pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Optimize, 1, Generation: 6)]);

        Assert.Contains("older generation", older.Message);
        Assert.Contains("rebound", rebound.Message);
        Assert.Equal(1, newer.ModeActionCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PublicPumpRejectsInvalidTablesWithoutPublishingModeAuthority()
    {
        using var session = CreateSession(2);
        var stale = session.RegisterTable(new(new(914), 1, LocalResourceDomain.Memory, 1));
        session.UnregisterTable(stale);
        var valid = session.RegisterTable(new(new(914), 2, LocalResourceDomain.Memory, 1));
        using var foreignSession = CreateSession(3);
        var foreign = foreignSession.RegisterTable(
            new(new(915), 1, LocalResourceDomain.Memory, 1));
        var otherForeign = foreignSession.RegisterTable(
            new(new(916), 1, LocalResourceDomain.Memory, 1));
        using var pump = new LocalResourceManagerPump(session);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var statesField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField(
                "_publicModeStates",
                nonPublicInstance));

        int StateCount()
        {
            var states = Assert.IsAssignableFrom<object>(statesField.GetValue(pump));
            var count = Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(
                states.GetType().GetProperty("Count"));
            return Assert.IsType<int>(count.GetValue(states));
        }

        async Task Reject(LocalResourceTableHandle table, ulong generation)
        {
            _ = await Assert.ThrowsAnyAsync<Exception>(() => pump.TickAsync(
                LocalResourceCapacityStrategy.Concentrated,
                [new(table, LocalResourceCleanupMode.Normal, 0, generation)]));
        }

        await Reject(default, 1);
        await Reject(stale, 2);
        await Reject(foreign, 3);
        await Reject(otherForeign, 4);
        Assert.Equal(0, StateCount());

        _ = await Assert.ThrowsAnyAsync<Exception>(() => pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [
                new(valid, LocalResourceCleanupMode.Normal, 0, Generation: 5),
                new(foreign, LocalResourceCleanupMode.Normal, 0, Generation: 5),
            ]));
        Assert.Equal(0, StateCount());

        _ = await pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(valid, LocalResourceCleanupMode.Normal, 0, Generation: 4)]);
        Assert.Equal(1, StateCount());
        Assert.True(StateCount() <= session.Configuration.TableCapacity);
    }

    [Fact]
    public async Task PublicPumpAutoGenerationSupersedesRemainingModeActions()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(913), 1, LocalResourceDomain.Memory, 4));
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _ = session.Capabilities.Register(
            table,
            new(913, 9130, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                return LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot);
            });
        for (ulong resourceId = 1; resourceId <= 2; resourceId++)
        {
            session.RegisterResource(
                table,
                new(new(resourceId), 4096, 1, LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var pump = new LocalResourceManagerPump(session);

        var first = pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.ReleaseAll, 2)]);
        Task<LocalResourceManagerTickResult>? update = null;
        Exception? updateFailure = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            update = pump.TickAsync(
                LocalResourceCapacityStrategy.Concentrated,
                [new(table, LocalResourceCleanupMode.Normal, 0)]);
            updateFailure = await Record.ExceptionAsync(
                () => update.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseFirst.TrySetResult();
            _ = await Record.ExceptionAsync(
                () => first.WaitAsync(TimeSpan.FromSeconds(5)));
            if (update is not null)
            {
                _ = await Record.ExceptionAsync(
                    () => update.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        var active = Assert.IsType<InvalidOperationException>(updateFailure);
        Assert.Contains("capability handler is active", active.Message);

        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var older = await Assert.ThrowsAsync<InvalidOperationException>(() => pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(table, LocalResourceCleanupMode.Optimize, 2, Generation: 1)]));

        Assert.Contains("older generation", older.Message);
        Assert.Equal(1, calls);
        Assert.Equal(1, result.ModeActionCount);
        var tableResult = Assert.Single(result.Tables);
        Assert.Equal(2, tableResult.BeforeCapacity.FreeCount);
        Assert.Equal(3, tableResult.AfterCapacity.FreeCount);
        Assert.Equal(1, tableResult.ReleasedSlotCount);
        Assert.True(tableResult.StopReasons.HasFlag(
            LocalResourceTableStopReason.ModeSuperseded));
    }

    [Fact]
    public async Task PublicModePublicationSupersedesWithMultipleSessionHandlersActive()
    {
        using var firstSession = CreateSession(1);
        using var secondSession = CreateSession(1);
        var firstTable = firstSession.RegisterTable(
            new(new(914), 1, LocalResourceDomain.Memory, 1));
        using var firstPump = new LocalResourceManagerPump(firstSession);
        using var secondPump = new LocalResourceManagerPump(secondSession);
        _ = await firstPump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            [new(firstTable, LocalResourceCleanupMode.Normal, 0, Generation: 1)]);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var ownerField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            typeof(LocalResourceManagerPump).GetField("_owner", nonPublicInstance));
        var firstOwner = ownerField.GetValue(firstPump);
        var secondOwner = ownerField.GetValue(secondPump);
        Assert.NotNull(firstOwner);
        Assert.NotNull(secondOwner);
        var enterHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "EnterCapabilityHandler",
                nonPublicInstance));
        var exitHandler = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ExitCapabilityHandler",
                nonPublicInstance));
        var wait = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "WaitForManagerOperation",
                nonPublicInstance));
        var release = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NativeLocalResourceManagerSession).GetMethod(
                "ReleaseOperation",
                nonPublicInstance));

        wait.Invoke(firstSession, [firstOwner, LocalResourceOperationKind.Maintenance]);
        wait.Invoke(secondSession, [secondOwner, LocalResourceOperationKind.Maintenance]);
        enterHandler.Invoke(firstSession, [firstOwner]);
        try
        {
            var recovery = Assert.Throws<InvalidOperationException>(
                firstSession.GetRecoveryRequiredExecutions);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                recovery.Message);
            enterHandler.Invoke(secondSession, [secondOwner]);
            try
            {
                var execution = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => firstPump.TickAsync(
                        LocalResourceCapacityStrategy.Concentrated,
                        [new(
                            firstTable,
                            LocalResourceCleanupMode.Normal,
                            0,
                            Generation: 2)]));
                Assert.Contains("capability handler is active", execution.Message);
            }
            finally
            {
                exitHandler.Invoke(secondSession, null);
            }
        }
        finally
        {
            exitHandler.Invoke(firstSession, null);
            release.Invoke(secondSession, [secondOwner]);
            release.Invoke(firstSession, [firstOwner]);
        }

        var older = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstPump.TickAsync(
                LocalResourceCapacityStrategy.Concentrated,
                [new(
                    firstTable,
                    LocalResourceCleanupMode.Normal,
                    0,
                    Generation: 1)]));
        Assert.Contains("older generation", older.Message);
    }

    [Fact]
    public async Task QueuedCapabilityReplacementRetriesAfterThePlanningOperationBoundary()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(4), 1, LocalResourceDomain.Memory, 1));
        var oldCalls = 0;
        var newCalls = 0;
        var capability = session.Capabilities.Register(
            table,
            new(3, 30, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref oldCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(
                new(1),
                100,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.ObservableNow));
        var cleanup = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var oldIntent = Assert.Single(cleanup.PlanMode(
            new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
        var replacementStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = Task.Run(() =>
        {
            replacementStarted.TrySetResult();
            return session.Capabilities.Replace(
                capability,
                new(3, 31, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
                (_, _) =>
                {
                    Interlocked.Increment(ref newCalls);
                    return ValueTask.FromResult(LocalResourceEffect.NoEffect);
                });
        });
        try
        {
            await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(replacement.IsCompleted);
            _ = await cleanup.ExecuteAsync(oldIntent);
        }
        finally
        {
            cleanup.Dispose();
        }

        var queuedError = await Record.ExceptionAsync(
            () => replacement.WaitAsync(TimeSpan.FromSeconds(5)));
        var activeHandler = Assert.IsType<InvalidOperationException>(queuedError);
        Assert.Contains("capability handler is active", activeHandler.Message);
        _ = session.Capabilities.Replace(
            capability,
            new(3, 31, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref newCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        Assert.Equal(1, oldCalls);
        Assert.Equal(0, newCalls);

        using var nextCleanup = session.BeginOperation(LocalResourceOperationKind.Cleanup);
        var newIntent = Assert.Single(nextCleanup.PlanMode(
            new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
        _ = await nextCleanup.ExecuteAsync(newIntent);
        Assert.Equal(1, newCalls);
    }

    [Fact]
    public async Task UnknownEffectReturnsOpaqueHandleAndCanBeReconciled()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(5), 1, LocalResourceDomain.Memory, 1));
        var release = session.Capabilities.Register(
            table,
            new(5, 50, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        session.RegisterResource(
            table,
            new(
                new(1),
                100,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourceExecutionResult execution;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var intent = Assert.Single(operation.PlanCapacity(
                LocalResourceCapacityStrategy.Concentrated).Intents);
            execution = await operation.ExecuteAsync(intent);
        }
        var executor = new LocalResourceIntentExecutor(session);

        Assert.True(execution.EffectUncertain);
        Assert.NotNull(execution.UncertainExecution);
        Assert.Equal(1, session.PendingExecutionCount);
        Assert.Equal(1, session.ReadCapacity(table).ActivePendingCount);
        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            session.TryClose());
        var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
            session.Dispose);
        Assert.Equal(1, closeException.PendingExecutionCount);
        Assert.Equal(1, session.PendingExecutionCount);
        var reconciled = executor.Reconcile(
            execution.UncertainExecution!,
            LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot));
        Assert.True(reconciled.ResourceSlotReleased);
        Assert.True(execution.UncertainExecution!.IsResolved);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(1, session.ReadCapacity(table).FreeCount);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, session.TryClose());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, session.TryClose());
    }

    [Fact]
    public async Task OwnedManagerClosePreservesExternallyRetainedUnknownExecution()
    {
        var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var session = manager.Session;
        var table = manager.RegisterTable(
            new(new(51), 1, LocalResourceDomain.Memory, 1));
        _ = manager.RegisterCapability(
            table,
            new(51, 510, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        manager.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.ReleaseAll,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = await manager.TickAsync();
        var uncertain = Assert.Single(tick.UncertainExecutions);

        Assert.Equal(1, manager.PendingExecutionCount);
        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            manager.TryClose());
        var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
            manager.Dispose);
        Assert.Equal(1, closeException.PendingExecutionCount);
        Assert.False(uncertain.IsResolved);
        Assert.Equal(1, session.ReadCapacity(table).ActivePendingCount);

        var reconciled = manager.Reconcile(
            uncertain,
            LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot));
        Assert.True(reconciled.ResourceSlotReleased);
        Assert.True(uncertain.IsResolved);
        Assert.Equal(0, manager.PendingExecutionCount);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Throws<ObjectDisposedException>(() => session.RegisterTable(
            new(new(52), 1, LocalResourceDomain.Memory, 1)));
    }

    [Fact]
    public async Task TickRanksCurrentWindowTouchBeforeAdvancingActivityEpoch()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var table = manager.RegisterTable(
            new(new(50), 1, LocalResourceDomain.Memory, 4));
        LocalResourceId invoked = default;
        _ = manager.RegisterCapability(
            table,
            new(50, 500, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (context, _) =>
            {
                invoked = context.ResourceUid;
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        var active = manager.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        manager.RegisterResource(
            table,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        manager.Touch(active);
        var tick = await manager.TickAsync();

        Assert.Equal(1, tick.ModeActionCount);
        Assert.Equal(new LocalResourceId(2), invoked);
    }

    [Fact]
    public async Task ReleaseAllReplansAfterEachSlotReleaseWithinBoundedBatch()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var table = manager.RegisterTable(
            new(new(52), 1, LocalResourceDomain.Memory, 4));
        var calls = 0;
        _ = manager.RegisterCapability(
            table,
            new(52, 520, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot));
            });
        for (ulong resourceId = 1; resourceId <= 3; resourceId++)
        {
            manager.RegisterResource(
                table,
                new(new(resourceId), 100, 1, LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.ReleaseAll,
            MaximumIntentsPerTick: 3,
            Generation: 1));

        var tick = await manager.TickAsync();

        Assert.Equal(3, calls);
        Assert.Equal(3, tick.ModeActionCount);
        Assert.Equal(3, Assert.Single(tick.Tables).ReleasedSlotCount);
        Assert.Equal(0, manager.Session.ReadCapacity(table).OccupiedCount);
    }

    [Fact]
    public async Task BorrowedManagerClosePreservesUnknownExecutionAndLeavesSessionOwnedByCaller()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(53), 1, LocalResourceDomain.Memory, 1));
        _ = session.Capabilities.Register(
            table,
            new(53, 530, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.ReleaseAll,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = await manager.TickAsync();
        var uncertain = Assert.Single(tick.UncertainExecutions);

        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            manager.TryClose());
        Assert.Throws<InvalidOperationException>(
            () => new DefaultLocalResourceManager(session));
        manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Equal(0, session.PendingExecutionCount);
        _ = session.RegisterTable(new(new(54), 1, LocalResourceDomain.Memory, 1));
    }

    [Fact]
    public async Task PumpClosePreservesUnknownExecutionAndBorrowedSession()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(55), 1, LocalResourceDomain.Memory, 1));
        _ = session.Capabilities.Register(
            table,
            new(55, 550, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var pump = new LocalResourceManagerPump(session);

        var tick = await pump.TickAsync(
            LocalResourceCapacityStrategy.Concentrated,
            []);
        var uncertain = Assert.Single(tick.UncertainExecutions);

        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            pump.TryClose());
        var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
            pump.Dispose);
        Assert.Equal(1, closeException.PendingExecutionCount);
        Assert.False(uncertain.IsResolved);

        pump.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.True(uncertain.IsResolved);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, pump.TryClose());
        Assert.Equal(0, session.PendingExecutionCount);
        _ = session.RegisterTable(new(new(56), 1, LocalResourceDomain.Memory, 1));
    }

    [Fact]
    public async Task HandlerFailureAfterInvocationReturnsRecoverableUncertainException()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(13), 1, LocalResourceDomain.Memory, 1));
        var trim = session.Capabilities.Register(
            table,
            new(13, 130, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => throw new InvalidOperationException("effect-not-observed"));
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.ObservableNow));
        LocalResourceEffectUncertainException exception;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            var intent = Assert.Single(operation.PlanMode(
                new(table, LocalResourceCleanupMode.Optimize, 1)).Intents);
            exception = await Assert.ThrowsAsync<LocalResourceEffectUncertainException>(
                () => operation.ExecuteAsync(intent).AsTask());
        }
        var executor = new LocalResourceIntentExecutor(session);

        Assert.Equal(1, session.ReadCapacity(table).ActivePendingCount);
        Assert.Equal(1, session.PendingExecutionCount);
        Assert.Same(
            exception.Execution,
            Assert.Single(session.GetRecoveryRequiredExecutions()));
        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            session.TryClose());
        var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
            session.Dispose);
        Assert.Equal(1, closeException.PendingExecutionCount);
        var reconciled = executor.Reconcile(exception.Execution, LocalResourceEffect.NoEffect);
        Assert.False(reconciled.EffectUncertain);
        Assert.True(exception.Execution.IsResolved);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(0, session.ReadCapacity(table).ActivePendingCount);
        Assert.Empty(session.GetRecoveryRequiredExecutions());
    }

    [Fact]
    public async Task MultipleUnknownExecutionsKeepCloseBlockedUntilEveryTokenIsResolved()
    {
        var session = CreateSession(4);
        var firstTable = session.RegisterTable(
            new(new(57), 1, LocalResourceDomain.Memory, 1));
        var secondTable = session.RegisterTable(
            new(new(59), 1, LocalResourceDomain.Memory, 1));
        var allEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandlers = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        async ValueTask<LocalResourceEffect> UnknownAfterBothEnter(
            LocalResourceExecutionContext _,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref enteredCount) == 2)
            {
                allEntered.TrySetResult();
            }
            await releaseHandlers.Task.WaitAsync(cancellationToken);
            return LocalResourceEffect.Unknown;
        }
        _ = session.Capabilities.Register(
            firstTable,
            new(57, 570, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            UnknownAfterBothEnter);
        _ = session.Capabilities.Register(
            secondTable,
            new(59, 590, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            UnknownAfterBothEnter);
        session.RegisterResource(
            firstTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var manager = new DefaultLocalResourceManager(session);
        var tick = manager.TickAsync();
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHandlers.TrySetResult();
        var tickResult = await tick.WaitAsync(TimeSpan.FromSeconds(5));
        var executions = tickResult.UncertainExecutions;
        Assert.Equal(2, executions.Count);

        Assert.Equal(2, session.PendingExecutionCount);
        var firstClose = Assert.Throws<LocalResourceRecoveryRequiredException>(
            manager.Dispose);
        Assert.Equal(2, firstClose.PendingExecutionCount);
        manager.Reconcile(executions[0], LocalResourceEffect.NoEffect);
        Assert.Equal(1, session.PendingExecutionCount);
        Assert.Equal(
            LocalResourceManagerCloseResult.RecoveryRequired,
            manager.TryClose());
        var secondClose = Assert.Throws<LocalResourceRecoveryRequiredException>(
            manager.Dispose);
        Assert.Equal(1, secondClose.PendingExecutionCount);
        manager.Reconcile(executions[1], LocalResourceEffect.NoEffect);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
        Assert.Equal(LocalResourceManagerCloseResult.Closed, session.TryClose());
    }

    [Fact]
    public async Task CloseDuringInvokedUnknownFailsFastThenReturnsRecoveryRequired()
    {
        var session = CreateSession(4);
        var table = session.RegisterTable(new(new(58), 1, LocalResourceDomain.Memory, 1));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.Capabilities.Register(
            table,
            new(58, 580, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                return LocalResourceEffect.Unknown;
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var manager = new DefaultLocalResourceManager(session);

        var tick = manager.TickAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var active = Assert.Throws<InvalidOperationException>(() => manager.TryClose());
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                active.Message);

            finish.TrySetResult();
            var tickResult = await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                LocalResourceManagerCloseResult.RecoveryRequired,
                manager.TryClose());
            var uncertain = Assert.Single(tickResult.UncertainExecutions);
            Assert.False(uncertain.IsResolved);
            Assert.Equal(1, manager.PendingExecutionCount);
            manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
            Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
            Assert.Equal(LocalResourceManagerCloseResult.Closed, session.TryClose());
        }
        finally
        {
            finish.TrySetResult();
            if (!tick.IsCompleted)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(() => ReconcileAll(manager));
                _ = Record.Exception(() => manager.TryClose());
                _ = Record.Exception(() => session.TryClose());
            }
        }
    }

    [Fact]
    public async Task CustomCapacityPolicyChangesThresholdsAndPacedBatch()
    {
        var policy = new LocalResourceCapacityPolicy(
            ConcentratedTriggerFreePercent: 30,
            ConcentratedTargetFreePercent: 40,
            SmoothTriggerFreePercent: 35,
            SmoothEmergencyFreePercent: 5,
            SmoothMaximumReleasesPerInterval: 3);
        using var session = CreateSession(16, policy);
        var table = session.RegisterTable(new(new(6), 1, LocalResourceDomain.Memory, 10));
        var release = session.Capabilities.Register(
            table,
            new(6, 60, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        for (ulong index = 1; index <= 8; index++)
        {
            session.RegisterResource(
                table,
                new(
                    new(index),
                    1,
                    checked((uint)index),
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }

        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            Assert.Equal(8, operation.PlanCapacity(
                LocalResourceCapacityStrategy.Concentrated).Intents.Count);
            Assert.Equal(3, operation.PlanCapacity(
                LocalResourceCapacityStrategy.Smooth).Intents.Count);
        }

        using var manager = new DefaultLocalResourceManager(
            session,
            LocalResourceCapacityStrategy.Smooth);
        var result = await manager.TickAsync();
        Assert.Equal(3, result.CapacityActionCount);
    }

    [Fact]
    public async Task SmoothEmergencyReportsUnrecoveredDangerAfterAllCandidatesHaveNoEffect()
    {
        using var session = CreateSession(20);
        var table = session.RegisterTable(new(new(69), 1, LocalResourceDomain.Memory, 20));
        _ = session.Capabilities.Register(
            table,
            new(69, 690, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        for (ulong index = 1; index <= 20; index++)
        {
            session.RegisterResource(
                table,
                new(
                    new(index),
                    1,
                    checked((uint)index),
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var manager = new DefaultLocalResourceManager(
            session,
            LocalResourceCapacityStrategy.Smooth);

        var result = await manager.TickAsync();

        Assert.Equal(1, result.CapacityPlan.EmergencyTableCount);
        Assert.Equal(20, result.CapacityActionCount);
        var tableResult = Assert.Single(result.Tables);
        Assert.Equal(0, tableResult.AfterCapacity.FreeCount);
        Assert.False(tableResult.CapacityGoalReached);
        Assert.True(tableResult.Stopped);
        Assert.True(tableResult.StopReasons.HasFlag(
            LocalResourceTableStopReason.CapacityGoalNotReached));
    }

    [Fact]
    public void SmoothCapacityUsesStrictFifteenPercentAndInclusiveFivePercentBoundaries()
    {
        using var session = CreateSession(20);
        var table = session.RegisterTable(new(new(70), 1, LocalResourceDomain.Memory, 20));
        _ = session.Capabilities.Register(
            table,
            new(70, 700, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        for (ulong index = 1; index <= 17; index++)
        {
            session.RegisterResource(
                table,
                new(new(index), 1, 1, LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }

        LocalResourcePlan exactlyFifteenPercentFree;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            exactlyFifteenPercentFree = operation.PlanCapacity(
                LocalResourceCapacityStrategy.Smooth);
        }
        Assert.Equal(0, exactlyFifteenPercentFree.TriggeredTableCount);
        Assert.Empty(exactlyFifteenPercentFree.Intents);

        session.RegisterResource(
            table,
            new(new(18), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourcePlan belowFifteenAboveFive;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            belowFifteenAboveFive = operation.PlanCapacity(
                LocalResourceCapacityStrategy.Smooth);
        }
        Assert.Equal(1, belowFifteenAboveFive.TriggeredTableCount);
        Assert.Equal(0, belowFifteenAboveFive.EmergencyTableCount);
        Assert.Single(belowFifteenAboveFive.Intents);

        session.RegisterResource(
            table,
            new(new(19), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourcePlan exactlyFivePercentFree;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            exactlyFivePercentFree = operation.PlanCapacity(
                LocalResourceCapacityStrategy.Smooth);
        }
        Assert.Equal(1, exactlyFivePercentFree.TriggeredTableCount);
        Assert.Equal(1, exactlyFivePercentFree.EmergencyTableCount);
        Assert.Equal(19, exactlyFivePercentFree.Intents.Count);
    }

    [Fact]
    public void ModeRankingUsesRecoveryCostCoefficientTimesCurrentSize()
    {
        using var session = CreateSession(2);
        var table = session.RegisterTable(new(new(74), 1, LocalResourceDomain.Memory, 2));
        _ = session.Capabilities.Register(
            table,
            new(74, 740, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        var lowerProductDespiteHigherCoefficient = session.RegisterResource(
            table,
            new(
                new(2),
                100,
                10,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            table,
            new(
                new(1),
                1000,
                2,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));

        LocalResourceIntent selected;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            selected = Assert.Single(operation.PlanMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                MaximumIntents: 1)).Intents);
        }

        Assert.Equal(lowerProductDespiteHigherCoefficient.ResourceUid, selected.ResourceUid);
    }

    [Fact]
    public void RegisterResourceReusesAHoleWithoutMovingLiveRows()
    {
        using var session = CreateSession(3);
        var table = session.RegisterTable(new(new(75), 1, LocalResourceDomain.Memory, 3));
        _ = session.Capabilities.Register(
            table,
            new(75, 750, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        var first = session.RegisterResource(
            table,
            new(new(1), 10, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var middle = session.RegisterResource(
            table,
            new(new(2), 20, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var last = session.RegisterResource(
            table,
            new(new(3), 30, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var firstSlot = first.Native.ResourceSlotIndex;
        var middleSlot = middle.Native.ResourceSlotIndex;
        var lastSlot = last.Native.ResourceSlotIndex;
        var middleGeneration = middle.Native.ResourceSlotGeneration;

        session.UnregisterResource(middle);
        var replacement = session.RegisterResource(
            table,
            new(new(4), 40, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));

        Assert.Equal(middleSlot, replacement.Native.ResourceSlotIndex);
        Assert.NotEqual(middleGeneration, replacement.Native.ResourceSlotGeneration);
        Assert.Equal(firstSlot, first.Native.ResourceSlotIndex);
        Assert.Equal(lastSlot, last.Native.ResourceSlotIndex);
        Assert.Equal(10UL, session.ReadResource(first).SizeBytes);
        Assert.Equal(30UL, session.ReadResource(last).SizeBytes);
    }

    [Fact]
    public async Task ConcentratedCapacityContinuesPastNoEffectUntilTwentyPercent()
    {
        using var session = CreateSession(16);
        var table = session.RegisterTable(new(new(7), 1, LocalResourceDomain.Memory, 10));
        var release = session.Capabilities.Register(
            table,
            new(7, 70, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (context, _) => ValueTask.FromResult(context.ResourceUid.Low <= 2
                ? LocalResourceEffect.NoEffect
                : LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot)));
        for (ulong index = 1; index <= 10; index++)
        {
            session.RegisterResource(
                table,
                new(
                    new(index),
                    1,
                    checked((uint)index),
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var manager = new DefaultLocalResourceManager(session);

        var result = await manager.TickAsync();

        Assert.Equal(4, result.CapacityActionCount);
        Assert.Equal(2, session.ReadCapacity(table).FreeCount);
        Assert.True(Assert.Single(result.Tables).CapacityGoalReached);
    }

    [Fact]
    public async Task ConcentratedCapacityReportsStalledAfterAllCandidatesHaveNoEffect()
    {
        using var session = CreateSession(16);
        var table = session.RegisterTable(new(new(71), 1, LocalResourceDomain.Memory, 10));
        _ = session.Capabilities.Register(
            table,
            new(71, 710, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        for (ulong index = 1; index <= 10; index++)
        {
            session.RegisterResource(
                table,
                new(
                    new(index),
                    1,
                    checked((uint)index),
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var manager = new DefaultLocalResourceManager(session);

        var result = await manager.TickAsync();

        Assert.Equal(10, result.CapacityActionCount);
        var tableResult = Assert.Single(result.Tables);
        Assert.True(tableResult.Stopped);
        Assert.False(tableResult.CapacityGoalReached);
        Assert.Contains((table.TableId, table.TableIncarnation), result.StoppedTables);
    }

    [Fact]
    public async Task CapacityNoEffectAllowsDifferentModeActionForTheSameResource()
    {
        using var session = CreateSession(2);
        var table = session.RegisterTable(new(new(72), 1, LocalResourceDomain.Memory, 1));
        var capacityCalls = 0;
        var modeCalls = 0;
        var capacityCapability = session.Capabilities.Register(
            table,
            new(72, 720, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                Interlocked.Increment(ref capacityCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        var modeCapability = session.Capabilities.Register(
            table,
            new(73, 730, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (context, _) =>
            {
                Interlocked.Increment(ref modeCalls);
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ChangesSizeBytes,
                    sizeBytesAfter: context.SizeBytes / 2,
                    releasedBytes: context.SizeBytes / 2));
            });
        var resource = session.RegisterResource(
            table,
            new(
                new(1),
                100,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        LocalResourceIntent capacityIntent;
        LocalResourceIntent modeIntent;
        using (var operation = session.BeginOperation(LocalResourceOperationKind.Cleanup))
        {
            capacityIntent = Assert.Single(operation.PlanCapacity(
                LocalResourceCapacityStrategy.Concentrated).Intents);
            modeIntent = Assert.Single(operation.PlanMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                MaximumIntents: 1)).Intents);
        }
        Assert.Equal(resource.ResourceUid, capacityIntent.ResourceUid);
        Assert.Equal(resource.ResourceUid, modeIntent.ResourceUid);
        Assert.Equal(capacityCapability.CapabilityId, capacityIntent.CapabilityId);
        Assert.Equal(modeCapability.CapabilityId, modeIntent.CapabilityId);
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var result = await manager.TickAsync();

        Assert.Equal(1, capacityCalls);
        Assert.Equal(1, modeCalls);
        Assert.Equal(1, result.CapacityActionCount);
        Assert.Equal(1, result.ModeActionCount);
        var tableResult = Assert.Single(result.Tables);
        Assert.False(tableResult.CapacityGoalReached);
        Assert.Equal(0, tableResult.ReleasedSlotCount);
        Assert.Equal(50UL, session.ReadResource(resource).SizeBytes);
    }

    [Fact]
    public async Task CapacityNoEffectDoesNotRepeatTheSameReleaseActionAsMode()
    {
        using var session = CreateSession(1);
        var table = session.RegisterTable(new(new(76), 1, LocalResourceDomain.Memory, 1));
        var calls = 0;
        _ = session.Capabilities.Register(
            table,
            new(76, 760, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.ReleaseAll,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var result = await manager.TickAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, result.CapacityActionCount);
        Assert.Equal(0, result.ModeActionCount);
    }

    [Fact]
    public async Task CapacityUnknownBlocksDifferentModeActionForTheSameResource()
    {
        using var session = CreateSession(1);
        var table = session.RegisterTable(new(new(77), 1, LocalResourceDomain.Memory, 1));
        var modeCalls = 0;
        _ = session.Capabilities.Register(
            table,
            new(77, 770, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        _ = session.Capabilities.Register(
            table,
            new(78, 780, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref modeCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        try
        {
            var result = await manager.TickAsync();

            Assert.Equal(0, modeCalls);
            Assert.Equal(1, result.CapacityActionCount);
            Assert.Equal(0, result.ModeActionCount);
            Assert.Single(result.UncertainExecutions);
            Assert.True(Assert.Single(result.Tables).StopReasons.HasFlag(
                LocalResourceTableStopReason.OperationRecoveryRequired));
        }
        finally
        {
            ReconcileAll(manager);
        }
    }

    [Fact]
    public async Task RepeatedOptimizeStateContinuesSelectingTheCurrentLowestResource()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(8), 1, LocalResourceDomain.Memory, 2));
        var selected = new List<LocalResourceId>();
        session.Capabilities.Register(
            table,
            new(8, 80, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (context, _) =>
            {
                selected.Add(context.ResourceUid);
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ChangesSizeBytes,
                    sizeBytesAfter: context.SizeBytes - 10,
                    releasedBytes: 10));
            });
        session.RegisterResource(
            table,
            new(
                new(1),
                100,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            table,
            new(
                new(2),
                100,
                10,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        var state = new LocalResourceModeState(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1);
        manager.SetDesiredMode(state);
        manager.SetDesiredMode(state);

        var first = await manager.TickAsync();
        var second = await manager.TickAsync();

        Assert.Equal([new LocalResourceId(1), new LocalResourceId(1)], selected);
        Assert.Equal(1, first.ModeActionCount);
        Assert.Equal(1, second.ModeActionCount);
    }

    [Fact]
    public async Task DesiredModeStateIsIdempotentOrderedAndReplacedOnlyByANewerGeneration()
    {
        using var session = CreateSession(1);
        var table = session.RegisterTable(new(new(8), 1, LocalResourceDomain.Memory, 1));
        var calls = 0;
        var trim = session.Capabilities.Register(
            table,
            new(8, 80, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        var optimize = new LocalResourceModeState(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 2);
        manager.SetDesiredMode(optimize);
        manager.SetDesiredMode(optimize);

        Assert.Throws<InvalidOperationException>(() => manager.SetDesiredMode(
            optimize with { Generation = 1 }));
        Assert.Throws<InvalidOperationException>(() => manager.SetDesiredMode(
            optimize with { Mode = LocalResourceCleanupMode.Normal, MaximumIntentsPerTick = 0 }));
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Normal,
            MaximumIntentsPerTick: 0,
            Generation: 3));
        var result = await manager.TickAsync();

        Assert.Equal(0, calls);
        Assert.Equal(0, result.ModeActionCount);
    }

    [Fact]
    public async Task DesiredModeRejectsForeignSessionTableBeforePublishingState()
    {
        using var ownerSession = CreateSession(1);
        using var foreignSession = CreateSession(1);
        var ownerTable = ownerSession.RegisterTable(
            new(new(806), 1, LocalResourceDomain.Memory, 1));
        var foreignTable = foreignSession.RegisterTable(
            new(new(807), 1, LocalResourceDomain.Memory, 1));
        using var manager = new DefaultLocalResourceManager(ownerSession);

        var exception = Assert.Throws<InvalidOperationException>(() => manager.SetDesiredMode(new(
            foreignTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1)));
        var result = await manager.TickAsync();

        Assert.Contains("belong to this manager session", exception.Message);
        Assert.Equal(0, result.ModeActionCount);
        manager.SetDesiredMode(new(
            ownerTable,
            LocalResourceCleanupMode.Normal,
            MaximumIntentsPerTick: 0,
            Generation: 1));
    }

    [Fact]
    public async Task DesiredModeRejectsUnregisteredTableAcrossSlotReuse()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(
                tableCapacity: 1,
                resourceCapacity: 4));

        for (var index = 0; index < 2; index++)
        {
            var stale = manager.RegisterTable(new(
                new LocalResourceId(820UL + (ulong)index),
                (ulong)index + 1,
                LocalResourceDomain.Memory,
                Capacity: 1));
            manager.UnregisterTable(stale);

            var exception = Assert.Throws<NativeLocalResourceManagerException>(() =>
                manager.SetDesiredMode(new(
                    stale,
                    LocalResourceCleanupMode.Optimize,
                    MaximumIntentsPerTick: 1,
                    Generation: (ulong)index + 1)));
            Assert.Equal(LocalResourceManagerError.StaleTable, exception.Error);
        }

        var result = await manager.TickAsync();
        Assert.Equal(0, result.ModeActionCount);
        Assert.Empty(result.Tables);
        Assert.Empty(result.StoppedTables);
    }

    [Fact]
    public async Task ActiveTickRejectsNewTickAndLaterTickBindsLatestMode()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(805), 1, LocalResourceDomain.Memory, 4));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _ = session.Capabilities.Register(
            table,
            new(805, 8050, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        session.RegisterResource(
            table,
            new(new(1), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var first = manager.TickAsync();
        var admissionChecked = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var activeError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.TickAsync());
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                activeError.Message);
            manager.SetDesiredMode(new(
                table,
                LocalResourceCleanupMode.Normal,
                MaximumIntentsPerTick: 0,
                Generation: 2));
            admissionChecked = true;
        }
        finally
        {
            release.TrySetResult();
            if (!admissionChecked)
            {
                _ = await Record.ExceptionAsync(
                    () => first.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var laterResult = await manager.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, firstResult.ModeActionCount);
        Assert.Equal(0, laterResult.ModeActionCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TickBindsModeAfterCapacityPhaseCompletes()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 5) with
        {
            MaximumConcurrentTables = 1
        };
        var session = new NativeLocalResourceManagerSession(configuration);
        var capacityTable = session.RegisterTable(
            new(new(806), 1, LocalResourceDomain.Memory, 1));
        var modeTable = session.RegisterTable(
            new(new(807), 1, LocalResourceDomain.Memory, 4));
        var capacityEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapacity = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var modeCalls = 0;
        _ = session.Capabilities.Register(
            capacityTable,
            new(806, 8060, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                capacityEntered.TrySetResult();
                await releaseCapacity.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        _ = session.Capabilities.Register(
            modeTable,
            new(807, 8070, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref modeCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            capacityTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            modeTable,
            new(new(2), 4096, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            modeTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = manager.TickAsync();
        var modePublished = false;
        try
        {
            await capacityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.SetDesiredMode(new(
                modeTable,
                LocalResourceCleanupMode.Normal,
                MaximumIntentsPerTick: 0,
                Generation: 2));
            modePublished = true;
        }
        finally
        {
            releaseCapacity.TrySetResult();
            if (!modePublished)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        var result = await tick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.CapacityActionCount);
        Assert.Equal(0, result.ModeActionCount);
        Assert.Equal(0, modeCalls);
    }

    [Fact]
    public async Task NewModeGenerationStopsRemainingActionsBeforeNativeBegin()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(new(new(808), 1, LocalResourceDomain.Memory, 4));
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _ = session.Capabilities.Register(
            table,
            new(808, 8080, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                return LocalResourceEffect.NoEffect;
            });
        for (ulong resourceId = 1; resourceId <= 2; resourceId++)
        {
            session.RegisterResource(
                table,
                new(
                    new(resourceId),
                    4096,
                    1,
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            table,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 2,
            Generation: 1));

        var tick = manager.TickAsync();
        var modePublished = false;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.SetDesiredMode(new(
                table,
                LocalResourceCleanupMode.Normal,
                MaximumIntentsPerTick: 0,
                Generation: 2));
            modePublished = true;
        }
        finally
        {
            releaseFirst.TrySetResult();
            if (!modePublished)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        var result = await tick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(1, result.ModeActionCount);
        var tableResult = Assert.Single(result.Tables);
        Assert.True(tableResult.Stopped);
        Assert.True(tableResult.StopReasons.HasFlag(
            LocalResourceTableStopReason.ModeSuperseded));
        Assert.Equal((table.TableId, table.TableIncarnation), Assert.Single(result.StoppedTables));
    }

    [Fact]
    public async Task SoftwareMemoryModeAppliesToEveryManagedMemoryTableButNotGpuTables()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(4, 16));
        var session = manager.Session;
        var firstMemory = manager.RegisterTable(
            new(new(820), 1, LocalResourceDomain.Memory, 4));
        var secondMemory = manager.RegisterTable(
            new(new(821), 1, LocalResourceDomain.Memory, 4));
        var gpu = manager.RegisterTable(
            new(
                new(822),
                1,
                LocalResourceDomain.Gpu,
                4,
                AdapterKey: new(0xA822),
                TopologyGeneration: 1));

        foreach (var table in new[] { firstMemory, secondMemory, gpu })
        {
            manager.RegisterCapability(
                table,
                new(
                    table.TableId.Low,
                    820,
                    LocalResourceEffects.ReleasesLedgerSlot,
                    Destructive: true),
                static (_, _) => ValueTask.FromResult(
                    LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot)));
            manager.RegisterResource(
                table,
                new(
                    new(table.TableId.Low),
                    4096,
                    1,
                    LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }

        manager.SetDesiredMemoryMode(new(
            LocalResourceSoftwareMemoryMode.PagedFrozen,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        var result = await manager.TickAsync();

        Assert.Equal(2, result.ModeActionCount);
        Assert.Equal(4, session.ReadCapacity(firstMemory).FreeCount);
        Assert.Equal(4, session.ReadCapacity(secondMemory).FreeCount);
        Assert.Equal(3, session.ReadCapacity(gpu).FreeCount);
        Assert.Equal(
            LocalResourceSoftwareMemoryMode.PagedFrozen,
            manager.DesiredMemoryMode!.Value.Mode);
    }

    [Fact]
    public async Task MemoryTableRegisteredAfterSoftwareModeInheritsThePersistentState()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(2, 8));
        manager.SetDesiredMemoryMode(new(
            LocalResourceSoftwareMemoryMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 7));

        var session = manager.Session;
        var table = manager.RegisterTable(
            new(new(823), 1, LocalResourceDomain.Memory, 4));
        var calls = 0;
        manager.RegisterCapability(
            table,
            new(823, 823, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        manager.RegisterResource(
            table,
            new(
                new(1),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));

        var result = await manager.TickAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, result.ModeActionCount);
    }

    [Fact]
    public async Task BorrowedManagerMemoryModeIncludesTablesExistingAtClaimTime()
    {
        using var session = CreateSession(4);
        var table = session.RegisterTable(
            new(new(824), 1, LocalResourceDomain.Memory, Capacity: 4));
        var calls = 0;
        session.Capabilities.Register(
            table,
            new(824, 8240, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            table,
            new(
                new(1),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);

        manager.SetDesiredMemoryMode(new(
            LocalResourceSoftwareMemoryMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        var result = await manager.TickAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, result.ModeActionCount);
        Assert.Equal(
            1,
            Assert.Single(result.Tables, item => item.TableId == table.TableId).ModeActionCount);
    }

    [Fact]
    public void SoftwareMemoryModeRejectsOlderAndConflictingSameGenerationState()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var state = new LocalResourceSoftwareMemoryModeState(
            LocalResourceSoftwareMemoryMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 9);

        manager.SetDesiredMemoryMode(state);
        manager.SetDesiredMemoryMode(state);

        Assert.Throws<InvalidOperationException>(() => manager.SetDesiredMemoryMode(
            state with { Generation = 8 }));
        Assert.Throws<InvalidOperationException>(() => manager.SetDesiredMemoryMode(
            state with { Mode = LocalResourceSoftwareMemoryMode.PagedFrozen }));
    }

    [Fact]
    public void UnregisterTableRemovesAllManagedCapabilityHandlers()
    {
        using var session = CreateSession(1);
        for (ulong incarnation = 1; incarnation <= 16; incarnation++)
        {
            var table = session.RegisterTable(
                new(new(81), incarnation, LocalResourceDomain.Memory, 1));
            session.Capabilities.Register(
                table,
                new(81, 810, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
                static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
            Assert.Equal(1, session.Capabilities.RegisteredCount);

            session.UnregisterTable(table);

            Assert.Equal(0, session.Capabilities.RegisteredCount);
        }
    }

    [Fact]
    public void DelayedOldTableCleanupCannotRemoveReusedSlotHandlers()
    {
        using var session = CreateSession(1);
        var oldTable = session.RegisterTable(
            new(new(82), 1, LocalResourceDomain.Memory, 1));
        session.Capabilities.Register(
            oldTable,
            new(82, 820, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        session.UnregisterTable(oldTable);
        var currentTable = session.RegisterTable(
            new(new(82), 2, LocalResourceDomain.Memory, 1));
        session.Capabilities.Register(
            currentTable,
            new(83, 830, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));

        var delayedCleanup = typeof(LocalResourceCapabilityRegistry).GetMethod(
            "RemoveTable",
            System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(delayedCleanup);
        delayedCleanup.Invoke(session.Capabilities, [oldTable]);

        Assert.Equal(1, session.Capabilities.RegisteredCount);
    }

    [Fact]
    public async Task CapabilityHandlerCannotReenterItsOwnManagerLifecycle()
    {
        using var session = CreateSession(1);
        var table = session.RegisterTable(new(new(82), 1, LocalResourceDomain.Memory, 1));
        DefaultLocalResourceManager? manager = null;
        Exception? modePublicationError = null;
        var calls = 0;
        var trim = session.Capabilities.Register(
            table,
            new(82, 820, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => manager!.TickAsync());
                Assert.Throws<InvalidOperationException>(manager!.Dispose);
                modePublicationError = Record.Exception(() => manager!.SetDesiredMode(new(
                    table,
                    LocalResourceCleanupMode.Normal,
                    MaximumIntentsPerTick: 0,
                    Generation: 2)));
                return LocalResourceEffect.NoEffect;
            });
        session.RegisterResource(
            table,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using (manager = new DefaultLocalResourceManager(session))
        {
            manager.SetDesiredMode(new(
                table,
                LocalResourceCleanupMode.Optimize,
                MaximumIntentsPerTick: 1,
                Generation: 1));
            var result = await manager.TickAsync();
            Assert.Equal(1, result.ModeActionCount);
            var publication = Assert.IsType<InvalidOperationException>(modePublicationError);
            Assert.Equal(
                "A local resource capability handler cannot publish local resource manager mode state.",
                publication.Message);
            var repeated = await manager.TickAsync();
            Assert.Equal(1, repeated.ModeActionCount);
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task CapabilityHandlerCannotEnterAnotherManagerLifecycleOrExecutor()
    {
        using var firstSession = CreateSession(1);
        using var secondSession = CreateSession(2);
        var firstTable = firstSession.RegisterTable(new(new(821), 1, LocalResourceDomain.Memory, 1));
        var secondTable = secondSession.RegisterTable(new(new(822), 1, LocalResourceDomain.Memory, 1));
        var directTable = secondSession.RegisterTable(new(new(825), 1, LocalResourceDomain.Memory, 1));
        DefaultLocalResourceManager? secondManager = null;
        LocalResourceIntentExecutor? secondExecutor = null;
        LocalResourceUncertainExecution? secondRecovery = null;
        var directIntent = default(LocalResourceIntent);
        Exception? nestedTickError = null;
        Exception? taskRunTickError = null;
        Exception? directExecuteError = null;
        Exception? directReconcileError = null;
        Exception? recoveryQueryError = null;
        Exception? disposeError = null;
        var secondHandlerInvocations = 0;
        var directHandlerInvocations = 0;

        var firstCapability = firstSession.Capabilities.Register(
            firstTable,
            new(821, 8210, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, _) =>
            {
                await Task.Yield();
                nestedTickError = await Record.ExceptionAsync(
                    () => secondManager!.TickAsync());
                taskRunTickError = await Record.ExceptionAsync(
                    () => Task.Run(() => secondManager!.TickAsync()));
                directExecuteError = await Record.ExceptionAsync(
                    async () => await secondExecutor!.ExecuteAsync(directIntent));
                directReconcileError = Record.Exception(() => secondExecutor!.Reconcile(
                    secondRecovery!,
                    LocalResourceEffect.NoEffect));
                recoveryQueryError = Record.Exception(
                    secondManager!.GetRecoveryRequiredExecutions);
                disposeError = Record.Exception(secondManager.Dispose);
                return LocalResourceEffect.NoEffect;
            });
        var secondCapability = secondSession.Capabilities.Register(
            secondTable,
            new(822, 8220, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref secondHandlerInvocations);
                return ValueTask.FromResult(LocalResourceEffect.Unknown);
            });
        secondSession.Capabilities.Register(
            directTable,
            new(825, 8250, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref directHandlerInvocations);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        firstSession.RegisterResource(
            firstTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        secondSession.RegisterResource(
            secondTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        secondSession.RegisterResource(
            directTable,
            new(new(3), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using (var operation = secondSession.BeginOperation(
            LocalResourceOperationKind.Cleanup))
        {
            directIntent = Assert.Single(operation.PlanMode(new(
                directTable,
                LocalResourceCleanupMode.Optimize,
                1)).Intents);
        }
        secondExecutor = new(secondSession);

        using var firstManager = new DefaultLocalResourceManager(firstSession);
        using (secondManager = new DefaultLocalResourceManager(secondSession))
        {
            secondManager.SetDesiredMode(new(
                secondTable,
                LocalResourceCleanupMode.Optimize,
                MaximumIntentsPerTick: 1,
                Generation: 1));
            var unknown = await secondManager.TickAsync();
            secondRecovery = Assert.Single(unknown.UncertainExecutions);
            firstManager.SetDesiredMode(new(
                firstTable,
                LocalResourceCleanupMode.Optimize,
                MaximumIntentsPerTick: 1,
                Generation: 1));

            var result = await firstManager.TickAsync();

            Assert.Equal(1, result.ModeActionCount);
            Assert.IsType<InvalidOperationException>(nestedTickError);
            Assert.IsType<InvalidOperationException>(taskRunTickError);
            var directGuard = Assert.IsType<InvalidOperationException>(directExecuteError);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                directGuard.Message);
            Assert.IsType<InvalidOperationException>(directReconcileError);
            Assert.IsType<InvalidOperationException>(recoveryQueryError);
            Assert.IsType<InvalidOperationException>(disposeError);
            Assert.Equal(1, secondHandlerInvocations);
            Assert.Equal(0, directHandlerInvocations);
            Assert.Same(
                secondRecovery,
                Assert.Single(secondManager.GetRecoveryRequiredExecutions()));

            secondManager.Reconcile(secondRecovery, LocalResourceEffect.NoEffect);
            Assert.True(secondRecovery.IsResolved);
            Assert.Empty(secondManager.GetRecoveryRequiredExecutions());
        }
    }

    [Fact]
    public async Task SuppressedFlowHandlerCannotEnterAnyOtherSessionLifecycle()
    {
        using var sourceSession = CreateSession(1);
        using var tickSession = CreateSession(1);
        using var standaloneSession = CreateSession(4);
        using var ownerSession = CreateSession(1);
        var sourceTable = sourceSession.RegisterTable(
            new(new(826), 1, LocalResourceDomain.Memory, 1));
        var tickTable = tickSession.RegisterTable(
            new(new(827), 1, LocalResourceDomain.Memory, 1));
        var recoveryTable = standaloneSession.RegisterTable(
            new(new(828), 1, LocalResourceDomain.Memory, 1));
        var directTable = standaloneSession.RegisterTable(
            new(new(829), 1, LocalResourceDomain.Memory, 1));
        var tickHandlerCalls = 0;
        var directHandlerCalls = 0;
        _ = tickSession.Capabilities.Register(
            tickTable,
            new(827, 8270, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref tickHandlerCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        _ = standaloneSession.Capabilities.Register(
            recoveryTable,
            new(828, 8280, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        _ = standaloneSession.Capabilities.Register(
            directTable,
            new(829, 8290, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref directHandlerCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        tickSession.RegisterResource(
            tickTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        standaloneSession.RegisterResource(
            recoveryTable,
            new(new(3), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        standaloneSession.RegisterResource(
            directTable,
            new(new(4), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var standaloneExecutor = new LocalResourceIntentExecutor(standaloneSession);
        LocalResourceExecutionResult recoveryResult;
        LocalResourceIntent directIntent;
        using (var operation = standaloneSession.BeginOperation(
            LocalResourceOperationKind.Cleanup))
        {
            var recoveryIntent = Assert.Single(operation.PlanMode(new(
                recoveryTable,
                LocalResourceCleanupMode.Optimize,
                1)).Intents);
            directIntent = Assert.Single(operation.PlanMode(new(
                directTable,
                LocalResourceCleanupMode.Optimize,
                1)).Intents);
            recoveryResult = await operation.ExecuteAsync(recoveryIntent);
        }
        var recovery = Assert.IsType<LocalResourceUncertainExecution>(
            recoveryResult.UncertainExecution);
        using var tickManager = new DefaultLocalResourceManager(tickSession);
        using var closeManager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 1));
        tickManager.SetDesiredMode(new(
            tickTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        LocalResourceManagerPump? unexpectedOwner = null;
        (Exception? Tick, Exception? Execute, Exception? Reconcile, Exception? Recovery,
            Exception? Close, Exception? Owner) detachedErrors = default;
        _ = sourceSession.Capabilities.Register(
            sourceTable,
            new(826, 8260, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, _) =>
            {
                Task<(Exception? Tick, Exception? Execute, Exception? Reconcile,
                    Exception? Recovery, Exception? Close, Exception? Owner)> detached;
                using (ExecutionContext.SuppressFlow())
                {
                    detached = Task.Run(async () =>
                    {
                        var tick = await Record.ExceptionAsync(() => tickManager.TickAsync());
                        var execute = await Record.ExceptionAsync(
                            () => standaloneExecutor.ExecuteAsync(directIntent).AsTask());
                        var reconcile = Record.Exception(() => standaloneExecutor.Reconcile(
                            recovery,
                            LocalResourceEffect.NoEffect));
                        var recoveryQuery = Record.Exception(
                            tickManager.GetRecoveryRequiredExecutions);
                        var close = Record.Exception(() =>
                        {
                            _ = closeManager.TryClose();
                        });
                        var owner = Record.Exception(() =>
                        {
                            unexpectedOwner = new LocalResourceManagerPump(ownerSession);
                        });
                        return (
                            (Exception?)tick,
                            (Exception?)execute,
                            (Exception?)reconcile,
                            (Exception?)recoveryQuery,
                            (Exception?)close,
                            (Exception?)owner);
                    });
                }
                detachedErrors = await detached.WaitAsync(TimeSpan.FromSeconds(5));
                return LocalResourceEffect.NoEffect;
            });
        sourceSession.RegisterResource(
            sourceTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var sourceManager = new DefaultLocalResourceManager(sourceSession);
        sourceManager.SetDesiredMode(new(
            sourceTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var sourceResult = await sourceManager.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var error in new[]
        {
            detachedErrors.Tick,
            detachedErrors.Execute,
            detachedErrors.Reconcile,
            detachedErrors.Recovery,
            detachedErrors.Close,
            detachedErrors.Owner,
        })
        {
            var active = Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                active.Message);
        }
        Assert.Null(unexpectedOwner);
        Assert.Equal(1, sourceResult.ModeActionCount);
        Assert.Equal(0, tickHandlerCalls);
        Assert.Equal(0, directHandlerCalls);
        Assert.False(recovery.IsResolved);

        var tickResult = await tickManager.TickAsync();
        Assert.Equal(1, tickResult.ModeActionCount);
        Assert.Equal(1, tickHandlerCalls);
        _ = standaloneExecutor.Reconcile(recovery, LocalResourceEffect.NoEffect);
        Assert.True(recovery.IsResolved);
        using (var operation = standaloneSession.BeginOperation(
            LocalResourceOperationKind.Cleanup))
        {
            var retryIntent = Assert.Single(operation.PlanMode(new(
                directTable,
                LocalResourceCleanupMode.Optimize,
                1)).Intents);
            _ = await operation.ExecuteAsync(retryIntent);
        }
        Assert.Equal(1, directHandlerCalls);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, closeManager.TryClose());
        using var owner = new LocalResourceManagerPump(ownerSession);
    }

    [Fact]
    public async Task LateIndependentRootFailsFastAndRetriesAfterAnotherHandlerExits()
    {
        using var firstSession = CreateSession(1);
        using var secondSession = CreateSession(1);
        var firstTable = firstSession.RegisterTable(new(new(823), 1, LocalResourceDomain.Memory, 1));
        var secondTable = secondSession.RegisterTable(new(new(824), 1, LocalResourceDomain.Memory, 1));
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalls = 0;

        firstSession.Capabilities.Register(
            firstTable,
            new(823, 8230, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        secondSession.Capabilities.Register(
            secondTable,
            new(824, 8240, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref secondCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        firstSession.RegisterResource(
            firstTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        secondSession.RegisterResource(
            secondTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var firstManager = new DefaultLocalResourceManager(firstSession);
        using var secondManager = new DefaultLocalResourceManager(secondSession);
        firstManager.SetDesiredMode(new(
            firstTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        secondManager.SetDesiredMode(new(
            secondTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var firstTick = firstManager.TickAsync();
        var admissionChecked = false;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var late = await Assert.ThrowsAsync<InvalidOperationException>(
                () => secondManager.TickAsync());
            Assert.Equal(
                "A local resource capability handler cannot enter any local resource manager lifecycle.",
                late.Message);
            Assert.Equal(0, secondCalls);
            admissionChecked = true;
        }
        finally
        {
            releaseFirst.TrySetResult();
            if (!admissionChecked)
            {
                _ = await Record.ExceptionAsync(
                    () => firstTick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        var firstResult = await firstTick.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await secondManager.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, firstResult.ModeActionCount);
        Assert.Equal(1, retry.ModeActionCount);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task ClaimedSessionRejectsRawCapabilityMutationAndOwnerFacadeAppliesNextTick()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 2) with
        {
            MaximumConcurrentTables = 1
        };
        using var session = new NativeLocalResourceManagerSession(configuration);
        var firstTable = session.RegisterTable(new(new(83), 1, LocalResourceDomain.Memory, 1));
        var secondTable = session.RegisterTable(new(new(84), 1, LocalResourceDomain.Memory, 1));
        using var firstEntered = new ManualResetEventSlim();
        using var allowFirst = new ManualResetEventSlim();
        var firstRelease = session.Capabilities.Register(
            firstTable,
            new(83, 830, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                firstEntered.Set();
                Assert.True(allowFirst.Wait(TimeSpan.FromSeconds(10)));
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        var staleRelease = session.Capabilities.Register(
            secondTable,
            new(84, 840, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        session.RegisterResource(
            firstTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        var refreshedCalls = 0;
        manager.SetDesiredMode(new(
            secondTable,
            LocalResourceCleanupMode.ReleaseAll,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = Task.Run(() => manager.TickAsync());
        InvalidOperationException? rawMutation = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            rawMutation = Assert.Throws<InvalidOperationException>(() =>
                session.Capabilities.Replace(
                    staleRelease,
                    new(84, 841, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
                    static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect)));
        }
        finally
        {
            allowFirst.Set();
            _ = await Record.ExceptionAsync(
                () => tick.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        _ = await tick.WaitAsync(TimeSpan.FromSeconds(10));
        _ = manager.ReplaceCapability(
            staleRelease,
            new(84, 841, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                Interlocked.Increment(ref refreshedCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        _ = await manager.TickAsync();

        Assert.Equal(
            "A local resource capability handler cannot enter any local resource manager lifecycle.",
            Assert.IsType<InvalidOperationException>(rawMutation).Message);
        Assert.Equal(1, refreshedCalls);
    }

    [Fact]
    public async Task DangerousTablesRunConcurrentlyAndActiveTickRejectsNewTick()
    {
        using var session = CreateSession(4);
        var firstTable = session.RegisterTable(new(new(9), 1, LocalResourceDomain.Memory, 1));
        var secondTable = session.RegisterTable(new(new(10), 1, LocalResourceDomain.Memory, 1));
        var active = 0;
        var maximumActive = 0;
        async ValueTask<LocalResourceEffect> Release(
            LocalResourceExecutionContext _,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maximumActive)))
            {
                if (Interlocked.CompareExchange(ref maximumActive, current, observed) == observed) break;
            }
            await Task.Delay(40, cancellationToken);
            Interlocked.Decrement(ref active);
            return LocalResourceEffect.NoEffect;
        }
        var firstRelease = session.Capabilities.Register(
            firstTable,
            new(9, 90, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            Release);
        var secondRelease = session.Capabilities.Register(
            secondTable,
            new(10, 100, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            Release);
        session.RegisterResource(
            firstTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);

        var firstTick = manager.TickAsync();
        var activeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TickAsync());
        Assert.True(
            activeError.Message ==
                "A local resource capability handler cannot enter any local resource manager lifecycle." ||
            activeError.Message.Contains(
                "capability handler is active",
                StringComparison.Ordinal),
            activeError.Message);
        var firstResult = await firstTick;
        var secondResult = await manager.TickAsync();

        Assert.Equal(2, maximumActive);
        Assert.Equal(2, firstResult.CapacityActionCount);
        Assert.Equal(2, secondResult.CapacityActionCount);
        Assert.NotEqual(0UL, firstResult.CapacityPlan.OperationId);
        Assert.All(firstResult.CapacityPlan.Intents, intent =>
        {
            Assert.Equal(firstResult.CapacityPlan.OperationId, intent.OperationId);
            Assert.Equal(
                firstResult.CapacityPlan.OperationGeneration,
                intent.OperationGeneration);
        });
        Assert.True(
            secondResult.CapacityPlan.OperationId >
                firstResult.CapacityPlan.OperationId);
        Assert.True(
            secondResult.CapacityPlan.OperationGeneration >
                firstResult.CapacityPlan.OperationGeneration);
    }

    [Fact]
    public async Task PendingCapacityStopsSiblingWithoutLosingUncertainExecution()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 2) with
        {
            PendingCapacity = 1,
            MaximumConcurrentTables = 2
        };
        var session = new NativeLocalResourceManagerSession(configuration);
        var firstTable = session.RegisterTable(new(new(21), 1, LocalResourceDomain.Memory, 1));
        var secondTable = session.RegisterTable(new(new(22), 1, LocalResourceDomain.Memory, 1));
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCalls = 0;
        async ValueTask<LocalResourceEffect> Release(
            LocalResourceExecutionContext _,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref handlerCalls);
            handlerEntered.TrySetResult();
            await finishHandler.Task.WaitAsync(cancellationToken);
            return LocalResourceEffect.Unknown;
        }
        _ = session.Capabilities.Register(
            firstTable,
            new(21, 210, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            Release);
        _ = session.Capabilities.Register(
            secondTable,
            new(22, 220, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            Release);
        session.RegisterResource(
            firstTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var manager = new DefaultLocalResourceManager(session);

        var tick = manager.TickAsync();
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finishHandler.TrySetResult();
            var result = await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, handlerCalls);
            Assert.Equal(1, result.CapacityActionCount);
            Assert.Equal(2, result.Tables.Count);
            var uncertain = Assert.Single(result.UncertainExecutions);
            Assert.Single(result.Tables, static table => table.EffectUncertain);
            var pendingStopped = Assert.Single(
                result.Tables,
                static table => table.Stopped && !table.EffectUncertain);
            Assert.True(pendingStopped.StopReasons.HasFlag(
                LocalResourceTableStopReason.PendingCapacityExhausted));
            Assert.False(pendingStopped.StopReasons.HasFlag(
                LocalResourceTableStopReason.ResourceBusy));
            Assert.Equal(0, pendingStopped.CapacityActionCount);
            Assert.Equal(0, pendingStopped.ModeActionCount);
            Assert.Equal(0, pendingStopped.ReleasedSlotCount);
            Assert.Equal(1, session.PendingExecutionCount);
            Assert.Same(uncertain, Assert.Single(manager.GetRecoveryRequiredExecutions()));

            var recoveryError = await Assert.ThrowsAsync<NativeLocalResourceManagerException>(
                () => manager.TickAsync());
            Assert.Equal(LocalResourceManagerError.RecoveryRequired, recoveryError.Error);
            Assert.Same(uncertain, Assert.Single(manager.GetRecoveryRequiredExecutions()));

            manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
            Assert.True(uncertain.IsResolved);
            Assert.Equal(0, session.PendingExecutionCount);
            Assert.Empty(manager.GetRecoveryRequiredExecutions());
        }
        finally
        {
            finishHandler.TrySetResult();
            if (!tick.IsCompleted)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(() => ReconcileAll(manager));
                _ = Record.Exception(() => manager.TryClose());
                _ = Record.Exception(() => session.TryClose());
            }
        }
    }

    [Fact]
    public async Task PendingCapacityStopsModeSiblingWithTypedReasonAndRecoverableAuthority()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 8) with
        {
            PendingCapacity = 1,
            MaximumConcurrentTables = 2
        };
        var session = new NativeLocalResourceManagerSession(configuration);
        var firstTable = session.RegisterTable(new(new(41), 1, LocalResourceDomain.Memory, 4));
        var secondTable = session.RegisterTable(new(new(42), 1, LocalResourceDomain.Memory, 4));
        var firstCalls = 0;
        var secondCalls = 0;
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.Capabilities.Register(
            firstTable,
            new(41, 410, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref firstCalls);
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.Unknown;
            });
        _ = session.Capabilities.Register(
            secondTable,
            new(42, 420, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref secondCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            firstTable,
            new(new(1), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            firstTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));
        manager.SetDesiredMode(new(
            secondTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var tick = manager.TickAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirst.TrySetResult();
        var result = await tick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
        Assert.Equal(0, result.CapacityActionCount);
        Assert.Equal(1, result.ModeActionCount);
        var uncertain = Assert.Single(result.UncertainExecutions);
        Assert.True(Assert.Single(result.Tables, table =>
            table.TableId == firstTable.TableId).StopReasons.HasFlag(
                LocalResourceTableStopReason.EffectUncertain));
        var pendingStopped = Assert.Single(result.Tables, table =>
            table.TableId == secondTable.TableId);
        Assert.True(pendingStopped.StopReasons.HasFlag(
            LocalResourceTableStopReason.PendingCapacityExhausted));
        Assert.Equal(0, pendingStopped.CapacityActionCount);
        Assert.Equal(0, pendingStopped.ModeActionCount);
        Assert.Equal(0, pendingStopped.ReleasedSlotCount);
        Assert.Same(uncertain, Assert.Single(manager.GetRecoveryRequiredExecutions()));

        manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.Empty(manager.GetRecoveryRequiredExecutions());
    }

    [Fact]
    public async Task FatalSiblingPublishesPartialResultAfterEveryTableSettles()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 3,
            resourceCapacity: 3) with
        {
            PendingCapacity = 3,
            MaximumConcurrentTables = 3
        };
        using var session = new NativeLocalResourceManagerSession(configuration);
        var unknownTable = session.RegisterTable(new(new(50), 1, LocalResourceDomain.Memory, 1));
        var failingTable = session.RegisterTable(new(new(51), 1, LocalResourceDomain.Memory, 1));
        var siblingTable = session.RegisterTable(new(new(52), 1, LocalResourceDomain.Memory, 1));
        var allHandlersEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandlers = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var siblingCompleted = 0;
        _ = session.Capabilities.Register(
            unknownTable,
            new(50, 500, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref enteredCount) == 3)
                {
                    allHandlersEntered.TrySetResult();
                }
                await releaseHandlers.Task.WaitAsync(cancellationToken);
                return LocalResourceEffect.Unknown;
            });
        _ = session.Capabilities.Register(
            failingTable,
            new(51, 510, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref enteredCount) == 3)
                {
                    allHandlersEntered.TrySetResult();
                }
                await releaseHandlers.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("handler-failed-after-invocation");
            });
        _ = session.Capabilities.Register(
            siblingTable,
            new(52, 520, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref enteredCount) == 3)
                {
                    allHandlersEntered.TrySetResult();
                }
                await releaseHandlers.Task.WaitAsync(cancellationToken);
                Interlocked.Exchange(ref siblingCompleted, 1);
                return LocalResourceEffect.NoEffect;
            });
        session.RegisterResource(
            unknownTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            failingTable,
            new(new(2), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            siblingTable,
            new(new(3), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);

        var tick = manager.TickAsync();
        await allHandlersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHandlers.TrySetResult();
        var exception = await Assert.ThrowsAsync<LocalResourceManagerTickFailedException>(
            () => tick);

        Assert.Equal(1, Volatile.Read(ref siblingCompleted));
        Assert.Equal(3, exception.PartialResult.CapacityActionCount);
        Assert.Equal(0, exception.PartialResult.ModeActionCount);
        Assert.Equal(3, exception.PartialResult.Tables.Count);
        var failedTableResult = Assert.Single(
            exception.PartialResult.Tables,
            table => table.TableId == failingTable.TableId);
        Assert.Equal(1, failedTableResult.CapacityActionCount);
        Assert.True(failedTableResult.EffectUncertain);
        Assert.True(failedTableResult.StopReasons.HasFlag(
            LocalResourceTableStopReason.EffectUncertain));
        Assert.Equal(1, session.ReadCapacity(failingTable).ActivePendingCount);
        Assert.Equal(1, failedTableResult.AfterCapacity.ActivePendingCount);
        var failure = Assert.Single(exception.Failures);
        Assert.Equal(failingTable.TableId, failure.TableId);
        Assert.Equal(LocalResourceManagerTickPhase.Capacity, failure.Phase);
        Assert.IsType<LocalResourceEffectUncertainException>(failure.Cause);
        var uncertain = exception.PartialResult.UncertainExecutions;
        Assert.Equal(2, uncertain.Count);
        Assert.Equal(
            [unknownTable.TableId, failingTable.TableId],
            uncertain.Select(static execution => execution.Context.TableId)
                .OrderBy(static tableId => tableId.Low)
                .ToArray());
        var recoverable = manager.GetRecoveryRequiredExecutions();
        Assert.Equal(2, recoverable.Count);
        Assert.All(uncertain, execution => Assert.Contains(execution, recoverable));
        Assert.Equal(LocalResourceManagerCloseResult.RecoveryRequired, manager.TryClose());

        manager.Reconcile(uncertain[0], LocalResourceEffect.NoEffect);
        Assert.Equal(1, session.PendingExecutionCount);
        Assert.Equal(LocalResourceManagerCloseResult.RecoveryRequired, manager.TryClose());
        manager.Reconcile(uncertain[1], LocalResourceEffect.NoEffect);
        Assert.Equal(0, session.PendingExecutionCount);
        Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
    }

    [Fact]
    public async Task CapacityUncertaintyStopsIndependentModeWithinTheSameOperation()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 5) with
        {
            PendingCapacity = 2,
            MaximumConcurrentTables = 2
        };
        using var session = new NativeLocalResourceManagerSession(configuration);
        var failingTable = session.RegisterTable(new(new(53), 1, LocalResourceDomain.Memory, 1));
        var healthyTable = session.RegisterTable(new(new(54), 1, LocalResourceDomain.Memory, 4));
        _ = session.Capabilities.Register(
            failingTable,
            new(53, 530, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => throw new InvalidOperationException("capacity-handler-failed"));
        var healthyModeCalls = 0;
        _ = session.Capabilities.Register(
            healthyTable,
            new(54, 540, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            (_, _) =>
            {
                Interlocked.Increment(ref healthyModeCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            failingTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            healthyTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            healthyTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        try
        {
            var exception = await Assert.ThrowsAsync<LocalResourceManagerTickFailedException>(
                () => manager.TickAsync());

            Assert.Equal(0, healthyModeCalls);
            Assert.Equal(1, exception.PartialResult.CapacityActionCount);
            Assert.Equal(0, exception.PartialResult.ModeActionCount);
            var healthyResult = Assert.Single(
                exception.PartialResult.Tables,
                table => table.TableId == healthyTable.TableId);
            Assert.Equal(0, healthyResult.ModeActionCount);
            Assert.True(healthyResult.StopReasons.HasFlag(
                LocalResourceTableStopReason.OperationRecoveryRequired));
            var failure = Assert.Single(exception.Failures);
            Assert.Equal(failingTable.TableId, failure.TableId);
            Assert.Equal(LocalResourceManagerTickPhase.Capacity, failure.Phase);
        }
        finally
        {
            ReconcileAll(manager);
        }
    }

    [Fact]
    public async Task CapacityRecoveryStopsLaterModePhaseAndPreservesAuthority()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 5) with
        {
            PendingCapacity = 2,
            MaximumConcurrentTables = 2
        };
        using var session = new NativeLocalResourceManagerSession(configuration);
        var capacityTable = session.RegisterTable(new(new(60), 1, LocalResourceDomain.Memory, 1));
        var modeTable = session.RegisterTable(new(new(61), 1, LocalResourceDomain.Memory, 4));
        _ = session.Capabilities.Register(
            capacityTable,
            new(60, 600, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        _ = session.Capabilities.Register(
            modeTable,
            new(61, 610, LocalResourceEffects.ChangesSizeBytes, Destructive: false),
            static (_, _) => throw new InvalidOperationException("mode-handler-failed"));
        session.RegisterResource(
            capacityTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            modeTable,
            new(new(2), 100, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        using var manager = new DefaultLocalResourceManager(session);
        manager.SetDesiredMode(new(
            modeTable,
            LocalResourceCleanupMode.Optimize,
            MaximumIntentsPerTick: 1,
            Generation: 1));

        var result = await manager.TickAsync();

        Assert.Equal(1, result.CapacityActionCount);
        Assert.Equal(0, result.ModeActionCount);
        var stoppedModeTableResult = Assert.Single(
            result.Tables,
            table => table.TableId == modeTable.TableId);
        Assert.True(stoppedModeTableResult.StopReasons.HasFlag(
            LocalResourceTableStopReason.OperationRecoveryRequired));
        Assert.Equal(0, session.ReadCapacity(modeTable).ActivePendingCount);
        Assert.Equal(0, stoppedModeTableResult.AfterCapacity.ActivePendingCount);
        var uncertain = Assert.Single(result.UncertainExecutions);
        Assert.Equal(capacityTable.TableId, uncertain.Context.TableId);
        Assert.Same(uncertain, Assert.Single(manager.GetRecoveryRequiredExecutions()));

        manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
        Assert.Empty(manager.GetRecoveryRequiredExecutions());
    }

    [Fact]
    public async Task CancellationAfterInvocationReturnsPartialResultAndUncertainHandle()
    {
        var configuration = LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 2,
            resourceCapacity: 2) with
        {
            MaximumConcurrentTables = 1
        };
        var session = new NativeLocalResourceManagerSession(configuration);
        var firstTable = session.RegisterTable(new(new(31), 1, LocalResourceDomain.Memory, 1));
        var secondTable = session.RegisterTable(new(new(32), 1, LocalResourceDomain.Memory, 1));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = session.Capabilities.Register(
            firstTable,
            new(31, 310, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return LocalResourceEffect.NoEffect;
            });
        var secondCalls = 0;
        var secondRelease = session.Capabilities.Register(
            secondTable,
            new(32, 320, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            (_, _) =>
            {
                Interlocked.Increment(ref secondCalls);
                return ValueTask.FromResult(LocalResourceEffect.NoEffect);
            });
        session.RegisterResource(
            firstTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        session.RegisterResource(
            secondTable,
            new(new(1), 1, 1, LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        var manager = new DefaultLocalResourceManager(session);
        using var cancellation = new CancellationTokenSource();

        var tick = manager.TickAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.CancellationObserved);
            Assert.Equal(1, result.CapacityActionCount);
            Assert.Equal(0, secondCalls);
            var uncertain = Assert.Single(result.UncertainExecutions);
            Assert.False(uncertain.IsResolved);
            Assert.Equal(
                LocalResourceManagerCloseResult.RecoveryRequired,
                manager.TryClose());
            var closeException = Assert.Throws<LocalResourceRecoveryRequiredException>(
                manager.Dispose);
            Assert.Equal(1, closeException.PendingExecutionCount);
            Assert.False(uncertain.IsResolved);
            manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
            Assert.True(uncertain.IsResolved);
            Assert.Equal(LocalResourceManagerCloseResult.Closed, manager.TryClose());
            using var operation = session.BeginOperation(
                LocalResourceOperationKind.Cleanup);
            Assert.Equal(2, operation.PlanCapacity(
                LocalResourceCapacityStrategy.Concentrated).Intents.Count);
        }
        finally
        {
            cancellation.Cancel();
            if (!tick.IsCompleted)
            {
                _ = await Record.ExceptionAsync(
                    () => tick.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            if (tick.IsCompleted)
            {
                _ = Record.Exception(() => ReconcileAll(manager));
                _ = Record.Exception(() => manager.TryClose());
                _ = Record.Exception(() => session.TryClose());
            }
        }
    }

    [Fact]
    public async Task StaleModeRequestCannotBlockCapacityRecovery()
    {
        using var session = CreateSession(16);
        var danger = session.RegisterTable(new(new(11), 1, LocalResourceDomain.Memory, 10));
        var stale = session.RegisterTable(new(new(12), 1, LocalResourceDomain.Memory, 1));
        var release = session.Capabilities.Register(
            danger,
            new(11, 110, LocalResourceEffects.ReleasesLedgerSlot, Destructive: true),
            static (_, _) => ValueTask.FromResult(
                LocalResourceEffect.Applied(LocalResourceEffects.ReleasesLedgerSlot)));
        for (ulong index = 1; index <= 10; index++)
        {
            session.RegisterResource(
                danger,
                new(new(index), 1, 1, LocalResourceRecoverability.Recoverable,
                    LocalResourceAccessLossImpact.UnobservableNow));
        }
        session.UnregisterTable(stale);
        using var manager = new DefaultLocalResourceManager(session);
        var rejected = Assert.Throws<NativeLocalResourceManagerException>(() =>
            manager.SetDesiredMode(new(
                stale,
                LocalResourceCleanupMode.Optimize,
                MaximumIntentsPerTick: 1,
                Generation: 1)));
        Assert.Equal(LocalResourceManagerError.StaleTable, rejected.Error);

        var result = await manager.TickAsync();

        Assert.Equal(2, session.ReadCapacity(danger).FreeCount);
        Assert.Equal(2, result.CapacityActionCount);
        Assert.DoesNotContain(
            result.StoppedTables,
            identity => identity.TableId == stale.TableId);
    }

    private static void ReconcileAll(DefaultLocalResourceManager manager)
    {
        foreach (var execution in manager.GetRecoveryRequiredExecutions())
        {
            if (!execution.IsResolved)
            {
                manager.Reconcile(execution, LocalResourceEffect.NoEffect);
            }
        }
    }

    [Fact]
    public async Task PredeclaredPartitionReservationPermanentlyExcludesDirectResources()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 6));
        var table = manager.RegisterTable(
            PartitionTable(5010, capacity: 6, reservationStart: 2, reservationCapacity: 3));
        RegisterPartitionReleaseCapability(manager, table);

        manager.RegisterResource(table, PartitionResource(50100));
        manager.RegisterResource(table, PartitionResource(50101));
        manager.RegisterResource(table, PartitionResource(50102));
        var full = Assert.Throws<NativeLocalResourceManagerException>(
            () => manager.RegisterResource(table, PartitionResource(50103)));
        Assert.Equal(LocalResourceManagerError.TableFull, full.Error);

        var before = manager.Session.ReadCapacity(table);
        Assert.Equal(6, before.Capacity);
        Assert.Equal(3, before.DirectCapacity);
        Assert.Equal(3, before.DirectOccupiedCount);
        Assert.Equal(0, before.DirectFreeCount);
        Assert.Equal(2, before.PartitionReservationStart);
        Assert.Equal(3, before.PartitionReservationCapacity);
        Assert.Equal(3, before.PartitionFreeCount);

        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 3)).Partition);
        Assert.Equal(2, manager.ReadPartition(shell).LocalStart);
        var partitionResource = manager.RegisterResource(
            shell,
            1,
            PartitionResource(50104));
        var occupied = manager.Session.ReadCapacity(table);
        Assert.Equal(4, occupied.OccupiedCount);
        Assert.Equal(3, occupied.DirectOccupiedCount);
        Assert.Equal(1, occupied.PartitionOccupiedCount);

        manager.UnregisterResource(partitionResource);
        Assert.Equal(
            LocalResourcePartitionCloseStatus.Closed,
            (await manager.ClosePartitionAsync(shell)).Status);
        var stillFull = Assert.Throws<NativeLocalResourceManagerException>(
            () => manager.RegisterResource(table, PartitionResource(50105)));
        Assert.Equal(LocalResourceManagerError.TableFull, stillFull.Error);
    }

    [Fact]
    public async Task RootPartitionWithoutReservationIsBlocked()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(
            new(new(5011), 1, LocalResourceDomain.Memory, 2));
        RegisterPartitionReleaseCapability(manager, table);

        var result = await manager.AdmitPartitionAsync(table, 1);

        Assert.Equal(LocalResourcePartitionAdmissionStatus.Blocked, result.Status);
        Assert.Null(result.Partition);
        Assert.Equal(2, manager.Session.ReadCapacity(table).DirectCapacity);
    }

    [Fact]
    public void InvalidPartitionReservationIsRejectedBeforeNativeMutation()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterTable(
            PartitionTable(5012, capacity: 4, reservationStart: 3, reservationCapacity: 2)));
    }

    [Fact]
    public async Task PartitionReopenByHandleReturnsOrderedSurvivorsAndHoles()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 8));
        var table = manager.RegisterTable(PartitionTable(5001, 4));
        RegisterPartitionReleaseCapability(manager, table);
        var admission = await manager.AdmitPartitionAsync(table, 4);
        Assert.Equal(LocalResourcePartitionAdmissionStatus.Admitted, admission.Status);
        var shell = Assert.IsType<LocalResourcePartitionHandle>(admission.Partition);
        var first = manager.RegisterResource(
            shell,
            0,
            PartitionResource(50010));
        var last = manager.RegisterResource(
            shell,
            3,
            PartitionResource(50013));

        Assert.Equal(first.ResourceUid, manager.ReadPartitionCell(shell, 0).Resource!.Value.ResourceUid);
        Assert.Equal(LocalResourcePartitionCellKind.Empty, manager.ReadPartitionCell(shell, 1).Kind);
        Assert.Equal(last.ResourceUid, manager.ReadPartitionCell(shell, 3).Resource!.Value.ResourceUid);

        manager.UnregisterResource(first);
        var reopened = manager.ReadPartition(shell);
        Assert.Equal(4, reopened.Capacity);
        Assert.Equal(1, reopened.DescendantResourceCount);
        Assert.Equal(LocalResourcePartitionCellKind.Empty, manager.ReadPartitionCell(shell, 0).Kind);
        Assert.Equal(last.ResourceUid, manager.ReadPartitionCell(shell, 3).Resource!.Value.ResourceUid);

        var filled = await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50014));
        Assert.Equal(LocalResourcePartitionAdmissionStatus.Admitted, filled.Status);
        Assert.Equal(0, filled.Placement!.Value.LocalOrdinal);
        Assert.Equal(0, filled.InvokedActionCount);
        Assert.Equal(last.ResourceUid, manager.ReadPartitionCell(shell, 3).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionWholeReplacementUsesLowestActivityAndStalesOldHandle()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 6));
        var table = manager.RegisterTable(PartitionTable(5002, 6));
        RegisterPartitionReleaseCapability(manager, table);
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 6)).Partition);
        var firstChild = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitChildPartitionAsync(root, 3)).Partition);
        var secondChild = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitChildPartitionAsync(root, 3)).Partition);
        var firstResource = manager.RegisterResource(firstChild, 0, PartitionResource(50020));
        var secondResource = manager.RegisterResource(secondChild, 0, PartitionResource(50021));
        manager.Touch(firstResource, 10);
        manager.Touch(secondResource, 20);

        var replacement = await manager.AdmitChildPartitionAsync(root, 3);

        Assert.Equal(LocalResourcePartitionAdmissionStatus.Admitted, replacement.Status);
        Assert.Equal(1, replacement.InvokedActionCount);
        Assert.Equal(1, replacement.ReleasedResourceCount);
        var replacementHandle = Assert.IsType<LocalResourcePartitionHandle>(replacement.Partition);
        Assert.Equal(firstChild.SlotIndex, replacementHandle.SlotIndex);
        Assert.NotEqual(firstChild.Generation, replacementHandle.Generation);
        var stale = Assert.Throws<NativeLocalResourceManagerException>(
            () => manager.ReadPartition(firstChild));
        Assert.Equal(LocalResourceManagerError.StalePartition, stale.Error);
        Assert.Equal(
            secondResource.ResourceUid,
            manager.ReadPartitionCell(secondChild, 0).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionResourceAdmissionFillsThenRotatesLowestDirectActivity()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var table = manager.RegisterTable(PartitionTable(5003, 2));
        RegisterPartitionReleaseCapability(manager, table);
        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var first = Assert.IsType<LocalResourcePartitionResourcePlacement>(
            (await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50030))).Placement);
        var second = Assert.IsType<LocalResourcePartitionResourcePlacement>(
            (await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50031))).Placement);
        Assert.Equal(0, first.LocalOrdinal);
        Assert.Equal(1, second.LocalOrdinal);
        manager.Touch(first.Resource, 10);
        manager.Touch(second.Resource, 20);

        var rotated = await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50032));

        Assert.Equal(LocalResourcePartitionAdmissionStatus.Admitted, rotated.Status);
        Assert.Equal(1, rotated.InvokedActionCount);
        Assert.Equal(1, rotated.ReleasedResourceCount);
        Assert.Equal(0, rotated.Placement!.Value.LocalOrdinal);
        Assert.Equal(
            second.Resource.ResourceUid,
            manager.ReadPartitionCell(shell, 1).Resource!.Value.ResourceUid);

        var firstLease = manager.BeginUse(rotated.Placement.Value.Resource);
        var secondLease = manager.BeginUse(second.Resource);
        try
        {
            var blocked = await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50033));
            Assert.Equal(LocalResourcePartitionAdmissionStatus.Blocked, blocked.Status);
            Assert.Null(blocked.Placement);
        }
        finally
        {
            manager.EndUse(secondLease);
            manager.EndUse(firstLease);
        }
    }

    [Fact]
    public async Task PartitionResourceAdmissionValidatesDefinitionBeforeRelease()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5007, 1));
        var releaseCalls = 0;
        manager.RegisterCapability(
            table,
            new(
                5007,
                5007,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 1)).Partition);
        var existing = manager.RegisterResource(shell, 0, PartitionResource(50070));
        var invalid = PartitionResource(0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => manager.AdmitPartitionResourceAsync(shell, invalid));

        Assert.Equal(0, releaseCalls);
        Assert.Equal(
            existing.ResourceUid,
            manager.ReadPartitionCell(shell, 0).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionResourceAdmissionRejectsDuplicateNonVictimBeforeRelease()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 3));
        var table = manager.RegisterTable(PartitionTable(5008, 2));
        var releaseCalls = 0;
        manager.RegisterCapability(
            table,
            new(
                5008,
                5008,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var cold = manager.RegisterResource(shell, 0, PartitionResource(50080));
        var hot = manager.RegisterResource(shell, 1, PartitionResource(50081));
        manager.Touch(hot, 100);

        var exception = await Assert.ThrowsAsync<NativeLocalResourceManagerException>(
            () => manager.AdmitPartitionResourceAsync(shell, PartitionResource(50081)));

        Assert.Equal(LocalResourceManagerError.DuplicateResource, exception.Error);
        Assert.Equal(0, releaseCalls);
        Assert.Equal(
            cold.ResourceUid,
            manager.ReadPartitionCell(shell, 0).Resource!.Value.ResourceUid);
        Assert.Equal(
            hot.ResourceUid,
            manager.ReadPartitionCell(shell, 1).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionAdmissionCancellationPreservesConfirmedPartialRelease()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 8));
        var table = manager.RegisterTable(PartitionTable(5009, 4));
        using var cancellation = new CancellationTokenSource();
        var releaseCalls = 0;
        manager.RegisterCapability(
            table,
            new(
                5009,
                5009,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                if (releaseCalls == 1) cancellation.Cancel();
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 4)).Partition);
        var children = new LocalResourcePartitionHandle[4];
        var resources = new LocalResourceHandle[4];
        for (var index = 0; index < children.Length; index++)
        {
            children[index] = Assert.IsType<LocalResourcePartitionHandle>(
                (await manager.AdmitChildPartitionAsync(root, 1)).Partition);
            resources[index] = manager.RegisterResource(
                children[index],
                0,
                PartitionResource(50090UL + checked((ulong)index)));
            manager.Touch(resources[index], checked((uint)(index + 1)));
        }

        var result = await manager.AdmitChildPartitionAsync(
            root,
            2,
            cancellation.Token);

        Assert.Equal(LocalResourcePartitionAdmissionStatus.Canceled, result.Status);
        Assert.Null(result.Partition);
        Assert.Equal(1, result.InvokedActionCount);
        Assert.Equal(1, result.ReleasedResourceCount);
        Assert.Equal(1, releaseCalls);
        Assert.Equal(4, manager.ReadPartition(root).DirectChildCount);
        Assert.Equal(
            LocalResourcePartitionCellKind.Empty,
            manager.ReadPartitionCell(children[0], 0).Kind);
        Assert.Equal(
            resources[1].ResourceUid,
            manager.ReadPartitionCell(children[1], 0).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionUnknownEffectPreservesShellAndRecoveryAuthority()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5004, 1));
        manager.RegisterCapability(
            table,
            new(
                5004,
                5004,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 1)).Partition);
        var existing = manager.RegisterResource(shell, 0, PartitionResource(50040));

        var uncertain = await manager.AdmitPartitionResourceAsync(shell, PartitionResource(50041));

        Assert.Equal(LocalResourcePartitionAdmissionStatus.EffectUncertain, uncertain.Status);
        Assert.Null(uncertain.Placement);
        var recovery = Assert.Single(uncertain.UncertainExecutions);
        Assert.True(recovery.IsRecoveryRequired);
        Assert.Equal(1, manager.PendingExecutionCount);
        var reconciled = manager.Reconcile(recovery, LocalResourceEffect.NoEffect);
        Assert.False(reconciled.EffectUncertain);
        Assert.Equal(0, manager.PendingExecutionCount);
        Assert.Equal(existing.ResourceUid, manager.ReadPartitionCell(shell, 0).Resource!.Value.ResourceUid);
    }

    [Fact]
    public async Task PartitionUseLeaseBlocksWholeReclaimButNotOrdinaryCapacityCleanup()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 10));
        var table = manager.RegisterTable(
            PartitionTable(5005, capacity: 10, reservationCapacity: 2));
        var released = new List<LocalResourceId>();
        manager.RegisterCapability(
            table,
            new(
                5005,
                5005,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                released.Add(context.ResourceUid);
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var shell = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var cold = manager.RegisterResource(shell, 0, PartitionResource(50050));
        var hot = manager.RegisterResource(shell, 1, PartitionResource(50051));
        manager.Touch(hot, 100);
        for (ulong uid = 50052; uid < 50060; uid++)
        {
            var direct = manager.RegisterResource(table, PartitionResource(uid));
            manager.Touch(direct, 10);
        }

        var hotLease = manager.BeginUse(hot);
        try
        {
            var tick = await manager.TickAsync();

            Assert.Equal(2, tick.CapacityActionCount);
            Assert.Equal(cold.ResourceUid, released[0]);
            Assert.Equal(LocalResourcePartitionCellKind.Empty, manager.ReadPartitionCell(shell, 0).Kind);
            Assert.Equal(hot.ResourceUid, manager.ReadPartitionCell(shell, 1).Resource!.Value.ResourceUid);
            Assert.True(manager.ReadPartition(shell).ReclaimProtected);
        }
        finally
        {
            manager.EndUse(hotLease);
        }
    }

    [Fact]
    public async Task PartitionCloseReleasesWholeTreeBeforeInvalidatingEveryHandle()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 4));
        var table = manager.RegisterTable(PartitionTable(5006, 4));
        var released = new List<LocalResourceId>();
        manager.RegisterCapability(
            table,
            new(
                5006,
                5006,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                released.Add(context.ResourceUid);
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 4)).Partition);
        var child = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitChildPartitionAsync(root, 2)).Partition);
        var childResource = manager.RegisterResource(child, 0, PartitionResource(50060));
        var rootResource = manager.RegisterResource(root, 3, PartitionResource(50061));

        var close = await manager.ClosePartitionAsync(root);

        Assert.Equal(LocalResourcePartitionCloseStatus.Closed, close.Status);
        Assert.Equal(2, close.InvokedActionCount);
        Assert.Equal(2, close.ReleasedResourceCount);
        Assert.Equal(2, released.Count);
        Assert.Contains(new LocalResourceId(50060), released);
        Assert.Contains(new LocalResourceId(50061), released);
        Assert.Equal(
            LocalResourceManagerError.StalePartition,
            Assert.Throws<NativeLocalResourceManagerException>(
                () => manager.ReadPartition(root)).Error);
        Assert.Equal(
            LocalResourceManagerError.StalePartition,
            Assert.Throws<NativeLocalResourceManagerException>(
                () => manager.ReadPartition(child)).Error);
        Assert.Equal(
            LocalResourceManagerError.StaleResource,
            Assert.Throws<NativeLocalResourceManagerException>(
                () => manager.Session.ReadResource(childResource)).Error);
        Assert.Equal(
            LocalResourceManagerError.StaleResource,
            Assert.Throws<NativeLocalResourceManagerException>(
                () => manager.Session.ReadResource(rootResource)).Error);
        manager.UnregisterTable(table);
    }

    [Fact]
    public async Task PartitionClosePreflightBlocksOccupiedAndAbsolutelyUndeletableResources()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5007, 2));
        var releaseCalls = 0;
        manager.RegisterCapability(
            table,
            new(
                5007,
                5007,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var occupied = manager.RegisterResource(root, 0, PartitionResource(50070));
        var undeletable = manager.RegisterResource(
            root,
            1,
            PartitionResource(50071) with
            {
                Recoverability = LocalResourceRecoverability.NotRecoverable
            });
        var lease = manager.BeginUse(occupied);

        var occupiedClose = await manager.ClosePartitionAsync(root);

        Assert.Equal(LocalResourcePartitionCloseStatus.Blocked, occupiedClose.Status);
        Assert.Equal(0, occupiedClose.InvokedActionCount);
        Assert.Equal(0, occupiedClose.ReleasedResourceCount);
        Assert.Equal(0, releaseCalls);
        Assert.Equal(2, manager.ReadPartition(root).DescendantResourceCount);

        manager.EndUse(lease);
        var undeletableClose = await manager.ClosePartitionAsync(root);

        Assert.Equal(LocalResourcePartitionCloseStatus.Blocked, undeletableClose.Status);
        Assert.Equal(0, releaseCalls);
        Assert.Equal(occupied, manager.ReadPartitionCell(root, 0).Resource);
        Assert.Equal(undeletable, manager.ReadPartitionCell(root, 1).Resource);

        manager.UnregisterResource(occupied);
        manager.UnregisterResource(undeletable);
        Assert.Equal(
            LocalResourcePartitionCloseStatus.Closed,
            (await manager.ClosePartitionAsync(root)).Status);
        manager.UnregisterTable(table);
    }

    [Fact]
    public async Task PartitionClosePreflightBlocksMissingManagedHandlerBeforeAnyAction()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5010, 2));
        var releaseCalls = 0;
        var capability = manager.RegisterCapability(
            table,
            new(
                5010,
                5010,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var first = manager.RegisterResource(root, 0, PartitionResource(50100));
        var second = manager.RegisterResource(root, 1, PartitionResource(50101));
        var handlersField = typeof(LocalResourceCapabilityRegistry).GetField(
            "_handlers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handlers = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            Assert.IsAssignableFrom<System.Reflection.FieldInfo>(handlersField)
                .GetValue(manager.Session.Capabilities));
        handlers.Clear();

        var close = await manager.ClosePartitionAsync(root);

        Assert.Equal(LocalResourcePartitionCloseStatus.Blocked, close.Status);
        Assert.Equal(0, close.InvokedActionCount);
        Assert.Equal(0, close.ReleasedResourceCount);
        Assert.Equal(0, releaseCalls);
        Assert.Equal(first, manager.ReadPartitionCell(root, 0).Resource);
        Assert.Equal(second, manager.ReadPartitionCell(root, 1).Resource);

        manager.UnregisterResource(first);
        manager.UnregisterResource(second);
        manager.UnregisterCapability(capability);
        Assert.Equal(
            LocalResourcePartitionCloseStatus.Closed,
            (await manager.ClosePartitionAsync(root)).Status);
        manager.UnregisterTable(table);
    }

    [Fact]
    public async Task PartitionCloseCancellationPreservesConfirmedReleaseAndShell()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5008, 2));
        using var cancellation = new CancellationTokenSource();
        var releaseCalls = 0;
        manager.RegisterCapability(
            table,
            new(
                5008,
                5008,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            (context, _) =>
            {
                releaseCalls++;
                if (releaseCalls == 1) cancellation.Cancel();
                return ValueTask.FromResult(LocalResourceEffect.Applied(
                    LocalResourceEffects.ReleasesLedgerSlot,
                    releasedBytes: context.SizeBytes));
            });
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var released = manager.RegisterResource(root, 0, PartitionResource(50080));
        var preserved = manager.RegisterResource(root, 1, PartitionResource(50081));

        var close = await manager.ClosePartitionAsync(root, cancellation.Token);

        Assert.Equal(LocalResourcePartitionCloseStatus.Canceled, close.Status);
        Assert.Equal(1, close.InvokedActionCount);
        Assert.Equal(1, close.ReleasedResourceCount);
        Assert.Equal(1, releaseCalls);
        Assert.Equal(root, manager.ReadPartition(root).Partition);
        Assert.Equal(LocalResourcePartitionCellKind.Empty, manager.ReadPartitionCell(root, 0).Kind);
        Assert.Equal(preserved, manager.ReadPartitionCell(root, 1).Resource);
        Assert.Equal(
            LocalResourceManagerError.StaleResource,
            Assert.Throws<NativeLocalResourceManagerException>(
                () => manager.Session.ReadResource(released)).Error);

        manager.UnregisterResource(preserved);
        Assert.Equal(
            LocalResourcePartitionCloseStatus.Closed,
            (await manager.ClosePartitionAsync(root)).Status);
        manager.UnregisterTable(table);
    }

    [Fact]
    public async Task PartitionCloseUnknownEffectPreservesShellAndRecoveryAuthority()
    {
        var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 1));
        try
        {
            var table = manager.RegisterTable(PartitionTable(5009, 1));
            manager.RegisterCapability(
                table,
                new(
                    5009,
                    5009,
                    LocalResourceEffects.ReleasesLedgerSlot,
                    Destructive: true),
                static (_, _) => ValueTask.FromResult(LocalResourceEffect.Unknown));
            var root = Assert.IsType<LocalResourcePartitionHandle>(
                (await manager.AdmitPartitionAsync(table, 1)).Partition);
            var resource = manager.RegisterResource(root, 0, PartitionResource(50090));

            var close = await manager.ClosePartitionAsync(root);

            Assert.Equal(LocalResourcePartitionCloseStatus.EffectUncertain, close.Status);
            Assert.Equal(1, close.InvokedActionCount);
            Assert.Equal(0, close.ReleasedResourceCount);
            var uncertain = Assert.Single(close.UncertainExecutions);
            Assert.Same(uncertain, Assert.Single(manager.GetRecoveryRequiredExecutions()));

            var reconciled = manager.Reconcile(uncertain, LocalResourceEffect.NoEffect);
            Assert.False(reconciled.EffectUncertain);
            Assert.Empty(manager.GetRecoveryRequiredExecutions());
            Assert.Equal(root, manager.ReadPartition(root).Partition);
            Assert.Equal(resource, manager.ReadPartitionCell(root, 0).Resource);
            manager.UnregisterResource(resource);
            Assert.Equal(
                LocalResourcePartitionCloseStatus.Closed,
                (await manager.ClosePartitionAsync(root)).Status);
            manager.UnregisterTable(table);
        }
        finally
        {
            _ = Record.Exception(() => ReconcileAll(manager));
            _ = Record.Exception(() => manager.Dispose());
        }
    }

    [Fact]
    public async Task PartitionExplicitEmptyCloseInvalidatesWholeTree()
    {
        using var manager = DefaultLocalResourceManager.Create(
            LocalResourceManagerConfiguration.CreateDefault(1, 2));
        var table = manager.RegisterTable(PartitionTable(5006, 2));
        var root = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitPartitionAsync(table, 2)).Partition);
        var child = Assert.IsType<LocalResourcePartitionHandle>(
            (await manager.AdmitChildPartitionAsync(root, 1)).Partition);

        Assert.Equal(root, manager.ReadPartition(root).Partition);
        Assert.Equal(child, manager.ReadPartition(child).Partition);
        var close = await manager.ClosePartitionAsync(root);

        Assert.Equal(LocalResourcePartitionCloseStatus.Closed, close.Status);
        Assert.Equal(0, close.InvokedActionCount);
        Assert.Equal(0, close.ReleasedResourceCount);

        var staleRoot = Assert.Throws<NativeLocalResourceManagerException>(
            () => manager.ReadPartition(root));
        var staleChild = Assert.Throws<NativeLocalResourceManagerException>(
            () => manager.ReadPartition(child));
        Assert.Equal(LocalResourceManagerError.StalePartition, staleRoot.Error);
        Assert.Equal(LocalResourceManagerError.StalePartition, staleChild.Error);
        manager.UnregisterTable(table);
    }

    private static NativeLocalResourceManagerSession CreateSession(
        int resourceCapacity,
        LocalResourceCapacityPolicy? capacityPolicy = null)
    {
        return new(LocalResourceManagerConfiguration.CreateDefault(
            tableCapacity: 4,
            resourceCapacity,
            capacityPolicy: capacityPolicy));
    }

    private static void RegisterPartitionReleaseCapability(
        DefaultLocalResourceManager manager,
        LocalResourceTableHandle table)
    {
        manager.RegisterCapability(
            table,
            new(
                9500,
                9500,
                LocalResourceEffects.ReleasesLedgerSlot,
                Destructive: true),
            static (context, _) => ValueTask.FromResult(LocalResourceEffect.Applied(
                LocalResourceEffects.ReleasesLedgerSlot,
                releasedBytes: context.SizeBytes)));
    }

    private static LocalResourceTableDefinition PartitionTable(
        ulong tableId,
        int capacity,
        int reservationStart = 0,
        int? reservationCapacity = null)
        => new(
            new(tableId),
            1,
            LocalResourceDomain.Memory,
            capacity,
            PartitionReservation: new(
                reservationStart,
                reservationCapacity ?? capacity));

    private static LocalResourceDefinition PartitionResource(ulong uid)
        => new(
            new(uid),
            SizeBytes: 4096,
            RecoveryCostCoefficient: 1,
            LocalResourceRecoverability.Recoverable,
            LocalResourceAccessLossImpact.UnobservableNow);

    private static (LocalResourceTableHandle Table, LocalResourceHandle Resource)
        RegisterMergeFixture(
            NativeLocalResourceManagerSession session,
            ulong identity)
    {
        var table = session.RegisterTable(
            new(new(identity), identity, LocalResourceDomain.Memory, 1));
        _ = session.Capabilities.Register(
            table,
            new(
                identity,
                checked((uint)identity),
                LocalResourceEffects.ReleasesLedgerSlot |
                    LocalResourceEffects.ChangesSizeBytes,
                Destructive: true),
            static (_, _) => ValueTask.FromResult(LocalResourceEffect.NoEffect));
        var resource = session.RegisterResource(
            table,
            new(
                new(identity),
                4096,
                1,
                LocalResourceRecoverability.Recoverable,
                LocalResourceAccessLossImpact.UnobservableNow));
        return (table, resource);
    }

    private static LocalResourcePlan ForgePlanForTest(
        LocalResourcePlan template,
        string kindName,
        LocalResourceIntent[] intents)
    {
        var constructor = Assert.Single(typeof(LocalResourcePlan).GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic));
        var parameters = constructor.GetParameters();
        var producer = typeof(LocalResourcePlan).GetProperty(
            "Producer",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.GetValue(template);
        var kind = Enum.Parse(parameters[5].ParameterType, kindName);
        return Assert.IsType<LocalResourcePlan>(constructor.Invoke(
            [
                producer,
                template.ManagerInstanceId,
                template.OperationId,
                template.OperationGeneration,
                template.SnapshotGeneration,
                kind,
                intents,
                intents.Length == 0 ? 0 : template.AffectedTableCount,
                0,
                0,
                0,
            ]));
    }

    private static LocalResourceIntent RewriteNativeIntentForTest(
        LocalResourceIntent source,
        string fieldPath,
        object value)
    {
        const System.Reflection.BindingFlags instanceFields =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public;
        var nativeProperty = Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(
            typeof(LocalResourceIntent).GetProperty("Native", instanceFields));
        var native = nativeProperty.GetValue(source);
        Assert.NotNull(native);
        RewriteBoxedField(native, fieldPath.Split('.'), 0, value, instanceFields);
        var constructor = Assert.Single(typeof(LocalResourceIntent).GetConstructors(
            instanceFields));
        return Assert.IsType<LocalResourceIntent>(constructor.Invoke([native]));
    }

    private static void RewriteBoxedField(
        object target,
        IReadOnlyList<string> fieldPath,
        int index,
        object value,
        System.Reflection.BindingFlags bindingFlags)
    {
        var field = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
            target.GetType().GetField(fieldPath[index], bindingFlags));
        if (index == fieldPath.Count - 1)
        {
            field.SetValue(target, value);
            return;
        }

        var nested = field.GetValue(target);
        Assert.NotNull(nested);
        RewriteBoxedField(nested, fieldPath, index + 1, value, bindingFlags);
        field.SetValue(target, nested);
    }

    private static void RecordMaximum(ref int target, int value)
    {
        var observed = Volatile.Read(ref target);
        while (value > observed)
        {
            var prior = Interlocked.CompareExchange(ref target, value, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }
}
