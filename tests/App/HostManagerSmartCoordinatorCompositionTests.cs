using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartCoordinatorCompositionTests
{
    [Fact]
    public void RegistrationUsesOneOwnerForConcreteInterfaceAndHostedService()
    {
        var services = new ServiceCollection();

        services.AddHostManagerSmartCoordinatorOwner();

        var concrete = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(HostManagerSmartCoordinator));
        var contract = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IHostManagerSmartCoordinator));
        var dependencyLifetime = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BusinessBlockDependencyLifetime));

        var owner = (HostManagerSmartCoordinator)RuntimeHelpers.GetUninitializedObject(
            typeof(HostManagerSmartCoordinator));
        var provider = new SingleServiceProvider(owner);
        // Other branches have hosted adapters too; only the coordinator's alias
        // must be unique and must reuse the concrete/interface owner.
        var hosted = Assert.Single(services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory is not null)
            .Select(descriptor => (Descriptor: descriptor,
                Service: descriptor.ImplementationFactory!(provider))),
            static entry => entry.Service is NonOwningHostedService<HostManagerSmartCoordinator>);

        Assert.Equal(ServiceLifetime.Singleton, concrete.Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, contract.Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, hosted.Descriptor.Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, dependencyLifetime.Lifetime);
        Assert.Equal(typeof(HostManagerSmartCoordinator), concrete.ImplementationType);

        Assert.Same(owner, contract.ImplementationFactory!(provider));
        var hostedAlias = Assert.IsType<
            NonOwningHostedService<HostManagerSmartCoordinator>>(
                hosted.Service);
        Assert.Same(owner, hostedAlias.Service);

        Assert.DoesNotContain(services, static descriptor =>
            descriptor.ServiceType == typeof(NativeSmartCoordinatorSession)
            || descriptor.ServiceType == typeof(NativeSmartCoordinatorWorkspace));
    }

    [Fact]
    public void ScoreOnlyAdmissionDeniesEveryCoordinatorEffectPermit()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: true);

        Assert.True(admission.IsScoreOnly);
        foreach (var kind in Enum.GetValues<HostManagerCycleEffectKind>())
        {
            Assert.False(admission.TryAcquire(kind, out var permit));
            Assert.Throws<InvalidOperationException>(() => permit.Require(kind));
        }
    }

    [Fact]
    public void ExecuteAdmissionIssuesOnlyExactTypedPermits()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);

        Assert.False(admission.IsScoreOnly);
        foreach (var kind in Enum.GetValues<HostManagerCycleEffectKind>())
        {
            Assert.True(admission.TryAcquire(kind, out var permit));
            permit.Require(kind);
            var differentKind = kind == HostManagerCycleEffectKind.SelfLedgerMaintenance
                ? HostManagerCycleEffectKind.NativeTransactionRecovery
                : HostManagerCycleEffectKind.SelfLedgerMaintenance;
            Assert.Throws<InvalidOperationException>(() => permit.Require(differentKind));
        }
    }

    [Fact]
    public void ValidationAdmissionAllowsOnlyJobScopedProcessEffectsAndRecovery()
    {
        var identity = new HostManagerComputeProcessIdentity(
            ProcessId: 24_901,
            ProcessStartKey: 132_537_600_000_000_001);
        var scope = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.Active,
            Guid.NewGuid(),
            generation: 1,
            jobName: "Global\\ResourceManager-NonAdaptedOptimizationLab-test",
            DateTimeOffset.UtcNow.AddMinutes(5),
            new HashSet<HostManagerComputeProcessIdentity> { identity },
            TimeProvider.System,
            nonAdaptedMemoryTransactionAllowed: true);
        var admission = HostManagerCycleEffectAdmission.CreateForValidation(
            scoreOnly: false,
            scope);

        foreach (var kind in new[]
        {
            HostManagerCycleEffectKind.NativeTransactionRecovery,
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            HostManagerCycleEffectKind.ExitedOwnershipReconciliation,
            HostManagerCycleEffectKind.NativeActionTransaction,
            HostManagerCycleEffectKind.NativeWorkspaceLifecycle
        })
        {
            Assert.True(admission.TryAcquire(kind, out var permit));
            permit.Require(kind);
        }

        foreach (var kind in new[]
        {
            HostManagerCycleEffectKind.SelfLedgerMaintenance,
            HostManagerCycleEffectKind.LegacyPlacementRestore,
            HostManagerCycleEffectKind.AutomaticMemoryCleanup
        })
        {
            Assert.False(admission.TryAcquire(kind, out _));
        }

        Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsNativeAction(
            scope,
            NativeSmartCoordinatorActionScope.ProcessPolicy));
    }

    [Fact]
    public void OnlyProductionUnscopedAllowsNewAutomaticPlacement()
    {
        var allowed = new HostManagerComputeProcessIdentity(24_901, 132_537_600_000_000_001);
        var outside = new HostManagerComputeProcessIdentity(24_902, 132_537_600_000_000_002);
        var active = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.Active,
            Guid.NewGuid(),
            generation: 1,
            jobName: "Global\\ResourceManager-NonAdaptedOptimizationLab-placement-test",
            DateTimeOffset.UtcNow.AddMinutes(5),
            new HashSet<HostManagerComputeProcessIdentity> { allowed },
            TimeProvider.System);

        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            active,
            allowed.ProcessId,
            allowed.ProcessStartKey));
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            active,
            outside.ProcessId,
            outside.ProcessStartKey));

        var faulted = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.FaultedBlocked,
            active.ScopeId,
            active.Generation,
            active.JobName,
            active.ExpiresAt,
            active.AllowedProcesses,
            TimeProvider.System);
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            faulted,
            allowed.ProcessId,
            allowed.ProcessStartKey));
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            faulted,
            outside.ProcessId,
            outside.ProcessStartKey));
        Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsCycleEffect(
            faulted,
            HostManagerCycleEffectKind.NativeActionTransaction));
        var faultedAdmission = HostManagerCycleEffectAdmission.CreateForValidation(
            scoreOnly: false,
            faulted);
        faultedAdmission.InitializeBudgets(
            newPointOfNoReturnCapacity: 1,
            recoveryCapacity: 1);
        Assert.True(faultedAdmission.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var restorePermit));
        Assert.True(restorePermit.TryReserveOwnedNativeRestore(out var restoreReservation));
        restoreReservation!.Dispose();

        var expired = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.ExpiredBlocked,
            active.ScopeId,
            active.Generation,
            active.JobName,
            active.ExpiresAt,
            active.AllowedProcesses,
            TimeProvider.System);
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            expired,
            allowed.ProcessId,
            allowed.ProcessStartKey));
        Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsCycleEffect(
            expired,
            HostManagerCycleEffectKind.NativeActionTransaction));

        var unknown = new HostManagerProcessEffectValidationCycleSnapshot(
            (HostManagerProcessEffectValidationScopeState)byte.MaxValue,
            Guid.NewGuid(),
            generation: 1,
            jobName: null,
            DateTimeOffset.MaxValue,
            allowedProcesses: null,
            timeProvider: null);
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            unknown,
            allowed.ProcessId,
            allowed.ProcessStartKey));
        Assert.Equal(0UL,
            HostManagerProcessEffectValidationCyclePolicy.CreateAllowedEffectMask(unknown));
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNativeAction(
            unknown,
            NativeSmartCoordinatorActionScope.ProcessPolicy));

        Assert.True(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped,
            outside.ProcessId,
            outside.ProcessStartKey));
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsNewAutomaticPlacement(
            HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped,
            processId: 0,
            outside.ProcessStartKey));
    }

    [Fact]
    public void ValidationAdmissionAllowsAutomaticMemoryCleanupOnlyWhenExplicitlyScoped()
    {
        var identity = new HostManagerComputeProcessIdentity(
            ProcessId: 24_902,
            ProcessStartKey: 132_537_600_000_000_002);
        var scope = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.Active,
            Guid.NewGuid(),
            generation: 1,
            jobName: "Global\\ResourceManager-NonAdaptedOptimizationLab-trim-test",
            DateTimeOffset.UtcNow.AddMinutes(5),
            new HashSet<HostManagerComputeProcessIdentity> { identity },
            TimeProvider.System,
            automaticMemoryCleanupAllowed: true,
            nonAdaptedMemoryTransactionAllowed: false);
        var admission = HostManagerCycleEffectAdmission.CreateForValidation(
            scoreOnly: false,
            scope);

        Assert.True(scope.AutomaticMemoryCleanupAllowed);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var cleanupPermit));
        cleanupPermit.Require(HostManagerCycleEffectKind.AutomaticMemoryCleanup);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            out _));
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsFreshProcessMemory(scope));

        foreach (var kind in new[]
        {
            HostManagerCycleEffectKind.SelfLedgerMaintenance,
            HostManagerCycleEffectKind.LegacyPlacementRestore
        })
        {
            Assert.False(admission.TryAcquire(kind, out _));
        }
    }

    [Fact]
    public void DefaultAdmissionAndPermitFailClosed()
    {
        var admission = default(HostManagerCycleEffectAdmission);
        var permit = default(HostManagerCycleEffectPermit);

        Assert.Throws<InvalidOperationException>(() => _ = admission.IsScoreOnly);
        Assert.Throws<InvalidOperationException>(() =>
            admission.TryAcquire(
                HostManagerCycleEffectKind.AutomaticMemoryCleanup,
                out _));
        Assert.Throws<InvalidOperationException>(() => permit.Require(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup));
    }

    [Theory]
    [InlineData((int)HostManagerProcessEffectValidationScopeState.Active, false, false)]
    [InlineData((int)HostManagerProcessEffectValidationScopeState.Active, true, true)]
    [InlineData((int)HostManagerProcessEffectValidationScopeState.ExpiredBlocked, false, false)]
    [InlineData((int)HostManagerProcessEffectValidationScopeState.ExpiredBlocked, true, false)]
    [InlineData((int)HostManagerProcessEffectValidationScopeState.FaultedBlocked, true, false)]
    public void ScopedMemoryRecoveryPermitDoesNotGrantFreshWriteCapability(
        int stateValue, bool capability, bool freshAllowed)
    {
        var scope = new HostManagerProcessEffectValidationCycleSnapshot(
            (HostManagerProcessEffectValidationScopeState)stateValue, Guid.NewGuid(), 1, "test-job", DateTimeOffset.MaxValue,
            new HashSet<HostManagerComputeProcessIdentity>(), TimeProvider.System,
            nonAdaptedMemoryTransactionAllowed: capability);
        var admission = HostManagerCycleEffectAdmission.CreateForValidation(false, scope);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction, out var permit));
        Assert.True(permit.TryReserveOwnedMemoryRestore(1, out var reservation));
        reservation!.Dispose();
        Assert.Equal(freshAllowed, HostManagerProcessEffectValidationCyclePolicy.AllowsFreshProcessMemory(scope));
    }

    [Fact]
    public void CapturedAdmissionDoesNotFollowLaterSettingChanges()
    {
        var scoreOnlySetting = true;
        var scoreOnlyCycle = HostManagerCycleEffectAdmission.Create(scoreOnlySetting);
        scoreOnlySetting = false;

        Assert.False(scoreOnlyCycle.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out _));

        var executeCycle = HostManagerCycleEffectAdmission.Create(scoreOnlySetting);
        scoreOnlySetting = true;

        Assert.True(executeCycle.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var permit));
        permit.Require(HostManagerCycleEffectKind.NativeActionTransaction);
        Assert.True(scoreOnlySetting);
    }

    [Fact]
    public void CopiedAdmissionsShareOneNewPonrBudgetAndAnIndependentRecoveryBudget()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(
            newPointOfNoReturnCapacity: 1,
            recoveryCapacity: 1);
        var copied = admission;

        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            out var memoryPermit));
        Assert.True(memoryPermit.TryReserveNewPointOfNoReturn(1, out var memory));
        Assert.Equal(1U, memory!.Count);

        Assert.True(copied.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var nativePermit));
        Assert.False(nativePermit.TryReserveNewPointOfNoReturn(1, out _));

        Assert.True(copied.TryAcquire(
            HostManagerCycleEffectKind.NativeTransactionRecovery,
            out var recoveryPermit));
        Assert.True(recoveryPermit.TryReserveRecovery(1, out var recovery));
        Assert.Equal(1U, recovery!.Count);
        Assert.Equal(
            new HostManagerCycleEffectBudgetSnapshot(1, 0, 1, 0),
            admission.CaptureBudgetSnapshot());
    }

    [Fact]
    public void OnlyExplicitPrePonrProofReturnsUnusedReservationCapacity()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(
            newPointOfNoReturnCapacity: 2,
            recoveryCapacity: 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var permit));
        Assert.True(permit.TryReserveNewPointOfNoReturn(2, out var reservation));

        reservation!.ReleaseUnusedBeforePointOfNoReturn(consumedCount: 1);

        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Throws<InvalidOperationException>(() =>
            reservation.ReleaseUnusedBeforePointOfNoReturn(consumedCount: 0));
        Assert.True(permit.TryReserveNewPointOfNoReturn(1, out _));
        Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void ScopedReservationReturnsOnlyUnitsThatNeverEnteredPonr(
        int enteredCount,
        uint expectedRemaining)
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(
            newPointOfNoReturnCapacity: 2,
            recoveryCapacity: 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var permit));
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(2, out var reservation));
        Assert.NotNull(reservation);

        using (reservation)
        {
            for (var index = 0; index < enteredCount; index++)
            {
                reservation.EnterPointOfNoReturn();
            }
        }

        Assert.Equal(expectedRemaining, admission.NewPointOfNoReturnRemaining);
    }

    [Fact]
    public void ExactNativeReservationNeverPartiallyConsumesCompositeBudget()
    {
        var insufficient = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        insufficient.InitializeBudgets(
            newPointOfNoReturnCapacity: 1,
            recoveryCapacity: 1);
        Assert.True(insufficient.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var insufficientPermit));

        Assert.False(insufficientPermit.TryReserveExactNewPointOfNoReturn(
            2,
            out var rejected));
        Assert.Null(rejected);
        Assert.Equal(1U, insufficient.NewPointOfNoReturnRemaining);

        var sufficient = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        sufficient.InitializeBudgets(
            newPointOfNoReturnCapacity: 2,
            recoveryCapacity: 1);
        Assert.True(sufficient.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var sufficientPermit));

        Assert.True(sufficientPermit.TryReserveExactNewPointOfNoReturn(
            2,
            out var admitted));
        Assert.Equal(2U, admitted!.Count);
        Assert.Equal(0U, sufficient.NewPointOfNoReturnRemaining);
    }

    [Fact]
    public void ScoreOnlyAndInvalidBudgetUseFailClosed()
    {
        var scoreOnly = HostManagerCycleEffectAdmission.Create(scoreOnly: true);
        scoreOnly.InitializeBudgets(4, 4);
        Assert.Equal(
            new HostManagerCycleEffectBudgetSnapshot(0, 0, 0, 0),
            scoreOnly.CaptureBudgetSnapshot());
        var zeroCapacityScoreOnly = HostManagerCycleEffectAdmission.Create(scoreOnly: true);
        zeroCapacityScoreOnly.InitializeBudgets(0, 0);
        Assert.Equal(
            new HostManagerCycleEffectBudgetSnapshot(0, 0, 0, 0),
            zeroCapacityScoreOnly.CaptureBudgetSnapshot());

        var execute = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        Assert.Throws<InvalidOperationException>(() =>
            _ = execute.NewPointOfNoReturnRemaining);
        Assert.True(execute.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var uninitializedPermit));
        Assert.Throws<InvalidOperationException>(() =>
            uninitializedPermit.TryReserveNewPointOfNoReturn(0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execute.InitializeBudgets(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execute.InitializeBudgets(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execute.InitializeBudgets(0, 0));

        execute = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        execute.InitializeBudgets(1, 1);
        Assert.True(execute.TryAcquire(
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            out var memoryPermit));
        Assert.Throws<InvalidOperationException>(() =>
            memoryPermit.TryReserveRecovery(1, out _));
        Assert.True(execute.TryAcquire(
            HostManagerCycleEffectKind.SelfLedgerMaintenance,
            out var maintenancePermit));
        Assert.Throws<InvalidOperationException>(() =>
            maintenancePermit.TryReserveNewPointOfNoReturn(1, out _));
    }

    [Fact]
    public void CoordinatorEffectEntrypointsRequireTypedAdmission()
    {
        var permitEntrypoints = new[]
        {
            "RunSelfLocalResourceManagerTickAsync",
            "EnsureNativeWorkspaceAsync",
            "ApplyNonAdaptedMemoryModeTransactionsAsync",
            "ReconcileExitedProcessOwnershipAsync",
            "ExecuteNativeTransactionsAsync",
            "ApplyAutomaticMemoryCleanupAsync",
            "ExecuteMemoryCleanup"
        };
        foreach (var methodName in permitEntrypoints)
        {
            var method = RequireSinglePrivateMethod(methodName);
            Assert.Equal(
                typeof(HostManagerCycleEffectPermit),
                Assert.Single(method.GetParameters().Take(1)).ParameterType);
        }

        var admissionEntrypoints = new[]
        {
            "RecoverNativeTransactionsAsync",
            "PrepareLegacyStateAsync",
            "RunNativePlanningAndActionsAsync"
        };
        foreach (var methodName in admissionEntrypoints)
        {
            var method = RequireSinglePrivateMethod(methodName);
            Assert.Equal(
                typeof(HostManagerCycleEffectAdmission),
                Assert.Single(method.GetParameters().Take(1)).ParameterType);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScoreOnlyExternalCompositionCallsNoEffectSink(
        bool automaticMemoryCleanupEnabled)
    {
        var sink = new CountingExternalEffectSink();

        await HostManagerNativeExternalEffectDispatcher.RunAsync(
            HostManagerCycleEffectAdmission.Create(scoreOnly: true),
            automaticMemoryCleanupEnabled,
            sink);

        Assert.Equal(0, sink.MemoryJournalReconcileCalls);
        Assert.Equal(0, sink.MemoryJournalPrepareCalls);
        Assert.Equal(0, sink.ProcessBatchApplyCalls);
        Assert.Equal(0, sink.MemoryJournalMarkUnknownCalls);
        Assert.Equal(0, sink.MemoryJournalSettleCalls);
    }

    [Theory]
    [InlineData(AppOptimizationModes.MemoryOnly, 0.50)]
    [InlineData(AppOptimizationModes.MemoryOnly, 0.01)]
    [InlineData(AppOptimizationModes.Smart, 0.50)]
    [InlineData(AppOptimizationModes.Smart, 0.01)]
    public async Task ScoreOnlySupportedModesStayEffectFreeAtEveryMemoryPressure(
        string mode,
        double memoryFreeRatio)
    {
        var modePlan = CompiledOptimizationModePlan.Compile(mode);
        var sink = new CountingExternalEffectSink(memoryFreeRatio);

        await HostManagerNativeExternalEffectDispatcher.RunAsync(
            HostManagerCycleEffectAdmission.Create(scoreOnly: true),
            modePlan.AutomaticMemoryCleanupEnabled,
            sink);

        Assert.True(modePlan.AutomaticMemoryCleanupEnabled);
        Assert.Equal(0, sink.MemoryJournalReconcileCalls);
        Assert.Equal(0, sink.MemoryJournalPrepareCalls);
        Assert.Equal(0, sink.ProcessBatchApplyCalls);
        Assert.Equal(0, sink.MemoryJournalMarkUnknownCalls);
        Assert.Equal(0, sink.MemoryJournalSettleCalls);
        Assert.Equal(memoryFreeRatio, sink.MemoryFreeRatio);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ExecuteExternalCompositionPreservesExistingAdmission(
        bool automaticMemoryCleanupEnabled,
        int expectedMemoryCalls)
    {
        var sink = new CountingExternalEffectSink();

        await HostManagerNativeExternalEffectDispatcher.RunAsync(
            HostManagerCycleEffectAdmission.Create(scoreOnly: false),
            automaticMemoryCleanupEnabled,
            sink);

        Assert.Equal(expectedMemoryCalls, sink.MemoryJournalReconcileCalls);
        Assert.Equal(expectedMemoryCalls, sink.MemoryJournalPrepareCalls);
        Assert.Equal(expectedMemoryCalls, sink.ProcessBatchApplyCalls);
        Assert.Equal(expectedMemoryCalls, sink.MemoryJournalMarkUnknownCalls);
        Assert.Equal(expectedMemoryCalls, sink.MemoryJournalSettleCalls);
    }

    [Fact]
    public void EveryNewPonrKindSharesOneTotalBudget()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(
            newPointOfNoReturnCapacity: 1,
            recoveryCapacity: 1);
        var kinds = new[]
        {
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            HostManagerCycleEffectKind.NativeActionTransaction,
            HostManagerCycleEffectKind.AutomaticMemoryCleanup
        };
        var admitted = 0U;

        foreach (var kind in kinds)
        {
            Assert.True(admission.TryAcquire(kind, out var permit));
            if (permit.TryReserveNewPointOfNoReturn(1, out var reservation))
            {
                admitted = checked(admitted + reservation!.Count);
            }
        }

        Assert.Equal(1U, admitted);
        Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(1U, admission.RecoveryRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedNativeRestoreUsesOnlyTheRecoveryBudget(bool newBudgetSpent)
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction, out var native));
        if (newBudgetSpent)
        {
            Assert.True(native.TryReserveExactNewPointOfNoReturn(1, out var apply));
            using (apply)
            {
                apply!.EnterPointOfNoReturn();
            }
        }
        Assert.True(native.TryReserveOwnedNativeRestore(out var restore));
        using (restore)
        {
            restore!.EnterPointOfNoReturn();
        }
        Assert.False(native.TryReserveOwnedNativeRestore(out _));
        Assert.Equal(newBudgetSpent ? 0u : 1u, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0u, admission.RecoveryRemaining);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup, out var cleanup));
        Assert.Throws<InvalidOperationException>(() => cleanup.TryReserveOwnedNativeRestore(out _));
    }

    [Fact]
    public void OwnedMemoryRestoreUsesItsNarrowIndependentRecoveryBudget()
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(
            newPointOfNoReturnCapacity: 1,
            recoveryCapacity: 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NativeActionTransaction,
            out var actionPermit));
        Assert.True(actionPermit.TryReserveNewPointOfNoReturn(1, out _));
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
            out var memoryPermit));

        Assert.False(memoryPermit.TryReserveNewPointOfNoReturn(1, out _));
        Assert.True(memoryPermit.TryReserveOwnedMemoryRestore(1, out var restore));
        Assert.Equal(1U, restore!.Count);
        Assert.Throws<InvalidOperationException>(() =>
            memoryPermit.TryReserveRecovery(1, out _));
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup,
            out var cleanupPermit));
        Assert.Throws<InvalidOperationException>(() =>
            cleanupPermit.TryReserveOwnedMemoryRestore(1, out _));
        Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0U, admission.RecoveryRemaining);
    }


    private sealed class SingleServiceProvider(HostManagerSmartCoordinator owner) : IServiceProvider
    {
        private NativeEtwLifetimeService? nativeLifetime;

        public object? GetService(Type serviceType)
            => serviceType == typeof(HostManagerSmartCoordinator) ? owner
                : serviceType == typeof(NativeEtwLifetimeService)
                    ? nativeLifetime ??= new NativeEtwLifetimeService() : null;
    }

    private static MethodInfo RequireSinglePrivateMethod(string name)
        => Assert.Single(typeof(HostManagerSmartCoordinator).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name.Equals(name, StringComparison.Ordinal));

    private sealed class CountingExternalEffectSink(double memoryFreeRatio = 1)
        : IHostManagerNativeExternalEffectSink
    {
        internal double MemoryFreeRatio { get; } = memoryFreeRatio;
        internal int MemoryJournalReconcileCalls { get; private set; }
        internal int MemoryJournalPrepareCalls { get; private set; }
        internal int ProcessBatchApplyCalls { get; private set; }
        internal int MemoryJournalMarkUnknownCalls { get; private set; }
        internal int MemoryJournalSettleCalls { get; private set; }

        public ValueTask RunAutomaticMemoryCleanupAsync(HostManagerCycleEffectPermit permit)
        {
            permit.Require(HostManagerCycleEffectKind.AutomaticMemoryCleanup);
            MemoryJournalReconcileCalls++;
            MemoryJournalPrepareCalls++;
            ProcessBatchApplyCalls++;
            MemoryJournalMarkUnknownCalls++;
            MemoryJournalSettleCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
