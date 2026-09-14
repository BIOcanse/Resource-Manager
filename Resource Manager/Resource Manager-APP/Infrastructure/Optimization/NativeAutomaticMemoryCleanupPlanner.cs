using System.Buffers;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class NativeAutomaticMemoryCleanupPlanner(
    HostManagerMemoryCleanupRuntime runtime) : IAutomaticMemoryCleanupPlanner
{
    private readonly object sync = new();
    private IntPtr handle;
    private MemoryCleanupRuntimePlan? appliedPlan;
    private int outstandingReservationCount;
    private bool disposed;

    public AutomaticMemoryCleanupPlanResult Plan(AutomaticMemoryCleanupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);
        lock (sync)
        {
            ThrowIfDisposed();
            if (outstandingReservationCount != 0)
            {
                throw new InvalidOperationException(
                    "Automatic memory cleanup reservations must be completed before planning another batch.");
            }

            EnsureSession();
            var configurationGeneration = appliedPlan!.HotPublish.ConfigurationGeneration;
            if (request.Candidates.Count == 0)
            {
                return new([], StateRevision: 0, configurationGeneration);
            }

            var header = new NativeMemoryCleanupPlanHeader
            {
                AbiVersion = NativeMemoryCleanupAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupPlanHeader>()),
                InputStructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupCandidateInput>()),
                OutputStructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupDecisionOutput>()),
                InputCount = checked((uint)request.Candidates.Count),
                OutputCapacity = 0,
                RequestFlags = ToNativeFlags(request.Kind),
                OrdinaryMemoryFreeRatio = request.OrdinaryMemoryFreeRatio,
                PhysicalMemoryFreeRatio = request.PhysicalMemoryFreeRatio,
                VirtualMemoryFreeRatio = request.VirtualMemoryFreeRatio,
                ConfigGeneration = configurationGeneration
            };
            EnsureSuccess(
                (NativeCoreResultCode)NativeCoreLibrary.GetRequiredMemoryCleanupOutputCapacity(
                    handle,
                    header,
                    checked((uint)request.Candidates.Count),
                    out var requiredOutputCapacity),
                "required-output-capacity");
            if (requiredOutputCapacity == 0)
            {
                return new([], StateRevision: 0, configurationGeneration);
            }

            var maximumOutputCount = checked((int)requiredOutputCapacity);
            var inputBuffer = ArrayPool<NativeMemoryCleanupCandidateInput>.Shared.Rent(request.Candidates.Count);
            var outputBuffer = ArrayPool<NativeMemoryCleanupDecisionOutput>.Shared.Rent(maximumOutputCount);
            try
            {
                for (var index = 0; index < request.Candidates.Count; index++)
                {
                    inputBuffer[index] = CreateInput(request.Candidates[index]);
                }

                header.OutputCapacity = requiredOutputCapacity;
                NativeCoreResultCode code;
                unsafe
                {
                    fixed (NativeMemoryCleanupCandidateInput* inputs = inputBuffer)
                    fixed (NativeMemoryCleanupDecisionOutput* outputs = outputBuffer)
                    {
                        code = (NativeCoreResultCode)NativeCoreLibrary.PlanMemoryCleanup(
                            handle,
                            ref header,
                            inputs,
                            checked((uint)request.Candidates.Count),
                            outputs,
                            checked((uint)maximumOutputCount));
                    }
                }

                EnsureSuccess(code, "plan");
                if (header.OutputCount == 0)
                {
                    return new AutomaticMemoryCleanupPlanResult(
                        [],
                        header.StateRevision,
                        header.ConfigGeneration);
                }

                if (header.OutputCount > maximumOutputCount)
                {
                    throw new InvalidDataException(
                        "Native memory cleanup planner exceeded the supplied output capacity.");
                }
                var decisions = new AutomaticMemoryCleanupDecision[header.OutputCount];
                for (var index = 0; index < decisions.Length; index++)
                {
                    var native = outputBuffer[index];
                    if (native.SourceInputIndex >= request.Candidates.Count)
                    {
                        throw new InvalidDataException(
                            "Native memory cleanup planner returned an invalid source input index.");
                    }

                    decisions[index] = new AutomaticMemoryCleanupDecision(
                        checked((int)native.SourceInputIndex),
                        request.Candidates[checked((int)native.SourceInputIndex)],
                        ProjectMode(native.Mode),
                        new AutomaticMemoryCleanupReservation(
                            native.StateSlot,
                            native.StateGeneration));
                }

                outstandingReservationCount = decisions.Length;
                return new AutomaticMemoryCleanupPlanResult(
                    decisions,
                    header.StateRevision,
                    header.ConfigGeneration);
            }
            finally
            {
                ArrayPool<NativeMemoryCleanupCandidateInput>.Shared.Return(inputBuffer, clearArray: true);
                ArrayPool<NativeMemoryCleanupDecisionOutput>.Shared.Return(outputBuffer, clearArray: true);
            }
        }
    }

    internal static AutomaticMemoryCleanupMode ProjectMode(NativeMemoryCleanupMode mode)
        => mode switch
        {
            NativeMemoryCleanupMode.Normal => AutomaticMemoryCleanupMode.Normal,
            NativeMemoryCleanupMode.Emergency => AutomaticMemoryCleanupMode.Emergency,
            _ => throw new InvalidDataException(
                $"The native memory cleanup planner returned unknown mode {(uint)mode}.")
        };

    public void Complete(IReadOnlyList<AutomaticMemoryCleanupFeedback> feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        lock (sync)
        {
            ThrowIfDisposed();
            if (outstandingReservationCount == 0)
            {
                if (feedback.Count == 0) return;
                throw new InvalidOperationException("No automatic memory cleanup reservations are outstanding.");
            }

            if (feedback.Count != outstandingReservationCount)
            {
                throw new InvalidOperationException(
                    "Every automatic memory cleanup reservation requires exactly one feedback record.");
            }

            var buffer = ArrayPool<NativeMemoryCleanupFeedbackInput>.Shared.Rent(feedback.Count);
            try
            {
                for (var index = 0; index < feedback.Count; index++)
                {
                    var item = feedback[index];
                    buffer[index] = new NativeMemoryCleanupFeedbackInput
                    {
                        StructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupFeedbackInput>()),
                        StateSlot = item.Reservation.StateSlot,
                        StateGeneration = item.Reservation.StateGeneration,
                        Flags = (item.Attempted ? NativeMemoryCleanupFeedbackFlags.Attempted : 0)
                            | (item.Succeeded ? NativeMemoryCleanupFeedbackFlags.Succeeded : 0)
                    };
                }

                NativeCoreResultCode code;
                unsafe
                {
                    fixed (NativeMemoryCleanupFeedbackInput* pointer = buffer)
                    {
                        code = (NativeCoreResultCode)NativeCoreLibrary.CompleteMemoryCleanup(
                            handle,
                            pointer,
                            checked((uint)feedback.Count));
                    }
                }
                EnsureSuccess(code, "complete");
                outstandingReservationCount = 0;
            }
            finally
            {
                ArrayPool<NativeMemoryCleanupFeedbackInput>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (handle == IntPtr.Zero)
            {
                return;
            }
            EnsureSuccess((NativeCoreResultCode)NativeCoreLibrary.ResetMemoryCleanup(handle), "reset");
            outstandingReservationCount = 0;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (handle != IntPtr.Zero)
            {
                NativeCoreLibrary.DestroyMemoryCleanup(handle);
                handle = IntPtr.Zero;
            }
            disposed = true;
        }
    }

    private void EnsureSession()
    {
        var desired = runtime.CaptureDesired();
        if (handle == IntPtr.Zero)
        {
            CreateSession(desired);
            return;
        }

        if (appliedPlan!.HostPlan.DeploymentDigests.MemoryCleanup.RecreateSha256
            != desired.HostPlan.DeploymentDigests.MemoryCleanup.RecreateSha256)
        {
            return;
        }

        if (appliedPlan.HotPublish.ConfigurationGeneration == desired.HotPublish.ConfigurationGeneration)
        {
            return;
        }

        if (!runtime.CanApplyHot(desired.HostPlan))
        {
            throw new InvalidOperationException(
                "The desired automatic memory cleanup plan cannot be hot-published.");
        }

        var attempt = runtime.BeginHotPublish(desired.HostPlan);
        try
        {
            EnsureAbi();
            var config = CreateConfig(desired);
            var result = (NativeCoreResultCode)NativeCoreLibrary.ReconfigureMemoryCleanup(handle, config);
            EnsureSuccess(result, "reconfigure");
            _ = runtime.CompleteSucceeded(attempt);
            appliedPlan = desired;
        }
        catch (Exception exception)
        {
            Exception? settlementException = null;
            try
            {
                _ = runtime.CompleteFailed(
                    attempt,
                    "memory-cleanup-hot-publish-failed",
                    ToNativeResult(exception));
            }
            catch (Exception settlement)
            {
                settlementException = settlement;
            }
            if (settlementException is not null)
            {
                throw new AggregateException(
                    "The memory-cleanup hot publish and deployment settlement both failed.",
                    exception,
                    settlementException);
            }
            throw;
        }
    }

    private void CreateSession(MemoryCleanupRuntimePlan desired)
    {
        var attempt = runtime.BeginInitialCreate(desired.HostPlan);
        try
        {
            EnsureAbi();
            var config = CreateConfig(desired);
            var result = (NativeCoreResultCode)NativeCoreLibrary.CreateMemoryCleanup(config, out handle);
            EnsureSuccess(result, "create");
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Native memory cleanup planner returned an empty handle.");
            }
            _ = runtime.CompleteSucceeded(attempt);
            appliedPlan = desired;
        }
        catch (Exception exception)
        {
            Exception? cleanupException = null;
            Exception? settlementException = null;
            try
            {
                if (handle != IntPtr.Zero)
                {
                    NativeCoreLibrary.DestroyMemoryCleanup(handle);
                }
            }
            catch (Exception cleanup)
            {
                cleanupException = cleanup;
            }
            finally
            {
                handle = IntPtr.Zero;
                try
                {
                    _ = runtime.CompleteFailed(
                        attempt,
                        "memory-cleanup-initial-create-failed",
                        ToNativeResult(exception));
                }
                catch (Exception settlement)
                {
                    settlementException = settlement;
                }
            }
            if (cleanupException is not null || settlementException is not null)
            {
                var failures = new List<Exception> { exception };
                if (cleanupException is not null) failures.Add(cleanupException);
                if (settlementException is not null) failures.Add(settlementException);
                throw new AggregateException(
                    "The memory-cleanup planner failed to initialize and cleanup or deployment settlement also failed.",
                    failures);
            }
            throw;
        }
    }

    private static void EnsureAbi()
    {
        if (NativeCoreLibrary.GetMemoryCleanupAbiVersion() != NativeMemoryCleanupAbi.Version)
        {
            throw new InvalidOperationException("Native memory cleanup ABI does not match the Host plan.");
        }
    }

    private static HostManagerNativeResultSnapshot? ToNativeResult(Exception exception)
        => exception is NativeMemoryCleanupException native
            ? new HostManagerNativeResultSnapshot("memory-cleanup", (long)native.ResultCode)
            : null;

    private static NativeMemoryCleanupConfig CreateConfig(MemoryCleanupRuntimePlan plan)
    {
        var hot = plan.HotPublish;
        return new NativeMemoryCleanupConfig
        {
            AbiVersion = NativeMemoryCleanupAbi.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupConfig>()),
            Generation = hot.ConfigurationGeneration,
            StateCapacity = checked((uint)plan.Recreate.StateCapacity),
            CriticalFreeRatio = hot.CriticalFreeRatio,
            VeryLowFreeRatio = hot.VeryLowFreeRatio,
            LowFreeRatio = hot.LowFreeRatio,
            GuardedFreeRatio = hot.GuardedFreeRatio,
            PhysicalEmergencyFreeRatio = hot.PhysicalEmergencyFreeRatio,
            VirtualEmergencyFreeRatio = hot.VirtualEmergencyFreeRatio,
            HighTierMinimumBaseScore = hot.HighTierMinimumBaseScore,
            CriticalBatchCount = checked((uint)hot.CriticalBatchCount),
            VeryLowBatchCount = checked((uint)hot.VeryLowBatchCount),
            LowBatchCount = checked((uint)hot.LowBatchCount),
            GuardedBatchCount = checked((uint)hot.GuardedBatchCount),
            EmergencyBatchCount = checked((uint)hot.EmergencyBatchCount)
        };
    }

    private static NativeMemoryCleanupCandidateInput CreateInput(AutomaticMemoryCleanupCandidate candidate)
    {
        return new NativeMemoryCleanupCandidateInput
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeMemoryCleanupCandidateInput>()),
            Flags = candidate.CanApply
                ? NativeMemoryCleanupInputFlags.CanApply
                : NativeMemoryCleanupInputFlags.None,
            ProcessId = checked((uint)candidate.ProcessId),
            RuntimeState = checked((uint)HostManagerRuntimeStateCodec.Encode(candidate.RuntimeState)),
            ProcessStartKey = checked((ulong)candidate.ProcessStartedAt.ToFileTime()),
            TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(candidate.TargetId),
            BaseScore = candidate.BaseScore,
            CpuScore = candidate.CpuScore,
            MemoryUsedPercent = candidate.MemoryUsedPercent
        };
    }

    private static NativeMemoryCleanupRequestFlags ToNativeFlags(AutomaticMemoryCleanupRequestKind kind)
    {
        var flags = NativeMemoryCleanupRequestFlags.None;
        if ((kind & AutomaticMemoryCleanupRequestKind.Normal) != 0) flags |= NativeMemoryCleanupRequestFlags.AllowNormal;
        if ((kind & AutomaticMemoryCleanupRequestKind.EvaluateEmergency) != 0) flags |= NativeMemoryCleanupRequestFlags.EvaluateEmergency;
        if ((kind & AutomaticMemoryCleanupRequestKind.ForceEmergency) != 0) flags |= NativeMemoryCleanupRequestFlags.ForceEmergency;
        return flags;
    }

    private static void EnsureSuccess(NativeCoreResultCode code, string operation)
    {
        if (code != NativeCoreResultCode.Ok)
        {
            throw new NativeMemoryCleanupException(operation, code);
        }
    }

    private sealed class NativeMemoryCleanupException(
        string operation,
        NativeCoreResultCode resultCode)
        : InvalidOperationException($"Native automatic memory cleanup {operation} failed: {resultCode}.")
    {
        public NativeCoreResultCode ResultCode { get; } = resultCode;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
