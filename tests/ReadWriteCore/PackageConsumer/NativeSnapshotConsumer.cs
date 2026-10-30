using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Native;

internal static class NativeSnapshotConsumer
{
    internal static void Run()
    {
        var configuration = Configuration();
        using var session = new NativeMetricSnapshotSession(in configuration);
        Require(NativeMetricSnapshotSession.GetAbiVersion() == NativeMetricSnapshotAbi.Version
            && NativeMetricSnapshotSession.GetWireContractFingerprint() == NativeMetricSnapshotAbi.WireContractFingerprint,
            "actual packaged native ABI and wire contract");
        NativeMetricSnapshotSourcePolicyInput[] sources = [new()
        {
            StructSize = Size<NativeMetricSnapshotSourcePolicyInput>(), SourceHandle = 1,
            SourceRole = (uint)NativeMetricSnapshotSourceRole.Metrics, Priority = 100,
            CapabilityMask = 1, SemanticFingerprint = 11
        }];
        NativeMetricSnapshotMetricDefinitionInput[] rules = [new()
        {
            StructSize = Size<NativeMetricSnapshotMetricDefinitionInput>(), RuleHandle = 1,
            MetricHandle = 1, SourceHandle = 1, ScopeHandle = 1, CapabilityMask = 1,
            MetricKind = (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
            ScopeKind = (uint)NativeMetricSnapshotScopeKind.Host,
            ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64,
            SourcePriority = 100, MinimumValueBits = 0, MaximumValueBits = ulong.MaxValue, SemanticFingerprint = 22
        }];
        var catalog = new NativeMetricSnapshotCatalogReplaceInput
        {
            AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCatalogReplaceInput>(),
            ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = 1, CommandAtMilliseconds = 1_000,
            SourceCount = 1, RuleCount = 1, MetricCount = 1,
            SourceIndexCapacity = session.Capacity.SourceIndexCapacity,
            MetricIndexCapacity = session.Capacity.MetricIndexCapacity,
            RuleIndexCapacity = session.Capacity.RuleIndexCapacity,
            SemanticFingerprint = NativeMetricSnapshotSession.CalculateCatalogFingerprint(sources, rules)
        };
        Ok(session.ReplaceCatalog(in catalog, sources, rules));
        ulong operation = 1;
        for (ulong sequence = 1; sequence <= 4; sequence++)
        {
            var previous = Read(session);
            var plan = Plan(session, ++operation, sequence);
            var failed = sequence == 3;
            var value = sequence == 2 ? ulong.MaxValue : sequence == 4 ? 42UL : 0UL;
            var completion = new NativeMetricSnapshotCompletionHeader
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCompletionHeader>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = ++operation,
                PlanEpoch = sequence, SourceHandle = 1, SourceIncarnation = 1,
                SourceObservationSequence = sequence, CapabilityGeneration = 1,
                PlanTokenFingerprint = plan.PlanTokenFingerprint,
                CommandAtMilliseconds = sequence * 1_000, CapturedAtMilliseconds = sequence * 1_000,
                RequestedRuleCount = 1, ObservationCount = failed ? 0U : 1U,
                Status = (uint)(failed ? NativeMetricSnapshotSourceStatus.Unavailable : NativeMetricSnapshotSourceStatus.Complete)
            };
            var beginStatus = session.BeginCompletion(in completion);
            Require(beginStatus == NativeMetricSnapshotStatus.Ok,
                $"begin sequence {sequence}: {beginStatus}; requested {plan.RequestedRuleCount}, status {completion.Status}, observations {completion.ObservationCount}");
            NativeMetricSnapshotRequestedMetricInput[] requested =
                [new() { StructSize = Size<NativeMetricSnapshotRequestedMetricInput>(), RuleHandle = 1 }];
            var requestedStatus = session.SubmitRequested(requested);
            Require(requestedStatus == NativeMetricSnapshotStatus.Ok, $"requested sequence {sequence}: {requestedStatus}");
            if (!failed)
            {
                NativeMetricSnapshotObservationInput[] observations = [new()
                {
                    StructSize = Size<NativeMetricSnapshotObservationInput>(), RuleHandle = 1, MetricHandle = 1,
                    SourceHandle = 1, ScopeHandle = 1, SourceObservationSequence = sequence,
                    ObservedAtMilliseconds = sequence * 1_000, ValueBits = value, CapabilityMask = 1,
                    ValidMask = (ulong)(NativeMetricSnapshotObservationValidity.Required | NativeMetricSnapshotObservationValidity.Value),
                    Status = (uint)NativeMetricSnapshotObservationStatus.Current,
                    ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64
                }];
                var observationStatus = session.SubmitObservations(observations);
                Require(observationStatus == NativeMetricSnapshotStatus.Ok, $"observation sequence {sequence}: {observationStatus}");
            }
            var during = Read(session);
            Require(during.CommittedGeneration == previous.CommittedGeneration && during.ValueBits == previous.ValueBits,
                "staged source observations cannot replace committed current values");
            var finalize = new NativeMetricSnapshotFinalizeInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotFinalizeInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = operation,
                PlanEpoch = sequence, SourceHandle = 1, CommandAtMilliseconds = sequence * 1_000
            };
            Ok(session.FinalizeCompletion(in finalize));
            var committed = Read(session);
            Require(committed.CommittedGeneration > previous.CommittedGeneration,
                "source finalize advances the real committed native state");
            Require(failed ? committed.Status == (uint)NativeMetricSnapshotMetricStatus.Unavailable
                    : committed.Status == (uint)NativeMetricSnapshotMetricStatus.Current
                        && committed.ValueBits == value,
                "zero, exact UInt64 maximum, failed empty and recovery use the original native implementation");
        }
        session.Dispose();
        session.Dispose();
        try { session.QueryHeader(out _); throw new InvalidOperationException("disposed session accepted a read"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("ReadWriteCore native package consumer passed: actual native session, catalog, plan, atomic current values, exact integers, failure/recovery and release.");
    }

    private static NativeMetricSnapshotSourcePlanOutput Plan(NativeMetricSnapshotSession session, ulong operation, ulong sequence)
    {
        var input = new NativeMetricSnapshotPlanInput
        {
            AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotPlanInput>(),
            ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = operation,
            PlanEpoch = sequence, CommandAtMilliseconds = sequence * 1_000, RequestedMetricCount = 1, SourceModeCount = 1,
            SourcePlanCapacity = session.Capacity.SourcePlanCapacity, MetricPlanCapacity = session.Capacity.MetricPlanCapacity
        };
        var sources = new NativeMetricSnapshotSourcePlanOutput[session.Capacity.SourcePlanCapacity];
        var metrics = new NativeMetricSnapshotMetricPlanOutput[session.Capacity.MetricPlanCapacity];
        Ok(session.Plan(in input,
            [new() { StructSize = Size<NativeMetricSnapshotPlanMetricInput>(), MetricHandle = 1 }],
            [new() { StructSize = Size<NativeMetricSnapshotSourceStateInput>(), SourceHandle = 1, CapabilityGeneration = 1, Availability = (uint)NativeMetricSnapshotSourceAvailability.Available }],
            out var result, sources, metrics));
        Require(result.SourcePlanCount == 1 && result.MetricPlanCount == 1 && sources[0].RequestedRuleCount == 1,
            "real native plan selects the exact explicit source/rule");
        return sources[0];
    }

    private static (ulong CommittedGeneration, ulong ValueBits, uint Status) Read(NativeMetricSnapshotSession session)
    {
        Ok(session.QueryHeader(out var header));
        var input = new NativeMetricSnapshotReadInput
        {
            AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotReadInput>(),
            ConfigurationGeneration = 1, CatalogGeneration = header.CatalogGeneration,
            CatalogFingerprint = header.CatalogFingerprint, StateRevision = header.StateRevision,
            CommittedGeneration = header.CommittedGeneration
        };
        var metrics = new NativeMetricSnapshotMetricOutput[session.Capacity.MetricCapacity];
        Ok(session.Read(in input, new NativeMetricSnapshotSourceOutput[session.Capacity.SourceCapacity], metrics,
            new NativeMetricSnapshotGpuInventoryOutput[session.Capacity.GpuAdapterCapacity],
            new NativeMetricSnapshotRuleStateOutput[session.Capacity.RuleCapacity]));
        Require(header.MetricCount == 1 && metrics[0].MetricHandle == 1
            && metrics[0].ValueKind == (uint)NativeMetricSnapshotValueKind.Unsigned64,
            "cached read preserves the exact declared device field representation");
        return (header.CommittedGeneration, metrics[0].ValueBits, metrics[0].Status);
    }

    private static NativeMetricSnapshotConfiguration Configuration() => new()
    {
        AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotConfiguration>(), Generation = 1,
        MaximumSourceCount = 8, MaximumMetricCount = 32, MaximumRuleCount = 64,
        MaximumRequestedCount = 32, MaximumObservationCount = 64, MaximumGpuAdapterCount = 8,
        MaximumPersistenceSourceCount = 8, MaximumPersistenceRuleCount = 64, MaximumPersistenceGpuCount = 8,
        SourceIndexCapacity = 16, MetricIndexCapacity = 64, RuleIndexCapacity = 128, GpuIndexCapacity = 16,
        MaximumFutureSkewMilliseconds = 30_000, ResidentByteBudget = 4 * 1024 * 1024,
        CatalogContractVersion = NativeMetricSnapshotAbi.CatalogContractVersion,
        ValueContractVersion = NativeMetricSnapshotAbi.ValueContractVersion,
        ObservationContractVersion = NativeMetricSnapshotAbi.ObservationContractVersion,
        InventoryContractVersion = NativeMetricSnapshotAbi.InventoryContractVersion,
        PersistenceContractVersion = NativeMetricSnapshotAbi.PersistenceContractVersion,
        MaximumPlanMetricCount = 32, MaximumSourceModeCount = 8, MaximumSourcePlanCount = 8, MaximumMetricPlanCount = 64,
        GpuLuidIndexCapacity = 16, GpuKeyIndexCapacity = 16
    };
    private static uint Size<T>() where T : unmanaged => checked((uint)Unsafe.SizeOf<T>());
    private static void Ok(NativeMetricSnapshotStatus status) => Require(status == NativeMetricSnapshotStatus.Ok, "native call: " + status);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
