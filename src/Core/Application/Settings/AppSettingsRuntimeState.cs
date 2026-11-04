using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsRuntimeState
{
    public static AppSettingsUpdateResult Project(
        AppSettingsUpdateResult stored, AppSettingsUpdateResult? applied)
    {
        if (applied is null || !string.Equals(stored.Revision, applied.Revision, StringComparison.Ordinal))
        {
            return stored with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeFailureCode = applied is null
                    ? "runtime-plan-not-published" : "settings-runtime-revision-mismatch"
            };
        }

        return stored with
        {
            RuntimeApplicationDisposition = applied.RuntimeApplicationDisposition,
            RuntimePlanVersion = applied.RuntimePlanVersion,
            RuntimePublicationSequence = applied.RuntimePublicationSequence,
            RuntimeDeliveryFailureCount = applied.RuntimeDeliveryFailureCount,
            RuntimeCapabilityConstrainedPaths = applied.RuntimeCapabilityConstrainedPaths,
            RuntimeFailureCode = applied.RuntimeFailureCode
        };
    }
}
