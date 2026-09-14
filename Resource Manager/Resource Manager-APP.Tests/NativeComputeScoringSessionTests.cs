using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Theory]
    [InlineData(1.2, 60)]
    [InlineData(1.4, 70)]
    public void FreedomPointStateMultiplierReachesTheActualNativeCpuScore(double multiplier, double expectedScore)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]!["process_policy"]!["state_multipliers"]![1] = multiplier);
        var configuration = HostManagerComputeScoringConfigurationFactory.Create(plan.SmartCoordinator, plan.CpuScoring!);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        process.RuntimeState = NativeComputeScoringRuntimeState.ForegroundFocused;
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.ConfigurationGeneration = configuration.Generation;
        var snapshot = default(NativeComputeScoringSnapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(3U, snapshot.OutputCount);
        Assert.Equal(expectedScore, outputs[0].Score, precision: 10);
        Assert.Equal(expectedScore, outputs[1].Score, precision: 10);
        Assert.Equal(multiplier, Assert.Single(plan.FreedomPoints.EnumeratePoints(),
            point => point.Id == "state_multipliers").Value!.Value[1].GetDouble());
    }

    [Theory]
    [InlineData(1D, 80D, 80D)]
    [InlineData(0.5D, 40D, 80D)]
    [InlineData(0.5D, 80D, 160D)]
    [InlineData(0.5D, 100D, 200D)]
    [InlineData(0.5D, 0D, 0D)]
    [InlineData(0.25D, 25D, 100D)]
    public void ScoresCpuUsingSharedBaselineWithoutClippingReferenceUse(
        double cpuBaselineRatio,
        double occupancy,
        double expectedScore)
    {
        var configuration = CreateConfiguration(cpuBaselineRatio);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var processes = new[]
        {
            CreateProcess(1, 10, 100, 1, occupancy)
        };
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, processes,
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(expectedScore, outputs[0].Score, precision: 10);
        Assert.Equal(expectedScore, outputs[1].Score, precision: 10);
        Assert.Equal(occupancy, processes[0].WeightedCpuUsePercent);
    }

    [Fact]
    public void ScoresCpuAsOccupancyWeightedAndSumsSameSoftwareMembers()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var processes = new[]
        {
            CreateProcess(sourceIndex: 2, processId: 20, startKey: 200, softwareKey: 1, occupancy: 0),
            CreateProcess(sourceIndex: 1, processId: 10, startKey: 100, softwareKey: 1, occupancy: 50)
        };
        var outputs = new NativeComputeScoringOutput[8];
        var envelope = CreateEnvelope(
            generation: 1,
            processCount: processes.Length,
            gpuCount: 0,
            gpuAdapterCount: 0,
            outputCapacity: outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(
            in envelope,
            processes,
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty,
            outputs,
            ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(2U, snapshot.ProcessCpuOutputCount);
        Assert.Equal(1U, snapshot.SoftwareCpuOutputCount);
        Assert.Equal(4U, snapshot.OutputCount);
        Assert.Equal(1U, snapshot.SoftwareMemoryOutputCount);
        Assert.Equal(NativeComputeScoringOutputKind.ProcessCpu, outputs[0].Kind);
        Assert.Equal(10U, outputs[0].ProcessId);
        Assert.Equal(50D, outputs[0].Score);
        Assert.Equal(20U, outputs[1].ProcessId);
        Assert.Equal(0D, outputs[1].Score);
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(outputs[1].Score));
        Assert.Equal(NativeComputeScoringOutputKind.SoftwareCpu, outputs[2].Kind);
        Assert.Equal(50D, outputs[2].Score);
        Assert.Equal(2U, outputs[2].MemberCount);
    }

    [Fact]
    public void ScoresEachStableAdapterIndependently()
    {
        var configuration = CreateConfiguration(cpuBaselineRatio: 0.5);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var processes = new[]
        {
            CreateProcess(sourceIndex: 1, processId: 10, startKey: 100, softwareKey: 1, occupancy: 20),
            CreateProcess(sourceIndex: 2, processId: 20, startKey: 200, softwareKey: 1, occupancy: 30)
        };
        var gpus = new[]
        {
            CreateGpu(processes[1], adapterKey: 22, sourceIndex: 4, occupancy: 0),
            CreateGpu(processes[0], adapterKey: 11, sourceIndex: 1, occupancy: 25),
            CreateGpu(processes[0], adapterKey: 22, sourceIndex: 2, occupancy: 50),
            CreateGpu(processes[1], adapterKey: 11, sourceIndex: 3, occupancy: 25)
        };
        var outputs = new NativeComputeScoringOutput[16];
        var envelope = CreateEnvelope(
            generation: 1,
            processCount: processes.Length,
            gpuCount: gpus.Length,
            gpuAdapterCount: 2,
            outputCapacity: outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete |
                NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, processes, gpus, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(4U, snapshot.ProcessGpuOutputCount);
        Assert.Equal(2U, snapshot.SoftwareGpuOutputCount);
        Assert.Equal(303UL, snapshot.GpuTopologyGeneration);
        Assert.Equal(40D, outputs[0].Score);
        Assert.Equal(60D, outputs[1].Score);
        Assert.Equal(100D, outputs[2].Score);
        var softwareGpuStart = checked((int)(snapshot.ProcessCpuOutputCount +
            snapshot.SoftwareCpuOutputCount + snapshot.ProcessGpuOutputCount));
        Assert.Equal(11UL, outputs[softwareGpuStart].AdapterKey);
        Assert.Equal(50D, outputs[softwareGpuStart].Score);
        Assert.Equal(22UL, outputs[softwareGpuStart + 1].AdapterKey);
        Assert.Equal(50D, outputs[softwareGpuStart + 1].Score);
    }

    [Fact]
    public void CpuWelfareIsNotDividedOrRepeatedInSoftwareSumWhileGpuKeepsItsExistingFormula()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var processes = new[]
        {
            CreateProcess(sourceIndex: 1, processId: 10, startKey: 100, softwareKey: 1, occupancy: 50),
            CreateProcess(sourceIndex: 2, processId: 20, startKey: 200, softwareKey: 1, occupancy: 0)
        };
        var gpus = new[]
        {
            CreateGpu(processes[0], adapterKey: 11, sourceIndex: 1, occupancy: 25),
            CreateGpu(processes[1], adapterKey: 11, sourceIndex: 2, occupancy: 0),
            CreateGpu(processes[0], adapterKey: 22, sourceIndex: 3, occupancy: 50)
        };
        var outputs = new NativeComputeScoringOutput[16];
        var envelope = CreateEnvelope(
            generation: 1,
            processCount: processes.Length,
            gpuCount: gpus.Length,
            gpuAdapterCount: 2,
            outputCapacity: outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete |
                NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        envelope.CpuFreeRatio = 0.5;
        envelope.GpuFreeRatio = 0.5;
        envelope.VramFreeRatio = 0.5;
        envelope.MemoryFreeRatio = 0.5;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, processes, gpus, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(0.0625, snapshot.WelfareMultiplier, precision: 10);
        Assert.Equal(0.9375, snapshot.SystemPressure, precision: 10);
        Assert.Equal(3.75D, snapshot.WelfareBudget, precision: 10);
        Assert.Equal(1.875D, snapshot.WelfareShare, precision: 10);
        Assert.Equal(2U, snapshot.WelfareEligibleProcessCount);
        var cpuBonus = 60D * (1D - 50D / 70D);
        Assert.Equal(cpuBonus, snapshot.CpuWelfareBonus, precision: 10);
        Assert.Equal(cpuBonus, snapshot.MemoryWelfareBonus, precision: 10);
        var emitted = outputs[..checked((int)snapshot.OutputCount)];
        Assert.Equal(50 + cpuBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score, precision: 10);
        Assert.Equal(cpuBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 20).Score, precision: 10);
        Assert.Equal(26.875D, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessGpu && row.ProcessId == 10 && row.AdapterKey == 11).Score);
        Assert.Equal(1.875D, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessGpu && row.ProcessId == 20 && row.AdapterKey == 11).Score);
        Assert.Equal(51.875D, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessGpu && row.ProcessId == 10 && row.AdapterKey == 22).Score);
        Assert.Equal(cpuBonus * 2, emitted.Where(static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu).Sum(static row => row.Score) - 50, precision: 10);
        var softwareCpu = Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareCpu && row.SoftwareKey == 1);
        Assert.Equal(50 + cpuBonus, softwareCpu.Score, precision: 10);
        Assert.Equal(2U, softwareCpu.MemberCount);
        Assert.Equal(50 + cpuBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareMemory).Score, precision: 10);
        var firstAdapter = Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareGpu && row.AdapterKey == 11);
        Assert.Equal(28.75, firstAdapter.Score);
        Assert.Equal(2U, firstAdapter.MemberCount);
        var secondAdapter = Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareGpu && row.AdapterKey == 22);
        Assert.Equal(51.875, secondAdapter.Score);
        Assert.Equal(1U, secondAdapter.MemberCount);
    }

    [Fact]
    public void SoftwareMeanUsesTheActualNativeEntryAndRejectsInvalidBases()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        Assert.Equal(60, session.CalculateSoftwareBaseMean([80, 40]));
        Assert.Equal(40, session.CalculateSoftwareBaseMean([80, 40, 0]));
        Assert.Equal(0, session.CalculateSoftwareBaseMean([]));
        foreach (var value in new[] { -1D, 101D, double.NaN, double.PositiveInfinity })
            Assert.Throws<InvalidDataException>(() => session.CalculateSoftwareBaseMean([60, value]));
        Assert.Equal(60, session.CalculateSoftwareBaseMean([80, 40]));
    }

    [Fact]
    public void NotRunningProcessNeitherDilutesNorReceivesWelfare()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var running = CreateProcess(1, 10, 100, 1, 50);
        var stopped = CreateProcess(2, 20, 200, 1, 50);
        stopped.RuntimeState = NativeComputeScoringRuntimeState.NotRunning;
        stopped.Flags &= ~NativeComputeScoringProcessFlags.Running;
        var outputs = new NativeComputeScoringOutput[8];
        var envelope = CreateEnvelope(1, 2, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 0.5;
        envelope.GpuFreeRatio = 0.5;
        envelope.VramFreeRatio = 0.5;
        envelope.MemoryFreeRatio = 0.5;
        envelope.WelfareEligibleProcessCount = 1;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, [running, stopped],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(1U, snapshot.WelfareEligibleProcessCount);
        Assert.Equal(3.75D, snapshot.WelfareShare);
        var emitted = outputs[..checked((int)snapshot.OutputCount)];
        Assert.Equal(50 + 60 * (1 - 50D / 70), Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score, precision: 10);
        Assert.Equal(0D, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 20).Score);
    }

    [Fact]
    public void CpuWelfareDoesNotUseTheDeclaredGlobalRunningCountForAProjectedDomain()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 0.5;
        envelope.GpuFreeRatio = 0.5;
        envelope.VramFreeRatio = 0.5;
        envelope.MemoryFreeRatio = 0.5;
        envelope.WelfareEligibleProcessCount = 2;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(1.875D, snapshot.WelfareShare);
        Assert.Equal(50 + 60 * (1 - 50D / 70), outputs[0].Score, precision: 10);
    }

    [Fact]
    public void RuntimeStateAndRunningFlagMustAgree()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        process.Flags &= ~NativeComputeScoringProcessFlags.Running;
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.InvalidFacts, status);

        process = CreateProcess(1, 10, 100, 1, 50);
        process.RuntimeState = NativeComputeScoringRuntimeState.NotRunning;
        envelope.SchedulingGeneration++;
        status = session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.InvalidFacts, status);
    }

    [Fact]
    public void SessionCreationRejectsFiniteConfigurationThatCanOverflowDeclaredScores()
    {
        var configuration = CreateConfiguration();
        configuration.MaximumBaseImportance = 1e308;

        Assert.Throws<InvalidOperationException>(() =>
            new NativeComputeScoringSession(in configuration, [1]));
    }

    [Fact]
    public void MissingVramCapacityDoesNotCancelCpuOrMemoryWelfare()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 1;
        envelope.GpuFreeRatio = 1;
        envelope.VramFreeRatio = 0;
        envelope.MemoryFreeRatio = 1;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, status);
        Assert.Equal(0D, snapshot.WelfareMultiplier);
        Assert.Equal(1D, snapshot.SystemPressure);
        Assert.Equal(0D, snapshot.WelfareBudget);
        Assert.Equal(0D, snapshot.WelfareShare);
        Assert.Equal(60D, snapshot.CpuWelfareBonus);
        Assert.Equal(60D, snapshot.MemoryWelfareBonus);
        Assert.Equal(110D, outputs[0].Score);
    }

    [Fact]
    public void WelfareRejectsRatiosOutsideTheCurrentValueDomain()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 1.01;
        envelope.GpuFreeRatio = 1;
        envelope.VramFreeRatio = 1;
        envelope.MemoryFreeRatio = 1;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.InvalidFacts, status);
    }

    [Fact]
    public void CompleteGpuScoringRejectsMissingTopologyGeneration()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var processes = new[]
        {
            CreateProcess(sourceIndex: 1, processId: 10, startKey: 100, softwareKey: 1, occupancy: 20)
        };
        var gpus = new[] { CreateGpu(processes[0], adapterKey: 11, sourceIndex: 1, occupancy: 25) };
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(
            generation: 1,
            processCount: 1,
            gpuCount: 1,
            gpuAdapterCount: 1,
            outputCapacity: outputs.Length,
            NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        envelope.GpuTopologyGeneration = 0;
        var snapshot = default(NativeComputeScoringSnapshot);

        var status = session.Score(in envelope, processes, gpus, outputs, ref snapshot);

        Assert.Equal(NativeComputeScoringStatus.InvalidFacts, status);
    }

    [Fact]
    public void ReconfigureRejectsCapacityChangesWithoutMutatingSession()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var changed = configuration;
        changed.Generation = 2;
        changed.MaximumProcessCount++;
        changed.MaximumOutputCount += 3;

        var status = session.Reconfigure(in changed, [1]);

        Assert.Equal(NativeComputeScoringStatus.RecreateRequired, status);
        Assert.Equal(1UL, session.Capacity.ConfigurationGeneration);
    }

    private static NativeComputeScoringConfiguration CreateConfiguration(
        double cpuBaselineRatio = 1, uint cpuCoreCount = 1, double welfareUtilizationBaselinePercent = 70)
    {
        ReadOnlySpan<double> multipliers = [1, 1, 1, 1, 1, 1, 0];
        return NativeComputeScoringConfigurationWriter.Create(
            generation: 1,
            maximumProcessCount: 16,
            maximumGpuRowCount: 32,
            maximumOutputCount: 112,
            multipliers,
            multipliers,
            maximumBaseImportance: 100,
            maximumPolicyMultiplier: 4,
            cpuBaselineRatio: cpuBaselineRatio,
            cpuCoreCount: cpuCoreCount,
            welfareUtilizationBaselinePercent: welfareUtilizationBaselinePercent);
    }

    private static NativeComputeScoringGenerationEnvelope CreateEnvelope(
        ulong generation,
        int processCount,
        int gpuCount,
        uint gpuAdapterCount,
        int outputCapacity,
        NativeComputeScoringEnvelopeFlags flags)
    {
        var gpuComplete = flags.HasFlag(NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        return new()
        {
            AbiVersion = NativeComputeScoringAbi.Version,
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGenerationEnvelope>(),
            ProcessInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringProcessInput>(),
            GpuInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGpuInput>(),
            OutputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringOutput>(),
            ConfigurationGeneration = 1,
            SchedulingGeneration = generation,
            ProcessSourceGeneration = 101,
            GpuSourceGeneration = gpuComplete ? 202UL : 0,
            GpuTopologyGeneration = gpuComplete ? 303UL : 0,
            GpuTopologyFingerprint = gpuComplete ? 404UL : 0,
            ProcessObservedAtMilliseconds = 1_000,
            GpuObservedAtMilliseconds = gpuComplete ? 1_001 : 0,
            ValidMask = NativeComputeScoringEnvelopeValidity.ProcessGeneration |
                NativeComputeScoringEnvelopeValidity.ProcessObservedAt |
                NativeComputeScoringEnvelopeValidity.WelfareCapacity |
                (flags.HasFlag(NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete)
                    ? NativeComputeScoringEnvelopeValidity.GpuGeneration |
                        NativeComputeScoringEnvelopeValidity.GpuObservedAt |
                        NativeComputeScoringEnvelopeValidity.GpuTopology
                    : NativeComputeScoringEnvelopeValidity.None),
            Flags = flags,
            ProcessCount = checked((uint)processCount),
            GpuRowCount = checked((uint)gpuCount),
            GpuAdapterCount = gpuAdapterCount,
            OutputCapacity = checked((uint)outputCapacity),
            WelfareEligibleProcessCount = checked((uint)processCount),
            SoftwareBaseMean = 60
        };
    }

    private static NativeComputeScoringProcessInput CreateProcess(
        uint sourceIndex,
        uint processId,
        ulong startKey,
        ulong softwareKey,
        double occupancy)
        => new()
        {
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringProcessInput>(),
            Flags = NativeComputeScoringProcessFlags.Running |
                NativeComputeScoringProcessFlags.CpuMetricsComplete |
                NativeComputeScoringProcessFlags.GpuMetricsComplete,
            ValidMask = NativeComputeScoringProcessValidity.CpuRequired,
            TargetKey = 1_000 + sourceIndex,
            SoftwareKey = softwareKey,
            ProcessStartKey = startKey,
            SourceGeneration = 101,
            BaseImportance = 100,
            CpuPolicyMultiplier = 1,
            WeightedCpuUsePercent = occupancy,
            SourceIndex = sourceIndex,
            ProcessId = processId,
            RuntimeState = NativeComputeScoringRuntimeState.Unknown
        };

    private static NativeComputeScoringGpuInput CreateGpu(
        NativeComputeScoringProcessInput process,
        ulong adapterKey,
        uint sourceIndex,
        double occupancy)
        => new()
        {
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGpuInput>(),
            ValidMask = NativeComputeScoringGpuValidity.Required,
            TargetKey = process.TargetKey,
            SoftwareKey = process.SoftwareKey,
            ProcessStartKey = process.ProcessStartKey,
            SourceGeneration = 202,
            AdapterKey = adapterKey,
            GpuPolicyMultiplier = 1,
            GpuOccupancyPercent = occupancy,
            SourceIndex = sourceIndex,
            ProcessId = process.ProcessId
        };
}
