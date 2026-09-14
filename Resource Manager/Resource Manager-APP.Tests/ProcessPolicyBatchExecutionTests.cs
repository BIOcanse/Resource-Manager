using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessPolicyBatchExecutionTests
{
    [Fact]
    public void RecoveryReadDistinguishesLiveAndMissingProcessInstances()
    {
        var writer = CreateWriter();

        var live = writer.ReadProcessInstanceForRecovery(Environment.ProcessId);
        Assert.Equal(RecoveryReadStatus.Found, live.Status);
        Assert.NotNull(live.Value);
        Assert.Equal(Environment.ProcessId, live.Value.ProcessId);
        Assert.True(live.Value.StartedAt.ToFileTime() > 0);

        var missing = writer.ReadProcessInstanceForRecovery(int.MaxValue);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, missing.Status);
        Assert.Null(missing.Value);
    }

    [Fact]
    public void NativeAbiKeepsVersionTwoFixedLayoutsAndCallableEntryPoint()
    {
        Assert.Equal(0x0002_0000U, NativeProcessPolicyBatchAbi.Version);
        Assert.Equal(32, Unsafe.SizeOf<NativeProcessPolicyBatchHeader>());
        Assert.Equal(48, Unsafe.SizeOf<NativeProcessPolicyBatchItem>());
        Assert.Equal(48, Unsafe.SizeOf<NativeProcessPolicyBatchItemResult>());
        Assert.Equal(
            28,
            Marshal.OffsetOf<NativeProcessPolicyBatchItem>(
                nameof(NativeProcessPolicyBatchItem.ExpectedMemoryPriority)).ToInt32());
        Assert.Equal(
            44,
            Marshal.OffsetOf<NativeProcessPolicyBatchItemResult>(
                nameof(NativeProcessPolicyBatchItemResult.ObservedMemoryPriority)).ToInt32());
        Assert.Equal(0xE001_0001U, NativeProcessPolicyBatchAbi.MemoryPriorityConflictError);

        var legacyRequest = new ProcessResourcePolicyBatchRequest(
            42,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null);
        legacyRequest.Deconstruct(
            out var legacyProcessId,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);
        Assert.Equal(42, legacyProcessId);
        Assert.Null(legacyRequest.ExpectedMemoryPriority);

        var legacyField = new ProcessResourcePolicyBatchFieldResult(
            ProcessResourcePolicyBatchFields.MemoryPriority,
            Succeeded: true,
            Message: "ok");
        var (_, legacySucceeded, _) = legacyField;
        Assert.True(legacySucceeded);
        Assert.Equal(ProcessResourcePolicyBatchFieldStatus.Succeeded, legacyField.Status);

        var writer = CreateWriter();
        var result = Assert.Single(writer.TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                int.MaxValue,
                null,
                PriorityClass: nameof(ProcessPriorityClass.BelowNormal))
        ]));
        var priority = Assert.Single(result.Fields);
        Assert.Equal(ProcessResourcePolicyBatchFields.PriorityClass, priority.Field);
        Assert.False(priority.Succeeded);
        Assert.NotEmpty(priority.Message);
    }

    [Fact]
    public void NativeMemoryPriorityConflictMapsToTypedPublicResult()
    {
        var result = WindowsProcessResourcePolicyWriter.MapMemoryPriorityFieldResult(
            new NativeProcessPolicyBatchItem
            {
                ExpectedMemoryPriority = 5
            },
            new NativeProcessPolicyBatchItemResult
            {
                RequestedFields = NativeProcessPolicyFields.MemoryPriority,
                MemoryError = NativeProcessPolicyBatchAbi.MemoryPriorityConflictError,
                ObservedMemoryPriority = 3
            });

        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);
        Assert.Equal(ProcessResourcePolicyBatchFieldStatus.Conflict, result.Status);
        Assert.Contains("预期 5", result.Message, StringComparison.Ordinal);
        Assert.Contains("实际 3", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchWriterRejectsMemoryPriorityWithoutExactPreconditionsBeforeNativeCall()
    {
        var writer = CreateWriter();

        var missingExpected = Assert.Single(writer.TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                MemoryPriority: 3)
        ]));
        var missingIdentity = Assert.Single(writer.TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                Environment.ProcessId,
                null,
                MemoryPriority: 3,
                ExpectedMemoryPriority: 5)
        ]));
        var expectedWithoutTarget = Assert.Single(writer.TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                ExpectedMemoryPriority: 5)
        ]));
#pragma warning disable CS0618
        var legacyDirectWrite = writer.TrySetMemoryPriority(Environment.ProcessId, 3);
#pragma warning restore CS0618

        Assert.False(Assert.Single(missingExpected.Fields).Succeeded);
        Assert.Contains("expected-current", missingExpected.Fields[0].Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(missingIdentity.Fields).Succeeded);
        Assert.Contains("进程开始时间", missingIdentity.Fields[0].Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(expectedWithoutTarget.Fields).Succeeded);
        Assert.Contains("同时提供", expectedWithoutTarget.Fields[0].Message, StringComparison.Ordinal);
        Assert.False(legacyDirectWrite.Succeeded);
        Assert.Contains("expected-current", legacyDirectWrite.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchWriterChangesAndRestoresPriorityOnOwnedChildProcess()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Unable to start the process policy test target.");
        try
        {
            var writer = CreateWriter();
            var snapshot = Assert.IsType<ProcessResourcePolicySnapshot>(writer.TryReadProcess(process.Id));
            Assert.True(snapshot.MemoryPriority.HasValue);
            Assert.True(snapshot.PowerThrottlingControlMask.HasValue);
            Assert.True(snapshot.PowerThrottlingStateMask.HasValue);
            var targetAffinity = snapshot.ProcessorAffinityMask & -snapshot.ProcessorAffinityMask;
            const uint targetMemoryPriority = 3;
            var targetPowerControl = snapshot.PowerThrottlingControlMask.Value | 1u;
            var targetPowerState = snapshot.PowerThrottlingStateMask.Value | 1u;
            var staleIdentityResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    snapshot.StartedAt?.AddMinutes(-2),
                    PriorityClass: nameof(ProcessPriorityClass.BelowNormal))
            ]));
            Assert.False(Assert.Single(staleIdentityResult.Fields).Succeeded);
            process.Refresh();
            Assert.Equal(Enum.Parse<ProcessPriorityClass>(snapshot.PriorityClass), process.PriorityClass);

            var applyResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    snapshot.StartedAt,
                    PriorityClass: nameof(ProcessPriorityClass.BelowNormal),
                    AffinityMask: targetAffinity,
                    MemoryPriority: targetMemoryPriority,
                    ExpectedMemoryPriority: snapshot.MemoryPriority,
                    PowerControlMask: targetPowerControl,
                    PowerStateMask: targetPowerState,
                    TrimWorkingSet: true)
            ]));

            Assert.Equal(5, applyResult.Fields.Count);
            Assert.All(applyResult.Fields, static field => Assert.True(field.Succeeded, field.Message));
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);
            var appliedSnapshot = Assert.IsType<ProcessResourcePolicySnapshot>(writer.TryReadProcess(process.Id));
            Assert.Equal(targetAffinity, appliedSnapshot.ProcessorAffinityMask);
            Assert.Equal(targetMemoryPriority, appliedSnapshot.MemoryPriority);
            Assert.Equal(targetPowerControl, appliedSnapshot.PowerThrottlingControlMask);
            Assert.Equal(targetPowerState, appliedSnapshot.PowerThrottlingStateMask);

            var restoreResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    snapshot.StartedAt,
                    PriorityClass: snapshot.PriorityClass,
                    AffinityMask: snapshot.ProcessorAffinityMask,
                    MemoryPriority: snapshot.MemoryPriority,
                    ExpectedMemoryPriority: targetMemoryPriority,
                    PowerControlMask: snapshot.PowerThrottlingControlMask,
                    PowerStateMask: snapshot.PowerThrottlingStateMask)
            ]));
            Assert.Equal(4, restoreResult.Fields.Count);
            Assert.All(restoreResult.Fields, static field => Assert.True(field.Succeeded, field.Message));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task BatchWriterLowersMemoryPriorityAndReleasesOwnedChildWorkingSet()
    {
        const long allocationBytes = 128L * 1024 * 1024;
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$buffer = New-Object byte[] 134217728; " +
            "for ($index = 0; $index -lt $buffer.Length; $index += 4096) { $buffer[$index] = 1 }; " +
            "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); " +
            "$command = [Console]::In.ReadLine(); " +
            "if ($command -eq 'TOUCH') { " +
            "$timer = [Diagnostics.Stopwatch]::StartNew(); [int64]$sum = 0; " +
            "for ($index = 0; $index -lt $buffer.Length; $index += 4096) { $sum += $buffer[$index] }; " +
            "$timer.Stop(); [Console]::Out.WriteLine(('TOUCHED:{0}:{1}' -f $timer.ElapsedTicks, $sum)); " +
            "[Console]::Out.Flush() }; " +
            "[Console]::In.ReadLine() | Out-Null; [GC]::KeepAlive($buffer)");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the memory optimization test target.");
        var writer = CreateWriter();
        var productionPolicy = LoadDefaultMemoryModePolicy();
        var targetMemoryPriority = productionPolicy.OptimizeMemoryPriority;
        Assert.Equal(3U, targetMemoryPriority);
        ProcessResourcePolicySnapshot? original = null;
        var memoryPriorityRestored = false;
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("READY", ready);

            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            Assert.True(
                workingSetBefore >= allocationBytes / 2,
                $"The child did not make its allocation resident: {workingSetBefore} bytes.");

            original = Assert.IsType<ProcessResourcePolicySnapshot>(
                writer.TryReadProcess(process.Id));
            var originalMemoryPriority = Assert.IsType<uint>(original.MemoryPriority);
            var priorityResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    original.StartedAt,
                    MemoryPriority: targetMemoryPriority,
                    ExpectedMemoryPriority: original.MemoryPriority)
            ]));
            var priorityField = Assert.Single(priorityResult.Fields);
            Assert.Equal(ProcessResourcePolicyBatchFields.MemoryPriority, priorityField.Field);
            Assert.True(priorityField.Succeeded, priorityField.Message);
            Assert.Equal(
                targetMemoryPriority,
                writer.TryReadMemoryPriority(process.Id)?.MemoryPriority);

            var trimResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    original.StartedAt,
                    TrimWorkingSet: true)
            ]));
            var trimField = Assert.Single(trimResult.Fields);
            Assert.Equal(ProcessResourcePolicyBatchFields.TrimWorkingSet, trimField.Field);
            Assert.True(trimField.Succeeded, trimField.Message);

            var lowestWorkingSet = workingSetBefore;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(100);
                process.Refresh();
                lowestWorkingSet = Math.Min(lowestWorkingSet, process.WorkingSet64);
            }

            Assert.False(process.HasExited, "The memory optimization target exited during the measurement.");
            var releasedBytes = workingSetBefore - lowestWorkingSet;
            Assert.True(
                releasedBytes >= 48L * 1024 * 1024,
                $"Working-set trim released only {releasedBytes} bytes " +
                $"({workingSetBefore} -> {lowestWorkingSet}).");

            await process.StandardInput.WriteLineAsync("TOUCH");
            await process.StandardInput.FlushAsync();
            var touchResult = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            var touchParts = Assert.IsType<string>(touchResult).Split(':');
            Assert.Equal(3, touchParts.Length);
            Assert.Equal("TOUCHED", touchParts[0]);
            var recoveryTicks = long.Parse(touchParts[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(recoveryTicks > 0);
            Assert.Equal(
                allocationBytes / 4096,
                long.Parse(touchParts[2], System.Globalization.CultureInfo.InvariantCulture));
            process.Refresh();
            var workingSetAfterRecovery = process.WorkingSet64;
            Assert.True(
                workingSetAfterRecovery >= lowestWorkingSet + 48L * 1024 * 1024,
                $"The child did not recover its resident pages after re-access: " +
                $"{lowestWorkingSet} -> {workingSetAfterRecovery}.");

            var restoreResult = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    original.StartedAt,
                    MemoryPriority: originalMemoryPriority,
                    ExpectedMemoryPriority: targetMemoryPriority)
            ]));
            var restoreField = Assert.Single(restoreResult.Fields);
            Assert.Equal(ProcessResourcePolicyBatchFields.MemoryPriority, restoreField.Field);
            Assert.True(restoreField.Succeeded, restoreField.Message);
            Assert.Equal(
                originalMemoryPriority,
                writer.TryReadMemoryPriority(process.Id)?.MemoryPriority);
            memoryPriorityRestored = true;

            var recoveryMilliseconds = recoveryTicks * 1000D / Stopwatch.Frequency;
            Console.WriteLine(
                $"Non-adapted memory optimization: priority={targetMemoryPriority}, " +
                $"workingSetBefore={workingSetBefore}, " +
                $"workingSetAfter={lowestWorkingSet}, released={releasedBytes}, " +
                $"workingSetRecovered={workingSetAfterRecovery}, " +
                $"recoveryMilliseconds={recoveryMilliseconds:0.###}, " +
                $"priorityRestored={originalMemoryPriority}.");
        }
        finally
        {
            if (!process.HasExited
                && !memoryPriorityRestored
                && original?.MemoryPriority is uint originalMemoryPriority)
            {
                _ = writer.TryApplyBatch(
                [
                    new ProcessResourcePolicyBatchRequest(
                        process.Id,
                        original.StartedAt,
                        MemoryPriority: originalMemoryPriority,
                        ExpectedMemoryPriority: targetMemoryPriority)
                ]);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public void BatchWriterUsesCpuSetsForGroupQualifiedPlacementAndRejectsStaleIdentity()
    {
        var topology = new WindowsCpuTopologyReader(new EmptyCpuCorePerformanceOverrideStore())
            .CaptureTopology();
        var cpuSetId = topology.LogicalProcessors
            .Select(static logical => logical.CpuSetId)
            .FirstOrDefault(static id => id is > 0);
        Assert.True(cpuSetId is > 0, "Windows 11 CPU topology must expose at least one CPU Set ID.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Unable to start the CPU Set test target.");
        var writer = CreateWriter();
        var originalCpuSets = writer.TryReadProcessDefaultCpuSets(process.Id);
        var snapshot = Assert.IsType<ProcessResourcePolicySnapshot>(writer.TryReadProcess(process.Id));
        try
        {
            var stale = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    snapshot.StartedAt?.AddMinutes(-1),
                    CpuSetIds: [cpuSetId.Value])
            ]));
            var staleField = Assert.Single(stale.Fields);
            Assert.Equal(ProcessResourcePolicyBatchFields.CpuSets, staleField.Field);
            Assert.False(staleField.Succeeded);

            var applied = Assert.Single(writer.TryApplyBatch(
            [
                new ProcessResourcePolicyBatchRequest(
                    process.Id,
                    snapshot.StartedAt,
                    CpuSetIds: [cpuSetId.Value])
            ]));
            var appliedField = Assert.Single(applied.Fields);
            Assert.Equal(ProcessResourcePolicyBatchFields.CpuSets, appliedField.Field);
            Assert.True(appliedField.Succeeded, appliedField.Message);
            Assert.Equal([cpuSetId.Value], writer.TryReadProcessDefaultCpuSets(process.Id));
        }
        finally
        {
            if (originalCpuSets is not null)
            {
                _ = writer.TrySetProcessDefaultCpuSets(process.Id, originalCpuSets);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public void BatchWriterRejectsAmbiguousAffinityAndCpuSetRequest()
    {
        var writer = CreateWriter();
        var result = Assert.Single(writer.TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                Environment.ProcessId,
                null,
                AffinityMask: 1,
                CpuSetIds: [1])
        ]));

        Assert.Equal(2, result.Fields.Count);
        Assert.Contains(result.Fields, static field =>
            field.Field == ProcessResourcePolicyBatchFields.AffinityMask && !field.Succeeded);
        Assert.Contains(result.Fields, static field =>
            field.Field == ProcessResourcePolicyBatchFields.CpuSets && !field.Succeeded);
    }

    [Fact]
    public void EngineCombinesAllFieldsForOneProcessIntoOneBatchWrite()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var writer = new RecordingPolicyWriter(new ProcessResourcePolicySnapshot(
            4242,
            "batch-target",
            @"C:\Apps\batch-target.exe",
            startedAt,
            nameof(ProcessPriorityClass.High),
            15,
            4,
            MemoryPriority: 5,
            MemoryPriorityRawValue: "5",
            MemoryPriorityDisplayValue: "Normal",
            PowerThrottlingControlMask: 0,
            PowerThrottlingStateMask: 0,
            PowerThrottlingRawValue: "control=0;state=0",
            PowerThrottlingDisplayValue: "未显式控制执行速度节流"));
        var engine = new OptimizationProcessPolicyEngine(
            null!,
            null!,
            null!,
            writer,
            NullLogger.Instance);
        var actions = new[]
        {
            CreateAction(OptimizationLevel2ActionKinds.LowerProcessPriority, "BelowNormal", "BelowNormal", startedAt),
            CreateAction(OptimizationLevel2ActionKinds.EnableExecutionSpeedThrottling, "开启", "control=1;state=1", startedAt),
            CreateAction(OptimizationLevel2ActionKinds.LowerMemoryPriority, "Low", "3", startedAt),
            CreateAction(OptimizationLevel2ActionKinds.MoveProcessAffinity, "0xC", "12", startedAt)
        };

        var applied = engine.ApplyActions(actions, DateTimeOffset.UtcNow, "batch-test");

        Assert.Equal(1, writer.ReadCount);
        Assert.Equal(1, writer.BatchCallCount);
        var request = Assert.Single(writer.Requests);
        Assert.Equal(nameof(ProcessPriorityClass.BelowNormal), request.PriorityClass);
        Assert.Equal(12, request.AffinityMask);
        Assert.Equal(3u, request.MemoryPriority);
        Assert.Equal(1u, request.PowerControlMask);
        Assert.Equal(1u, request.PowerStateMask);
        Assert.Equal(actions.Select(static action => action.Kind), applied.Select(static action => action.Kind));
    }

    private static ProcessPolicyOptimizationActionPreview CreateAction(
        string kind,
        string proposedValue,
        string proposedRawValue,
        DateTimeOffset startedAt)
    {
        return new ProcessPolicyOptimizationActionPreview(
            $"action:{kind}",
            kind,
            4242,
            "batch-target",
            @"C:\Apps\batch-target.exe",
            startedAt,
            "current",
            proposedValue,
            null,
            proposedRawValue,
            true,
            true,
            null,
            []);
    }

    private static WindowsProcessResourcePolicyWriter CreateWriter()
        => new(new NativeProcessPolicyBatchExecutor(NativeProcessPolicyBatchAbi.Version, 256));

    private static HostManagerMemoryModePolicy LoadDefaultMemoryModePolicy()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var smart = plan.SmartCoordinator;
        var policy = smart.HotPublish.MemoryModePolicy;
        var binding = new HostManagerSchedulingPlanBinding(
            HostPublicationSequence: 1,
            HostPlanEpoch: plan.PlanEpoch,
            HostPlanSha256: plan.PlanSha256,
            SmartConfigurationGeneration: smart.ConfigurationGeneration,
            SmartConfigurationSha256: smart.ConfigurationSha256,
            MemoryModePolicyEnabled: policy.Enabled,
            MemoryModePolicySourceKind: policy.SourceKind,
            MemoryModeConfigurationGeneration: policy.ConfigurationGeneration,
            MemoryModeConfigurationSha256: policy.ConfigurationSha256);
        var capture = new ProductBaselineHostManagerMemoryModePolicySource()
            .Capture(binding, smart);
        Assert.Null(capture.UnavailableReason);
        return Assert.IsType<HostManagerMemoryModePolicy>(capture.Policy);
    }

    private sealed class RecordingPolicyWriter(ProcessResourcePolicySnapshot snapshot)
        : IProcessResourcePolicyWriter
    {
        public int ReadCount { get; private set; }

        public int BatchCallCount { get; private set; }

        public IReadOnlyList<ProcessResourcePolicyBatchRequest> Requests { get; private set; } = [];

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
        {
            ReadCount++;
            return processId == snapshot.ProcessId ? snapshot : null;
        }

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int processId,
            ProcessPlacementReadFields requiredFields)
            => RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(0, "Recovery read is outside this batch test.");

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
            int processId,
            int threadId)
            => RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Unavailable(0, "Recovery read is outside this batch test.");

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            BatchCallCount++;
            Requests = requests;
            return requests.Select(static request => new ProcessResourcePolicyBatchWriteResult(
                request.ProcessId,
                CreateSuccessfulFields(request))).ToArray();
        }

        public DateTimeOffset? TryReadProcessStartedAt(int processId) => snapshot.StartedAt;

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
            int processId)
            => processId == snapshot.ProcessId && snapshot.StartedAt is not null
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, snapshot.StartedAt.Value))
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(0, "missing");

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int processId, string priorityClass) => Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int processId, long affinityMask) => Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(int processId, IReadOnlyList<uint> cpuSetIds) => Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int processId, int threadId) => null;

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds) => Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority) => Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(int processId, uint controlMask, uint stateMask) => Unexpected();

        public ProcessResourcePolicyWriteResult TryTrimWorkingSet(int processId) => Unexpected();

        private static IReadOnlyList<ProcessResourcePolicyBatchFieldResult> CreateSuccessfulFields(
            ProcessResourcePolicyBatchRequest request)
        {
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            Add(request.PriorityClass is not null, ProcessResourcePolicyBatchFields.PriorityClass);
            Add(request.AffinityMask is not null, ProcessResourcePolicyBatchFields.AffinityMask);
            Add(request.MemoryPriority is not null, ProcessResourcePolicyBatchFields.MemoryPriority);
            Add(request.PowerControlMask is not null, ProcessResourcePolicyBatchFields.PowerThrottling);
            Add(request.TrimWorkingSet, ProcessResourcePolicyBatchFields.TrimWorkingSet);
            Add(request.CpuSetIds is not null, ProcessResourcePolicyBatchFields.CpuSets);
            return fields;

            void Add(bool included, ProcessResourcePolicyBatchFields field)
            {
                if (included)
                {
                    fields.Add(new ProcessResourcePolicyBatchFieldResult(field, true, "ok"));
                }
            }
        }

        private static ProcessResourcePolicyWriteResult Unexpected()
        {
            throw new InvalidOperationException("The batch execution path fell back to an individual writer call.");
        }
    }

    private sealed class EmptyCpuCorePerformanceOverrideStore : ICpuCorePerformanceOverrideStore
    {
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(LoadScores(cpuName), null);

        public void SaveBaselineRatio(double ratio) => throw new NotSupportedException();

        public void ResetBaselineRatio() => throw new NotSupportedException();

        public IReadOnlyDictionary<int, double> LoadScores(string cpuName)
            => new Dictionary<int, double>();

        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request)
            => throw new InvalidOperationException("The CPU Set test never writes performance overrides.");

        public CpuCorePerformanceOverrideResult Reset(string cpuName)
            => throw new InvalidOperationException("The CPU Set test never resets performance overrides.");
    }
}
