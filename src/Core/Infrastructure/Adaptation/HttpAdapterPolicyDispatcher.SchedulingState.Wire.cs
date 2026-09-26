using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class HttpAdapterPolicyDispatcher
{
    private sealed class AdapterSchedulingDeadlineCancellation : IAsyncDisposable
    {
        private static readonly TimeSpan MaximumDelaySlice = TimeSpan.FromDays(1);

        private readonly CancellationTokenSource deadlineSource = new();
        private readonly CancellationTokenSource linkedSource;
        private readonly Task deadlineTask;
        private int deadlineExpired;

        public AdapterSchedulingDeadlineCancellation(
            DateTimeOffset deadline,
            CancellationToken callerCancellationToken)
        {
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                deadlineSource.Token);
            deadlineTask = RunDeadlineAsync(deadline);
        }

        public CancellationToken Token => linkedSource.Token;

        public bool DeadlineExpired => Volatile.Read(ref deadlineExpired) != 0;

        public async ValueTask DisposeAsync()
        {
            await deadlineSource.CancelAsync();
            await deadlineTask;
            linkedSource.Dispose();
            deadlineSource.Dispose();
        }

        private async Task RunDeadlineAsync(DateTimeOffset deadline)
        {
            while (!deadlineSource.IsCancellationRequested)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    Volatile.Write(ref deadlineExpired, 1);
                    await deadlineSource.CancelAsync();
                    return;
                }

                var delay = remaining > MaximumDelaySlice ? MaximumDelaySlice : remaining;
                try
                {
                    await Task.Delay(delay, deadlineSource.Token);
                }
                catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private sealed record AdapterSchedulingStateExportWireRequest(
        string Operation,
        Guid RequestId,
        string SoftwareId,
        string AdapterId,
        string ApplicationId,
        int MaximumPayloadBytes,
        DateTimeOffset Deadline);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record AdapterSchedulingStateExportWireResponse(
        string Operation,
        Guid RequestId,
        AdapterSchedulingStateExportStatus Status,
        AdapterSchedulingStateIdentityWire? Identity,
        uint PayloadVersion,
        int PayloadLength,
        string? PayloadDigestSha256,
        byte[]? Payload,
        AdapterCpuSchedulingGrade? CurrentCpuGrade,
        AdapterGpuSchedulingGrade? CurrentGpuGrade,
        DateTimeOffset ObservedAt,
        string? Message);

    private sealed record AdapterSchedulingStateRestoreWireRequest(
        string Operation,
        Guid RequestId,
        AdapterSchedulingStateIdentityWire Identity,
        uint PayloadVersion,
        int PayloadLength,
        string PayloadDigestSha256,
        byte[] Payload,
        AdapterCpuSchedulingGrade? ExpectedCpuGrade,
        AdapterGpuSchedulingGrade? ExpectedGpuGrade,
        int MaximumPayloadBytes,
        DateTimeOffset Deadline);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record AdapterSchedulingStateRestoreWireResponse(
        string Operation,
        Guid RequestId,
        AdapterSchedulingStateRestoreStatus Status,
        AdapterSchedulingStateOwnershipResult OwnershipResult,
        AdapterSchedulingStateIdentityWire? Identity,
        uint PayloadVersion,
        int PayloadLength,
        string? PayloadDigestSha256,
        AdapterCpuSchedulingGrade? ObservedCpuGrade,
        AdapterGpuSchedulingGrade? ObservedGpuGrade,
        DateTimeOffset ObservedAt,
        string? Message);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record AdapterSchedulingStateIdentityWire(
        string AdapterInstanceIdHigh,
        string AdapterInstanceIdLow,
        string LeaseIdHigh,
        string LeaseIdLow,
        string LeaseGeneration,
        string SoftwareId,
        string AdapterId,
        string ApplicationId);
}
