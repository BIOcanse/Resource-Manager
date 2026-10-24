using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeWorkspaceGrowthLoggingTests
{
    [Fact]
    public void OperationSubmitLogsOneManagedEventOnlyWhenItsNativeBankGrows()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        using var workspace = new NativeOperationCoordinatorWorkspace(NativeOperationScratchTests.Plan(),
            new(1, 2), new(3, 4), ledger.CreateScratch);
        Assert.Empty(logger.Events);
        for (ulong id = 1; id <= 16; id++) Submit(workspace, id);
        ReadDiagnostics(ledger);
        Assert.Empty(logger.Events);

        Submit(workspace, 17);
        AssertGrowth(Assert.Single(logger.Events), "operation-coordinator", 16, 32,
            NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.operation_submit);
        Submit(workspace, 18);
        _ = workspace.SnapshotLocked();
        ReadDiagnostics(ledger);
        Assert.Single(logger.Events);
    }

    [Fact]
    public void OwnershipPromotionLogsEachGrowthOnceAcrossCapacityReads()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        using var session = new NativeAppliedOwnershipSession(OwnershipConfiguration());
        using var workspace = new NativeAppliedOwnershipWorkspace(session.Capacity, ledger.CreateScratch, session);
        for (uint id = 1; id <= 16; id++) Promote(session, id);
        Assert.Empty(logger.Events);
        Promote(session, 17);
        AssertGrowth(Assert.Single(logger.Events), "applied-ownership", 16, 32,
            NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.ownership_promote);
        ReadDiagnostics(ledger);
        _ = session.WorkspaceCapacity;
        for (uint id = 18; id <= 32; id++) Promote(session, id);
        Assert.Single(logger.Events);
        Promote(session, 33);
        Assert.Equal(2, logger.Events.Count);
        AssertGrowth(logger.Events[1], "applied-ownership", 32, 64,
            NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.ownership_promote);
        ReadDiagnostics(ledger);
        Assert.Equal(2UL, session.WorkspaceCapacity.Growth.Count);
        Assert.Equal(2, logger.Events.Count);
    }

    [Fact]
    public void JournalPrepareLogsNativeGrowthAndWorkspaceDisposalDetachesItsObserver()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        using var session = new NativeTransactionJournalSession(JournalConfiguration());
        using var workspace = new NativeTransactionJournalWorkspace(session.Capacity, ledger.CreateScratch, session);
        for (uint id = 1; id <= 16; id++) Prepare(session, id);
        Assert.Empty(logger.Events);
        Prepare(session, 17);
        AssertGrowth(Assert.Single(logger.Events), "transaction-journal", 16, 32,
            NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.journal_prepare);
        ReadDiagnostics(ledger);
        _ = session.WorkspaceCapacity;
        for (uint id = 18; id <= 32; id++) Prepare(session, id);
        Assert.Single(logger.Events);
        Prepare(session, 33);
        Assert.Equal(2, logger.Events.Count);
        AssertGrowth(logger.Events[1], "transaction-journal", 32, 64,
            NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.journal_prepare);
        ReadDiagnostics(ledger);
        Assert.Equal(2, logger.Events.Count);

        workspace.Dispose();
        Assert.Empty(ledger.ReadWorkspaceCapacities());
        // This authority has a separate lifetime from the workspace registration.
        for (uint id = 34; id <= 65; id++) Prepare(session, id);
        Assert.Equal(128U, session.WorkspaceCapacity.RecordCapacity);
        Assert.Equal(3UL, session.WorkspaceCapacity.Growth.Count);
        Assert.Equal(2, logger.Events.Count);
    }

    [Fact]
    public void SmartPlanForTwoIdentitiesDeliversBothNativeGrowthEventsOnce()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        var plan = HostManagerTestPlanFactory.SmartCoordinator;
        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(plan.Build, plan.Recreate, plan.HotPublish, 1);
        using var workspace = new NativeSmartCoordinatorWorkspace(configuration, ledger.CreateScratch);
        workspace.EnsureRows(2);
        Assert.Empty(logger.Events); // Managed scratch growth has a separate event identity.
        using (var operation = workspace.BeginOperation())
        {
            using var buffers = workspace.CreateScratchView();
            buffers.Inputs[0] = ProcessFact(1);
            buffers.Inputs[1] = ProcessFact(2);
            var input = new NativeSmartCoordinatorCycleInput
            {
                AbiVersion = NativeSmartCoordinatorAbi.Version,
                StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>(),
                InputRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
                ActionStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
                ConfigurationGeneration = configuration.Generation, CycleSequence = 1,
                ObservedAtMilliseconds = 1_000, ValidMask = NativeSmartCoordinatorCycleValidity.Known,
                Flags = NativeSmartCoordinatorCycleFlags.FullProcessSnapshot |
                    NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts | NativeSmartCoordinatorCycleFlags.ScoreOnly,
                InputCount = 2, ActionCapacity = (uint)buffers.Actions.Length, RoundNumber = 1,
                MaximumActionsThisCycle = 0
            };
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in input));
            Assert.Equal(2, logger.Events.Count);
            AssertGrowth(logger.Events[0], "smart-coordinator", 1, 2,
                NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.smart_process);
            AssertGrowth(logger.Events[1], "smart-coordinator", 1, 2,
                NativeWorkspaceCapacityUnit.Rows, NativeWorkspaceGrowthReason.smart_software);
            ReadDiagnostics(ledger);
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
            input.CycleSequence++;
            input.RoundNumber++;
            input.ObservedAtMilliseconds++;
            input.Flags &= ~NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts;
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in input));
            operation.Complete();
        }
        ReadDiagnostics(ledger);
        Assert.Equal(2, logger.Events.Count);
    }

    [Fact]
    public void ReportRuleGrowthLogsTheNativeArenaByteCapacitiesWithoutReadDuplicates()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        using var workspace = new NativeReportCoordinatorWorkspace(
            plan, 101, 102, ledger.CreateScratch);
        var session = workspace.Session;
        var initial = session.WorkspaceCapacity;
        Assert.Empty(logger.Events);
        var input = new NativeReportRuleReplaceInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportRuleReplaceInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = 1, CommandMonotonicMilliseconds = 1, CommandUtcMilliseconds = 100,
            ValidMask = (ulong)NativeReportRuleReplaceValidity.Required, RuleCount = 2
        };
        NativeReportRuleInput[] rules = [ReportRule(21), ReportRule(22)];
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.ReplaceRules(input, rules));
        var grown = session.WorkspaceCapacity;
        var firstGrowth = grown.Growth.Events[0];
        Assert.True(grown.ResidentByteCount > initial.ResidentByteCount);
        Assert.Equal(grown.ResidentByteCount - initial.ResidentByteCount,
            firstGrowth.NewCapacity - firstGrowth.OldCapacity);
        AssertGrowth(Assert.Single(logger.Events), "report-coordinator", firstGrowth.OldCapacity,
            firstGrowth.NewCapacity, NativeWorkspaceCapacityUnit.Bytes, NativeWorkspaceGrowthReason.report_rules);
        ReadDiagnostics(ledger);
        input.OperationEpoch++;
        input.CommandMonotonicMilliseconds++;
        input.CommandUtcMilliseconds++;
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.ReplaceRules(input, rules));
        ReadDiagnostics(ledger);
        Assert.Single(logger.Events);

        input.OperationEpoch++;
        input.CommandMonotonicMilliseconds++;
        input.CommandUtcMilliseconds++;
        input.RuleCount = 3;
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.ReplaceRules(input,
            [ReportRule(21), ReportRule(22), ReportRule(23)]));
        var secondGrowth = session.WorkspaceCapacity.Growth.Events[1];
        Assert.Equal(2, logger.Events.Count);
        Assert.Equal(firstGrowth.NewCapacity, secondGrowth.OldCapacity);
        Assert.Equal(session.WorkspaceCapacity.ResidentByteCount - grown.ResidentByteCount,
            secondGrowth.NewCapacity - secondGrowth.OldCapacity);
        AssertGrowth(logger.Events[1], "report-coordinator", secondGrowth.OldCapacity,
            secondGrowth.NewCapacity, NativeWorkspaceCapacityUnit.Bytes,
            NativeWorkspaceGrowthReason.report_rules);
        ReadDiagnostics(ledger);
        Assert.Equal(2, logger.Events.Count);
    }

    [Fact]
    public void OneReportObserveDeliversBothPublishedArenaGrowthEvents()
    {
        var logger = new GrowthLogger();
        using var ledger = new ResourceManagerLedgerOwner(logger);
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        using var workspace = new NativeReportCoordinatorWorkspace(plan, 101, 102, ledger.CreateScratch);
        var session = workspace.Session;
        var rule = ReportRule(21);
        rule.Flags = (uint)NativeReportRuleFlags.Rolling;
        rule.MetricSelector = (uint)NativeReportMetricSelector.Sum24Hours;
        rule.RequiredConsecutiveHits = 1;
        rule.ActivationThreshold = 1;
        rule.ClearThreshold = 0.5;
        var replace = new NativeReportRuleReplaceInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportRuleReplaceInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = 1, CommandMonotonicMilliseconds = 1, CommandUtcMilliseconds = 1000,
            ValidMask = (ulong)NativeReportRuleReplaceValidity.Required, RuleCount = 1
        };
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.ReplaceRules(replace, [rule]));
        var import = new NativeReportImportInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportImportInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration, ImportGeneration = 1,
            OperationEpoch = 2, CommandMonotonicMilliseconds = 2, CommandUtcMilliseconds = 1000,
            ValidMask = (ulong)NativeReportImportValidity.Required
        };
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.Import(import, []));
        var observe = new NativeReportSourceSnapshotInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportSourceSnapshotInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = 3, CommandMonotonicMilliseconds = 3,
            CommandUtcMilliseconds = 1000, ObservedAtUtcMilliseconds = 1000,
            SourceHandle = 11, SourceGeneration = 12, CoverageScopeHandle = 13,
            SourceSnapshotEpoch = 1, ValidMask = (ulong)NativeReportSourceSnapshotValidity.Required,
            FactCount = 1, Status = (uint)NativeReportSourceStatus.Complete
        };
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.Observe(observe, [RollingFact(41, 1, 0)]));
        var before = session.WorkspaceCapacity;
        var eventCount = logger.Events.Count;
        observe.OperationEpoch++;
        observe.CommandMonotonicMilliseconds++;
        observe.CommandUtcMilliseconds++;
        observe.ObservedAtUtcMilliseconds++;
        observe.SourceSnapshotEpoch++;
        observe.FactCount = 2;
        Assert.Equal(NativeReportCoordinatorStatus.Ok, session.Observe(observe,
            [RollingFact(41, 2, 4), RollingFact(42, 1, 6)]));
        var after = session.WorkspaceCapacity;
        Assert.Equal(before.Growth.Count + 2, after.Growth.Count);
        Assert.Equal(eventCount + 2, logger.Events.Count);
        var factsGrowth = after.Growth.Events[(int)(before.Growth.Count % 8)];
        var reportsGrowth = after.Growth.Events[(int)((before.Growth.Count + 1) % 8)];
        Assert.Equal(after.ResidentByteCount - before.ResidentByteCount,
            reportsGrowth.NewCapacity - factsGrowth.OldCapacity);
        AssertGrowth(logger.Events[eventCount], "report-coordinator", factsGrowth.OldCapacity,
            factsGrowth.NewCapacity, NativeWorkspaceCapacityUnit.Bytes, NativeWorkspaceGrowthReason.report_facts);
        AssertGrowth(logger.Events[eventCount + 1], "report-coordinator", factsGrowth.NewCapacity,
            reportsGrowth.NewCapacity, NativeWorkspaceCapacityUnit.Bytes, NativeWorkspaceGrowthReason.report_reports);
        Assert.Equal(factsGrowth.NewCapacity, reportsGrowth.OldCapacity);
        ReadDiagnostics(ledger);
        Assert.Equal(eventCount + 2, logger.Events.Count);
    }

    [Fact]
    public void ObserverRejectsAnOverrunInsteadOfSilentlyDroppingGrowthEvents()
    {
        var observed = new List<NativeWorkspaceGrowthEvent>();
        var observer = new NativeWorkspaceGrowthObserver { Observer = observed.Add };
        var log = new NativeWorkspaceGrowthLog { Count = 9 };
        Assert.Throws<InvalidOperationException>(() => observer.Read(log));
        Assert.Empty(observed);
    }

    private static void ReadDiagnostics(ResourceManagerLedgerOwner ledger)
    {
        _ = ledger.ReadWorkspaceCapacities();
        _ = ledger.ReadWorkspaceCapacities();
    }

    private static void AssertGrowth(GrowthEntry entry, string owner, ulong oldCapacity, ulong newCapacity,
        NativeWorkspaceCapacityUnit unit, NativeWorkspaceGrowthReason reason)
    {
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(owner, entry.Fields["Owner"]);
        Assert.Equal(oldCapacity, Assert.IsType<ulong>(entry.Fields["OldCapacity"]));
        Assert.Equal(newCapacity, Assert.IsType<ulong>(entry.Fields["NewCapacity"]));
        Assert.Equal(unit, Assert.IsType<NativeWorkspaceCapacityUnit>(entry.Fields["CapacityUnit"]));
        Assert.Equal(reason, Assert.IsType<NativeWorkspaceGrowthReason>(entry.Fields["Reason"]));
        Assert.Contains(owner, entry.Message);
        Assert.Contains(reason.ToString(), entry.Message);
    }

    private sealed record GrowthEntry(LogLevel Level, Dictionary<string, object?> Fields, string Message);

    private sealed class GrowthLogger : ILogger<ResourceManagerLedgerOwner>
    {
        internal List<GrowthEntry> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 8101 || eventId.Name != "NativeWorkspaceGrowth") return;
            Assert.Null(exception);
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Events.Add(new(logLevel, fields.ToDictionary(pair => pair.Key, pair => pair.Value), formatter(state, exception)));
        }
    }

    private static void Submit(NativeOperationCoordinatorWorkspace workspace, ulong id)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tick = NativeOperationCoordinatorWorkspace.MonotonicMilliseconds();
        var input = new NativeOperationSubmitInput
        {
            OperationId = new(id, 12), KindHandle = new(21, 22), RequestHandle = new(31, 32),
            MaximumAttempts = 1, RetryDelayMilliseconds = 1, ExecutionTimeoutMilliseconds = 30000,
            CancelGraceMilliseconds = 5000, TerminalRetentionMilliseconds = 60000,
            CreatedUtcMilliseconds = now, ObservedUtcMilliseconds = now,
            CreatedMonotonicMilliseconds = tick, ObservedMonotonicMilliseconds = tick,
            ValidMask = (ulong)NativeOperationSubmitValidity.Request
        };
        _ = workspace.SubmitLocked(ref input);
    }

    private static NativeAppliedOwnershipCreateConfiguration OwnershipConfiguration() => new()
    {
        AbiVersion = NativeAppliedOwnershipAbi.Version, StructSize = NativeAppliedOwnershipAbi.CreateConfigurationSize,
        LedgerInstanceLow = 1, LedgerInstanceHigh = 2, RecordCapacity = 8192,
        PrimaryIndexCapacity = 16384, PayloadIndexCapacity = 16384,
        MaximumResidentBytes = 16 * 1024 * 1024, MaximumImageBytes = 128 + 8192 * 320
    };

    private static void Promote(NativeAppliedOwnershipSession session, uint id)
    {
        var input = new NativeAppliedOwnershipPromoteInput
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version, StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
            ExpectedLedgerRevision = id,
            Primary = new() { Scope = 1, TargetId = id, SoftwareId = id, ProcessStartKey = id, ProcessId = id },
            OriginalBinding = new()
            {
                JournalInstanceLow = 11, JournalInstanceHigh = 12,
                ActionIdentity = new()
                {
                    ConfigurationGeneration = 1, PlanEpoch = 2, ActionId = id, HostSessionIncarnation = 4,
                    TargetId = id, SoftwareId = id, ProcessStartKey = id, ProcessId = id
                },
                Scope = 1, Disposition = 1, DomainMask = 1, GradeValidMask = 1,
                ProcessFromGrade = 0, ProcessToGrade = -1, MaximumRecoveryAttempts = 3,
                RecoveryDeadlineUtcMilliseconds = 11000
            },
            Payload = new() { Slot = id, Generation = 1, Length = 512, DigestLow = 1, DigestHigh = 2 },
            CurrentGrades = new() { ValidMask = 1, ProcessGrade = -1 }, PromotedAtUtcMilliseconds = 1000
        };
        Assert.Equal(NativeAppliedOwnershipStatus.Ok, session.Promote(input));
    }

    private static NativeTransactionJournalCreateConfiguration JournalConfiguration() => new()
    {
        AbiVersion = NativeTransactionJournalAbi.Version,
        StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalCreateConfiguration>(),
        JournalInstanceLow = 1, JournalInstanceHigh = 2, RecordCapacity = 8192,
        MaximumResidentBytes = 16 * 1024 * 1024
    };

    private static void Prepare(NativeTransactionJournalSession session, uint id)
    {
        var input = new NativeTransactionJournalPrepareInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = id,
            Identity = new()
            {
                ConfigurationGeneration = 7, PlanEpoch = 19, ActionId = id, HostSessionIncarnation = 23,
                TargetId = id, SoftwareId = id, ProcessStartKey = id, ProcessId = id
            },
            Scope = (uint)NativeTransactionJournalScope.Process,
            Disposition = (uint)NativeTransactionJournalDisposition.Apply,
            DomainMask = (uint)NativeTransactionJournalDomain.Process,
            GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1, StableSystemStatus = 100,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = id, PayloadGeneration = 3, PayloadLength = 512,
            PayloadDigestLow = 0x1234, PayloadDigestHigh = 0x5678,
            PayloadProvenanceDigestLow = 0x9abc, PayloadProvenanceDigestHigh = 0xdef0,
            NowUtcMilliseconds = 100 + id, MaximumRecoveryAttempts = 3,
            RecoveryDeadlineUtcMilliseconds = 10000 + id
        };
        Assert.Equal(NativeTransactionJournalStatus.Ok, session.Prepare(input));
    }

    private static NativeSmartCoordinatorInputRow ProcessFact(uint id) => new()
    {
        StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
        MetricKind = NativeSmartCoordinatorMetricKind.None,
        SoftwareKind = NativeSmartCoordinatorSoftwareKind.GeneralApplication,
        ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity |
            NativeSmartCoordinatorInputValidity.SoftwareIdentity |
            NativeSmartCoordinatorInputValidity.SoftwareKind |
            NativeSmartCoordinatorInputValidity.BaseScore |
            NativeSmartCoordinatorInputValidity.SurfaceFacts |
            NativeSmartCoordinatorInputValidity.Eligibility |
            NativeSmartCoordinatorInputValidity.Protection |
            NativeSmartCoordinatorInputValidity.CpuCapabilities |
            NativeSmartCoordinatorInputValidity.GpuCapabilities |
            NativeSmartCoordinatorInputValidity.AppliedProcessGrade |
            NativeSmartCoordinatorInputValidity.AppliedCpuGrade |
            NativeSmartCoordinatorInputValidity.AppliedGpuGrade,
        Flags = NativeSmartCoordinatorInputFlags.Running,
        TargetKey = id, SoftwareKey = id + 100UL, ProcessStartKey = id + 1000UL,
        BaseScore = 20, SourceIndex = id - 1, ProcessId = id,
        AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
        AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
        AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
    };

    private static NativeReportRuleInput ReportRule(ulong id) => new()
    {
        StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportRuleInput>(),
        RuleHandle = id, RuleGeneration = 1, SourceHandle = 11, CoverageScopeHandle = 13,
        FamilyHandle = id + 100, ReportTypeHandle = 32, ResourceKindHandle = 33, PayloadHandle = 34,
        MetricSelector = (uint)NativeReportMetricSelector.Current,
        Comparison = (uint)NativeReportComparison.GreaterOrEqual, Priority = 10, Severity = 20,
        RequiredConsecutiveHits = 2, RequiredConsecutiveMisses = 2, MinimumSampleDurationMilliseconds = 1,
        ActivationThreshold = 50, ClearThreshold = 40, PredicateGroupHandle = id, PredicateCount = 1
    };

    private static NativeReportFactInput RollingFact(ulong target, ulong sequence, double delta) => new()
    {
        StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeReportFactInput>(),
        Flags = (uint)(NativeReportFactFlags.DeltaValid | NativeReportFactFlags.PeakValid),
        RuleHandle = 21, RuleGeneration = 1, TargetHandle = target, EvidencePayloadHandle = 42,
        FactSequence = sequence, DeltaValue = delta, PeakValue = delta, SampleDurationMilliseconds = 1
    };
}
