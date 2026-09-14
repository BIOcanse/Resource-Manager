using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

public sealed class HostManagerAdapterSchedulingTransaction
{
    private readonly IAdapterPolicyDispatcher dispatcher;
    private readonly TimeProvider timeProvider;

    internal HostManagerAdapterSchedulingTransaction(
        IAdapterPolicyDispatcher dispatcher,
        TimeProvider timeProvider)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<HostManagerAdapterSchedulingCaptureResult> CaptureAsync(
        string softwareId,
        int maximumOpaquePayloadBytes,
        int maximumEnvelopeBytes,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalText(softwareId)
            || maximumOpaquePayloadBytes <= 0
            || maximumEnvelopeBytes < HostManagerAdapterSchedulingTransactionPayloadCodec.HeaderSize)
        {
            return CaptureFailure(
                HostManagerAdapterSchedulingCaptureStatus.Conflict,
                "The capture request is not canonical or has invalid explicit capacity limits.");
        }

        if (!TryCreateDeadline(deadline, cancellationToken, out var deadlineScope))
        {
            return CaptureFailure(
                HostManagerAdapterSchedulingCaptureStatus.Unavailable,
                "The capture deadline has already elapsed.");
        }

        using (deadlineScope)
        {
            AdapterSchedulingStateExportResult export;
            try
            {
                export = await dispatcher.ExportSoftwareSchedulingStateAsync(
                    softwareId,
                    maximumOpaquePayloadBytes,
                    deadline,
                    deadlineScope.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.Unavailable,
                    "The adapter state capture did not complete before its explicit deadline.");
            }
            catch (Exception exception)
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.Unavailable,
                    $"The adapter state capture is unavailable: {exception.Message}");
            }

            if (timeProvider.GetUtcNow() > deadline)
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.Unavailable,
                    "The adapter state capture completed after its explicit deadline.");
            }

            if (export is null)
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.Unavailable,
                    "The adapter returned no scheduling state capture result.");
            }

            if (export.Status != AdapterSchedulingStateExportStatus.Exported)
            {
                return CaptureFailure(MapCaptureStatus(export.Status), export.Message);
            }

            if (export.Identity is null
                || !string.Equals(export.Identity.SoftwareId, softwareId, StringComparison.Ordinal))
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.Conflict,
                    "The exported scheduling identity does not exactly match the requested software identity.");
            }

            if (export.Payload?.Bytes is not { } opaquePayload
                || opaquePayload.Length > maximumOpaquePayloadBytes
                || !HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
                    export,
                    maximumEnvelopeBytes,
                    out var envelope))
            {
                return CaptureFailure(
                    HostManagerAdapterSchedulingCaptureStatus.InvalidPayload,
                    "The exported scheduling state cannot be encoded by the strict transaction contract.");
            }

            return new HostManagerAdapterSchedulingCaptureResult(
                HostManagerAdapterSchedulingCaptureStatus.Captured,
                new HostManagerAdapterSchedulingCapturedState(export.Identity, envelope),
                export.Message);
        }
    }

    internal async Task<HostManagerAdapterSchedulingApplyResult> ApplyAsync(
        HostManagerAdapterSchedulingApplyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var envelopeBytes = command.CapturedEnvelope;
        if (!IsCanonicalApplyCommand(command)
            || !TryDecodePayload(
                envelopeBytes,
                command.MaximumOpaquePayloadBytes,
                command.MaximumEnvelopeBytes,
                out var payload))
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.Rejected,
                "The apply command or captured scheduling state is invalid.");
        }

        if (!TryCreateDeadline(command.Deadline, cancellationToken, out var applyDeadline))
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.Unavailable,
                "The apply deadline has already elapsed.");
        }

        AdapterSoftwareSchedulingResult applyResult;
        using (applyDeadline)
        {
            try
            {
                applyResult = await dispatcher.ApplySoftwareSchedulingAsync(
                    payload.Identity.SoftwareId,
                    new AdapterSoftwareSchedulingEnvelope(
                        command.PolicyId,
                        command.GeneratedAt,
                        command.TargetId,
                        command.DisplayName,
                        payload.Identity.SoftwareId,
                        command.TargetCpuGrade,
                        command.TargetGpuGrade,
                        command.CpuScore,
                        command.GpuScore,
                        command.Reason),
                    applyDeadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ApplyFailure(
                    HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                    $"The scheduling effect may have started but no exact result is available: {exception.Message}");
            }
        }

        if (applyResult is null)
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                "The scheduling effect returned no result, so its state is uncertain.");
        }

        if (!applyResult.Accepted)
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.Rejected,
                applyResult.Message);
        }

        var receiptIsCanonical = IsCanonicalApplyReceipt(command, applyResult);
        if (!TryCreateDeadline(command.Deadline, cancellationToken, out var readbackDeadline))
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                "The scheduling effect was accepted but its exact state could not be read before the deadline.");
        }

        AdapterSchedulingStateExportResult readback;
        using (readbackDeadline)
        {
            try
            {
                readback = await dispatcher.ExportSoftwareSchedulingStateAsync(
                    payload.Identity.SoftwareId,
                    command.MaximumOpaquePayloadBytes,
                    command.Deadline,
                    readbackDeadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ApplyFailure(
                    HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                    $"The scheduling effect was accepted but exact readback is unavailable: {exception.Message}");
            }
        }

        if (readback is null
            || readback.Status != AdapterSchedulingStateExportStatus.Exported
            || readback.Identity is null)
        {
            return ApplyFailure(
                HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                "The scheduling effect was accepted but the adapter did not return an exported readback state.");
        }

        if (!Equals(readback.Identity, payload.Identity))
        {
            return new HostManagerAdapterSchedulingApplyResult(
                HostManagerAdapterSchedulingApplyStatus.OwnershipLost,
                readback.Identity,
                readback.CurrentCpuGrade,
                readback.CurrentGpuGrade,
                "The adapter instance or lease changed before exact scheduling readback.");
        }

        if (!receiptIsCanonical
            || readback.CurrentCpuGrade != (command.TargetCpuGrade ?? payload.OriginalCpuGrade)
            || readback.CurrentGpuGrade != (command.TargetGpuGrade ?? payload.OriginalGpuGrade)
            || timeProvider.GetUtcNow() > command.Deadline)
        {
            return new HostManagerAdapterSchedulingApplyResult(
                HostManagerAdapterSchedulingApplyStatus.StateUncertain,
                readback.Identity,
                readback.CurrentCpuGrade,
                readback.CurrentGpuGrade,
                "The adapter did not prove the exact requested scheduling state within the deadline.");
        }

        return new HostManagerAdapterSchedulingApplyResult(
            applyResult.CpuChanged || applyResult.GpuChanged
                ? HostManagerAdapterSchedulingApplyStatus.Applied
                : HostManagerAdapterSchedulingApplyStatus.AlreadyApplied,
            readback.Identity,
            readback.CurrentCpuGrade,
            readback.CurrentGpuGrade,
            applyResult.Message);
    }

    internal async Task<HostManagerAdapterSchedulingRestoreResult> RestoreAsync(
        HostManagerAdapterSchedulingRestoreCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var envelopeBytes = command.CapturedEnvelope;
        if (!TryDecodePayload(
                envelopeBytes,
                command.MaximumOpaquePayloadBytes,
                command.MaximumEnvelopeBytes,
                out var payload))
        {
            return RestoreFailure(
                HostManagerAdapterSchedulingRestoreStatus.InvalidPayload,
                "The captured scheduling state is invalid.");
        }

        if (!TryCreateDeadline(command.Deadline, cancellationToken, out var deadlineScope))
        {
            return RestoreFailure(
                HostManagerAdapterSchedulingRestoreStatus.Unavailable,
                "The restore deadline has already elapsed.");
        }

        AdapterSchedulingStateRestoreResult result;
        using (deadlineScope)
        {
            try
            {
                result = await dispatcher.RestoreSoftwareSchedulingStateAsync(
                    payload.Identity.SoftwareId,
                    new AdapterSchedulingStateRestoreCommand(
                        payload.Identity,
                        payload.Payload,
                        payload.OriginalCpuGrade,
                        payload.OriginalGpuGrade,
                        command.MaximumOpaquePayloadBytes,
                        command.Deadline),
                    deadlineScope.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return RestoreFailure(
                    HostManagerAdapterSchedulingRestoreStatus.StateUncertain,
                    $"The restore effect may have started but no exact result is available: {exception.Message}");
            }
        }

        if (result is null)
        {
            return RestoreFailure(
                HostManagerAdapterSchedulingRestoreStatus.StateUncertain,
                "The restore effect returned no result, so its state is uncertain.");
        }

        if (result.OwnershipResult == AdapterSchedulingStateOwnershipResult.OwnershipLost)
        {
            return new HostManagerAdapterSchedulingRestoreResult(
                HostManagerAdapterSchedulingRestoreStatus.OwnershipLost,
                result.Identity,
                result.ObservedCpuGrade,
                result.ObservedGpuGrade,
                result.Message);
        }

        if (result.Status == AdapterSchedulingStateRestoreStatus.Unsupported
            || result.Status == AdapterSchedulingStateRestoreStatus.Unavailable)
        {
            return new HostManagerAdapterSchedulingRestoreResult(
                HostManagerAdapterSchedulingRestoreStatus.Unavailable,
                result.Identity,
                result.ObservedCpuGrade,
                result.ObservedGpuGrade,
                result.Message);
        }

        if (result.Status == AdapterSchedulingStateRestoreStatus.Conflict)
        {
            return new HostManagerAdapterSchedulingRestoreResult(
                HostManagerAdapterSchedulingRestoreStatus.Conflict,
                result.Identity,
                result.ObservedCpuGrade,
                result.ObservedGpuGrade,
                result.Message);
        }

        if (result.Status != AdapterSchedulingStateRestoreStatus.Restored
            || result.OwnershipResult is not (
                AdapterSchedulingStateOwnershipResult.Restored
                or AdapterSchedulingStateOwnershipResult.AlreadyRestored)
            || result.Identity is null
            || !Equals(result.Identity, payload.Identity)
            || result.ObservedCpuGrade != payload.OriginalCpuGrade
            || result.ObservedGpuGrade != payload.OriginalGpuGrade
            || timeProvider.GetUtcNow() > command.Deadline)
        {
            return new HostManagerAdapterSchedulingRestoreResult(
                HostManagerAdapterSchedulingRestoreStatus.StateUncertain,
                result.Identity,
                result.ObservedCpuGrade,
                result.ObservedGpuGrade,
                "The adapter did not prove exact restoration of the captured scheduling state.");
        }

        return new HostManagerAdapterSchedulingRestoreResult(
            result.OwnershipResult == AdapterSchedulingStateOwnershipResult.Restored
                ? HostManagerAdapterSchedulingRestoreStatus.Restored
                : HostManagerAdapterSchedulingRestoreStatus.AlreadyRestored,
            result.Identity,
            result.ObservedCpuGrade,
            result.ObservedGpuGrade,
            result.Message);
    }

    private bool TryCreateDeadline(
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        out DeadlineScope scope)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            scope = null!;
            return false;
        }

        try
        {
            scope = new DeadlineScope(remaining, timeProvider, cancellationToken);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            scope = null!;
            return false;
        }
    }

    private static bool TryDecodePayload(
        ReadOnlySpan<byte> envelope,
        int maximumOpaquePayloadBytes,
        int maximumEnvelopeBytes,
        out HostManagerAdapterSchedulingTransactionPayload payload)
    {
        payload = null!;
        return maximumOpaquePayloadBytes > 0
            && maximumEnvelopeBytes >= HostManagerAdapterSchedulingTransactionPayloadCodec.HeaderSize
            && HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
                envelope,
                maximumEnvelopeBytes,
                out payload)
            && payload.Payload.Bytes.Length <= maximumOpaquePayloadBytes;
    }

    private static bool IsCanonicalApplyCommand(HostManagerAdapterSchedulingApplyCommand command)
    {
        return command.MaximumOpaquePayloadBytes > 0
            && command.MaximumEnvelopeBytes >= HostManagerAdapterSchedulingTransactionPayloadCodec.HeaderSize
            && IsCanonicalText(command.PolicyId)
            && IsCanonicalText(command.TargetId)
            && IsCanonicalText(command.DisplayName)
            && IsCanonicalText(command.Reason)
            && command.GeneratedAt != default
            && command.GeneratedAt <= command.Deadline
            && (command.TargetCpuGrade.HasValue || command.TargetGpuGrade.HasValue)
            && (!command.TargetCpuGrade.HasValue || Enum.IsDefined(command.TargetCpuGrade.Value))
            && (!command.TargetGpuGrade.HasValue || Enum.IsDefined(command.TargetGpuGrade.Value))
            && double.IsFinite(command.CpuScore)
            && double.IsFinite(command.GpuScore);
    }

    private static bool IsCanonicalApplyReceipt(
        HostManagerAdapterSchedulingApplyCommand command,
        AdapterSoftwareSchedulingResult result)
    {
        return string.Equals(result.PolicyId, command.PolicyId, StringComparison.Ordinal)
            && result.AppliedCpuGrade == command.TargetCpuGrade
            && result.AppliedGpuGrade == command.TargetGpuGrade
            && (command.TargetCpuGrade.HasValue || !result.CpuChanged)
            && (command.TargetGpuGrade.HasValue || !result.GpuChanged);
    }

    private static bool IsCanonicalText(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Contains('\0', StringComparison.Ordinal);

    private static HostManagerAdapterSchedulingCaptureStatus MapCaptureStatus(
        AdapterSchedulingStateExportStatus status) => status switch
        {
            AdapterSchedulingStateExportStatus.Unsupported => HostManagerAdapterSchedulingCaptureStatus.Unsupported,
            AdapterSchedulingStateExportStatus.Unavailable => HostManagerAdapterSchedulingCaptureStatus.Unavailable,
            _ => HostManagerAdapterSchedulingCaptureStatus.Conflict
        };

    private static HostManagerAdapterSchedulingCaptureResult CaptureFailure(
        HostManagerAdapterSchedulingCaptureStatus status,
        string message) => new(status, null, message);

    private static HostManagerAdapterSchedulingApplyResult ApplyFailure(
        HostManagerAdapterSchedulingApplyStatus status,
        string message) => new(status, null, null, null, message);

    private static HostManagerAdapterSchedulingRestoreResult RestoreFailure(
        HostManagerAdapterSchedulingRestoreStatus status,
        string message) => new(status, null, null, null, message);

    private sealed class DeadlineScope : IDisposable
    {
        private readonly CancellationTokenSource deadlineCancellation;
        private readonly CancellationTokenSource linkedCancellation;

        internal DeadlineScope(
            TimeSpan remaining,
            TimeProvider timeProvider,
            CancellationToken callerCancellation)
        {
            deadlineCancellation = new CancellationTokenSource(remaining, timeProvider);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                deadlineCancellation.Token);
        }

        internal CancellationToken Token => linkedCancellation.Token;

        public void Dispose()
        {
            linkedCancellation.Dispose();
            deadlineCancellation.Dispose();
        }
    }
}
