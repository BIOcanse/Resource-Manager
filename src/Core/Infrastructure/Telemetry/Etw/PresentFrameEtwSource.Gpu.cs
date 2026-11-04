using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using Microsoft.Diagnostics.Tracing;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource
{
    private const ulong GpuObjectKeywords = 0x840; // LongHaul | Resource, manifest constants.
    private static readonly int[] GpuEventIds = [27, 28, 29, 30, 31, 32, 175, 177, 180, 422, 423, 424, 450, 451];
    private GpuFrameTracker? gpuTracker;
    private bool gpuRequested;
    private bool gpuAllProcesses;
    private HashSet<int> gpuTargets = [];
    private bool gpuRundownNeeded;
    private bool gpuEventsEnabled;
    private string? captureFailure;

    private void RefuseGpuTier(IPresentFrameEtwSession current, ProviderSelection selection, Exception error)
    {
        // Keep ordinary frame coverage when the optional tier cannot be enabled or captured. If
        // rollback also fails the normal session failure boundary owns native cleanup.
        lock (gate) gpuTracker?.Refuse(GpuFailureReason(error));
        gpuRundownNeeded = false;
        var baseline = selection with { Keywords = GraphicsKernelDisplayKeyword,
            EventIds = selection.EventIds.Except(GpuEventIds).ToArray() };
        current.EnableProvider(baseline.Provider, baseline.Level, baseline.Keywords, baseline.EventIds);
        activeRun!.Providers[baseline.Provider] = baseline;
        gpuEventsEnabled = false;
        logger.LogWarning(error, "Optional GPU frame tier unavailable: {Reason}", GpuFailureReason(error));
    }

    // Called under gate: targets changing resets correlation and requests fresh rundown, so released
    // targets cannot leave stale GPU state or donate accumulated work to a later lease.
    private bool PrepareGpuTier(FrameSubscriptionRequest[] requests)
    {
        var gpu = requests.Where(static request => (request.Detail & FrameDetail.GpuFrames) != 0
            && request.TargetProcessIds is not { Count: 0 }).ToArray();
        var all = gpu.Any(static request => request.TargetProcessIds is null);
        var targets = gpu.SelectMany(static request => request.TargetProcessIds ?? []).Where(static pid => pid > 0).ToHashSet();
        var changed = gpuRequested != (gpu.Length > 0) || gpuAllProcesses != all || !gpuTargets.SetEquals(targets);
        if (changed || (gpu.Length > 0 && gpuTracker is null))
        {
            gpuRequested = gpu.Length > 0; gpuAllProcesses = all; gpuTargets = targets;
            gpuTracker = gpuRequested ? new GpuFrameTracker(ReadProcessStartKey,
                new(Pipeline.GpuObjectCapacity, Pipeline.GpuPendingPacketCapacity, Pipeline.GpuPendingFrameCapacity),
                pid => gpuAllProcesses || gpuTargets.Contains(pid)) : null;
            gpuRundownNeeded = gpuRequested;
            if (ledger is not null) ledger.GpuFrames = gpuTracker;
        }
        return gpuRequested && gpuTracker?.Failure is null;
    }

    private bool ApplyGpuEvent(PresentFrameLedger current, TraceEvent data, EtwFixedPayload payload)
    {
        if (gpuTracker is not { } tracker) return false;
        if (data.ProviderGuid == KernelProcessProvider && (int)data.ID is ProcessStart or ProcessStop)
        {
            tracker.ProcessStopped(payload.Int32((int)PresentFrameField.ProcessID), data.TimeStamp.ToUniversalTime().Ticks);
            return false;
        }
        if (data.ProviderGuid != GraphicsKernelProvider) return false;
        var id = (int)data.ID;
        var ticks = data.TimeStamp.ToUniversalTime().Ticks;
        ulong Pointer(PresentFrameField field) => payload.Pointer((int)field);
        switch (id)
        {
            case 27: case 29:
                var pid = Pointer(PresentFrameField.hProcessId);
                tracker.DeviceStarted(Pointer(PresentFrameField.hDevice), Pointer(PresentFrameField.pDxgAdapter),
                    pid <= int.MaxValue ? (int)pid : 0);
                break;
            case 28: tracker.DeviceStopped(Pointer(PresentFrameField.hDevice)); break;
            case 30: case 32:
                tracker.ContextStarted(Pointer(PresentFrameField.hContext), Pointer(PresentFrameField.hDevice),
                    payload.UInt32((int)PresentFrameField.NodeOrdinal), id == 32 ? 0 : data.ProcessID);
                break;
            case 31: tracker.ContextStopped(Pointer(PresentFrameField.hContext)); break;
            case 175:
                tracker.DmaStarted(Pointer(PresentFrameField.hContext), payload.UInt32((int)PresentFrameField.QueueSubmitSequence), ticks);
                break;
            case 177:
                tracker.DmaCompleted(Pointer(PresentFrameField.hContext), payload.UInt32((int)PresentFrameField.QueueSubmitSequence), ticks);
                break;
            case 178:
                var type = payload.UInt32((int)PresentFrameField.PacketType);
                var present = payload.Boolean((int)PresentFrameField.bPresent) || type is 3 or 7;
                tracker.QueueStarted(Pointer(PresentFrameField.hContext), payload.UInt32((int)PresentFrameField.SubmitSequence),
                    data.ProcessID, present ? current.CurrentGpuOwner(data.ProcessID, data.ThreadID) : null);
                break;
            case 180:
                tracker.QueueStopped(Pointer(PresentFrameField.hContext), payload.UInt32((int)PresentFrameField.SubmitSequence), ticks);
                break;
            case 422: case 424: tracker.HardwareQueue(Pointer(PresentFrameField.hContext)); break;
            case 423: break; // HAGS remains unavailable for that process for the duration of this tier.
            case 450: case 451: tracker.Refuse(FrameTimingReasons.GpuHardwareScheduledQueue); break;
            default: return false;
        }
        return true;
    }

    private static string GpuFailureReason(Exception error)
        => NativeEtwSessionAvailability.Classify(error) == NativeEtwSessionUnavailableCause.Capacity
            ? FrameTimingReasons.GpuEtwProviderSessionLimit : FrameTimingReasons.GpuEtwEnableFailed;
}
