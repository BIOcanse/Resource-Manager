using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerPlacementCoordinatorRecreatePlan CompilePlacementCoordinatorRecreate(
        HostManagerPlacementCoordinatorRecreateProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ValidateCapacity(source.MaximumDesiredCount, limits.PlacementCoordinatorMaximumDesiredCapacity, "host_recreate.placement_coordinator.maximum_desired_count");
        ValidateCapacity(source.MaximumAppliedCount, limits.PlacementCoordinatorMaximumAppliedCapacity, "host_recreate.placement_coordinator.maximum_applied_count");
        ValidateCapacity(source.MaximumActionCount, limits.PlacementCoordinatorMaximumActionCapacity, "host_recreate.placement_coordinator.maximum_action_count");
        ValidateCapacity(source.MaximumStateCount, limits.PlacementCoordinatorMaximumStateCapacity, "host_recreate.placement_coordinator.maximum_state_count");
        ValidateCapacity(source.CoreCapacity, limits.PlacementCoordinatorCoreCapacity, "host_recreate.placement_coordinator.core_capacity");
        ValidateCapacity(source.CcdCapacity, limits.PlacementCoordinatorCcdCapacity, "host_recreate.placement_coordinator.ccd_capacity");
        ValidateCapacity(source.TargetCapacity, limits.PlacementCoordinatorTargetCapacity, "host_recreate.placement_coordinator.target_capacity");
        ValidateCapacity(source.ReservationCapacity, limits.PlacementCoordinatorReservationCapacity, "host_recreate.placement_coordinator.reservation_capacity");
        if (source.MaximumDesiredCount > source.MaximumStateCount)
        {
            throw new InvalidDataException(
                "host_recreate.placement_coordinator.maximum_desired_count must not exceed maximum_state_count.");
        }
        if (source.MaximumAppliedCount > source.MaximumStateCount)
        {
            throw new InvalidDataException(
                "host_recreate.placement_coordinator.maximum_applied_count must not exceed maximum_state_count.");
        }
        if (source.MaximumActionCount != source.MaximumStateCount)
        {
            throw new InvalidDataException(
                "host_recreate.placement_coordinator.maximum_action_count must equal maximum_state_count for ABI v1.");
        }
        if (source.ReservationCapacity < source.TargetCapacity)
        {
            throw new InvalidDataException(
                "host_recreate.placement_coordinator.reservation_capacity must be at least target_capacity.");
        }

        return new CompiledHostManagerPlacementCoordinatorRecreatePlan(
            source.MaximumDesiredCount,
            source.MaximumAppliedCount,
            source.MaximumActionCount,
            source.MaximumStateCount,
            source.CoreCapacity,
            source.CcdCapacity,
            source.TargetCapacity,
            source.ReservationCapacity);
    }

    private static CompiledHostManagerPlacementCoordinatorHotPublishPlan CompilePlacementCoordinatorHotPublish(
        HostManagerPlacementCoordinatorHotPublishProfile source,
        int profileRevision,
        FreedomPointCompilation freedom)
    {
        const string consumer = "HostManagerPlanCompiler.CompilePlacementCoordinatorHotPublish";
        var timeout = freedom.Consume<int>(BackendFreedomPointPaths.PlacementActionTimeout, consumer);
        var observationWindow = freedom.Consume<int>(BackendFreedomPointPaths.GpuApiObservationWindow, consumer);
        ValidatePositive(observationWindow, BackendFreedomPointPaths.GpuApiObservationWindow);
        var window = freedom.Consume<GpuWindowExecutionLimitsDeclaration>(BackendFreedomPointPaths.GpuWindowExecutionLimits, consumer);
        ValidatePositive(source.RetryDelayMilliseconds, "hot_publish.placement_coordinator.retry_delay_ms");
        ValidatePositive(timeout, "hot_publish.placement_coordinator.action_timeout_ms");
        ValidateNonNegative(source.MaximumFutureSkewMilliseconds, "hot_publish.placement_coordinator.maximum_future_skew_ms");
        if (window.MaximumWindowCount <= 0 || window.CleanupReserveMilliseconds <= 0
            || window.CleanupReserveMilliseconds >= timeout
            || window.MaximumFrameBytes < GpuWindowActionProtocol.MaximumMessageBytes || window.PipeBufferBytes <= 0
            || window.PreparationMaximumFrameBytes < OpenGlCallbackPreparationProtocol.MaximumMessageBytes)
            throw new InvalidDataException("GPU window execution needs positive explicit limits and work time before cleanup.");

        return new CompiledHostManagerPlacementCoordinatorHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.RetryDelayMilliseconds,
            timeout,
            source.MaximumFutureSkewMilliseconds,
            new(window.MaximumWindowCount, window.CleanupReserveMilliseconds, window.MaximumFrameBytes,
                window.PipeBufferBytes, window.PreparationMaximumFrameBytes), observationWindow);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record GpuWindowExecutionLimitsDeclaration(
        [property: JsonPropertyName("maximum_window_count")] int MaximumWindowCount,
        [property: JsonPropertyName("cleanup_reserve_ms")] int CleanupReserveMilliseconds,
        [property: JsonPropertyName("maximum_frame_bytes")] int MaximumFrameBytes,
        [property: JsonPropertyName("pipe_buffer_bytes")] int PipeBufferBytes,
        [property: JsonPropertyName("preparation_maximum_frame_bytes")] int PreparationMaximumFrameBytes);
}
