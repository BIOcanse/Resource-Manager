using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Host binding only: the independent subscription package owns these current facts.</summary>
internal sealed class SubscriptionActivityFacts(
    HostManagerSamplingSubscriptionOwner owner,
    PresentFrameEtwSource present,
    ILogger<SubscriptionActivityFacts> logger) : IDisposable
{
    private readonly Dictionary<int, ActivityBinding> roles = [];
    private readonly Dictionary<SubscriptionFieldGroup, ActivityBinding> fields = [];
    private bool initialized;

    internal void Initialize(StateAuthority authority)
    {
        if (initialized) return;
        initialized = true;
        owner.Activity.BindField(SubscriptionFieldGroup.GraphicsApiObservation,
            () => present.HasActiveSubscriptions, handler => present.SubscriptionsChanged += handler);
        BindRole(1, StateItemUids.SamplingHardwareMetricsInUse);
        BindRole(2, StateItemUids.SamplingDeviceTopologyInUse);
        BindRole(3, StateItemUids.SamplingResourceBreakdownInUse);
        BindRole(4, StateItemUids.SamplingGpuTelemetryInUse);
        BindRole(5, StateItemUids.SamplingInterruptsInUse);
        BindRole(6, StateItemUids.SamplingCpuResidencyInUse);
        BindRole(7, StateItemUids.SamplingCpuTopologyInUse);
        BindField(SubscriptionFieldGroup.GraphicsApiObservation, StateItemUids.GraphicsApiObservationInUse);
        BindField(SubscriptionFieldGroup.ProcessDedicatedGpuMemory, StateItemUids.ProcessDedicatedGpuMemoryInUse);
        BindField(SubscriptionFieldGroup.VramResidual, StateItemUids.VramResidualInUse);
        BindField(SubscriptionFieldGroup.DiskAttribution, StateItemUids.DiskAttributionInUse);
        BindField(SubscriptionFieldGroup.NetworkAttribution, StateItemUids.NetworkAttributionInUse);
        owner.Activity.RoleChanged += OnRoleChanged;
        owner.Activity.FieldChanged += OnFieldChanged;
        owner.SettingsPublished += OnSettingsPublished;
        foreach (var reader in roles.Values.Concat(fields.Values)) _ = NotifyAsync(reader);

        void BindRole(int role, int uid) => roles.Add(role, Bind(uid, () => ReadRole(role)));
        void BindField(SubscriptionFieldGroup group, int uid) => fields.Add(group, Bind(uid, () => ReadField(group)));
        ActivityBinding Bind(int uid, Func<StateRead<bool>> read)
        {
            var capability = authority.ClaimReadFunction<bool>(uid, read);
            return new(read, () => capability.NotifyChangedAsync(new(BusinessBlockRequestKind.Dependency,
                "subscription-core", "subscription-activity-changed", 0)));
        }
    }

    internal StateRead<bool> ReadRole(int role) => owner.HasPublishedSettings
        ? StateRead<bool>.FromValue(owner.Activity.HasInUseSubscriptions(role))
        : StateRead<bool>.NoValue(NoValueReason.NotPublished);

    internal StateRead<bool> ReadField(SubscriptionFieldGroup group) => owner.HasPublishedSettings
        ? StateRead<bool>.FromValue(owner.Activity.HasInUseSubscriptions(group))
        : StateRead<bool>.NoValue(NoValueReason.NotPublished);

    private void OnRoleChanged(int role) => _ = NotifyAsync(roles[role]);
    private void OnFieldChanged(SubscriptionFieldGroup field) => _ = NotifyAsync(fields[field]);
    private void OnSettingsPublished()
    {
        foreach (var reader in roles.Values.Concat(fields.Values)) _ = NotifyAsync(reader);
    }

    public void Dispose()
    {
        owner.Activity.RoleChanged -= OnRoleChanged;
        owner.Activity.FieldChanged -= OnFieldChanged;
        owner.SettingsPublished -= OnSettingsPublished;
    }

    private async Task NotifyAsync(ActivityBinding reader)
    {
        try { await reader.NotifyAsync(); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { logger.LogError(error, "Subscription activity notification failed."); }
    }

    internal sealed class ActivityBinding(Func<StateRead<bool>> read, Func<Task> notify)
    {
        private readonly object notificationGate = new();
        private StateRead<bool>? lastNotified;

        internal async Task NotifyAsync()
        {
            lock (notificationGate)
            {
                var current = read();
                if (lastNotified is { } previous && current.HasValue == previous.HasValue
                    && (current.HasValue ? current.Value == previous.Value : current.NoValueReason == previous.NoValueReason)) return;
                lastNotified = current; // Notification metadata only; authority reads always call the owner.
            }
            // Observe the source edge before deferring; a renewal can otherwise
            // turn false back to true before either continuation reads it.
            // Leave the source's mutation gate before evaluating any control node.
            await Task.Yield();
            await notify();
        }
    }
}
