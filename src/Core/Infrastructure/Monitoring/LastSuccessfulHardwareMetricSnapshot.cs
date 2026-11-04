using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

// Host plan/schedule adaptation only. The original current/history owner is in the SDK.
internal static class HostHardwareMetricPublicationExtensions
{
    internal static bool TryCaptureHostedTicket(
        this LastSuccessfulHardwareMetricSnapshot owner,
        ulong hostedRunId,
        SamplingOwnerToken workspaceOwnerToken,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        out HardwareMetricHostedPublicationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(schedule);
        return owner.TryCaptureHostedTicket(hostedRunId, workspaceOwnerToken,
            schedule.WorkspaceIncarnation, schedule.ConfigurationGeneration, out ticket);
    }

}
