using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Domain.PublicResources;

namespace ResourceManager.App.Application.PublicResources;

public interface ISharedResourceBroker
{
    ulong HostOwnerApplicationKey { get; }

    SharedResourceId PublishHostResource(
        HostPublicResourceDefinition definition,
        IHostPublicResourceLifecycle lifecycle);

    bool TryUpdateHostResource(SharedResourceId id, HostPublicResourceDefinition definition);

    bool RemoveHostResource(SharedResourceId id);
}

public interface IHostPublicResourceLifecycle
{
    bool SupportsRecall => false;

    bool TryUnload(HostPublicResourceUnloadContext context);

    bool TryRecall(
        HostPublicResourceRecallContext context,
        out HostPublicResourceDefinition? definition)
    {
        definition = null;
        return false;
    }

    HostPublicResourceLifecycleResult ExecuteUnload(
        HostPublicResourceLifecycleOperation operation,
        HostPublicResourceUnloadContext context)
    {
        try
        {
            return TryUnload(context)
                ? new(HostPublicResourceEffectKnowledge.KnownEffect, null)
                : new(HostPublicResourceEffectKnowledge.KnownNoEffect, null);
        }
        catch
        {
            return new(HostPublicResourceEffectKnowledge.EffectUncertain, null);
        }
    }

    HostPublicResourceLifecycleResult ExecuteRecall(
        HostPublicResourceLifecycleOperation operation,
        HostPublicResourceRecallContext context)
    {
        try
        {
            return TryRecall(context, out var definition)
                ? new(HostPublicResourceEffectKnowledge.KnownEffect, definition)
                : new(HostPublicResourceEffectKnowledge.KnownNoEffect, null);
        }
        catch
        {
            return new(HostPublicResourceEffectKnowledge.EffectUncertain, null);
        }
    }

    HostPublicResourceLifecycleResult Recover(
        HostPublicResourceLifecycleOperation operation)
        => new(HostPublicResourceEffectKnowledge.EffectUncertain, null);
}

public enum HostPublicResourceLifecycleOperationKind : byte
{
    Unload = 1,
    Recall = 2
}

public readonly record struct HostPublicResourceLifecycleOperation(
    SharedResourceAuthorityId OperationId,
    HostPublicResourceLifecycleOperationKind Kind,
    SharedResourceId ResourceId);

public enum HostPublicResourceEffectKnowledge : byte
{
    KnownNoEffect = 0,
    KnownEffect = 1,
    EffectUncertain = 2
}

public sealed record HostPublicResourceLifecycleResult(
    HostPublicResourceEffectKnowledge EffectKnowledge,
    HostPublicResourceDefinition? Definition);

public readonly record struct HostPublicResourceUnloadContext(
    SharedResourceId ResourceId,
    ulong AdapterKey,
    ulong SizeBytes,
    AdapterResourceTier Tier,
    HostPublicResourceUnloadReason Reason,
    uint RetentionScoreQ16);

public readonly record struct HostPublicResourceRecallContext(
    SharedResourceId ResourceId,
    ulong AdapterKey,
    AdapterResourceTier Tier,
    int SubscriberCount,
    byte ActivityScore,
    uint RetentionScoreQ16);

public interface IPublicResourceDirectoryQueries
{
    PublicResourceCatalogSnapshot GetCatalog();

    PublicResourceDescriptor? Find(ulong publicResourceId);

    PublicResourceTransportSummary GetTransportSummary();
}

public interface IHostPublicResourceCapability
{
    HostPublicResourceCapabilitySnapshot GetCapability();
}

internal interface ISharedResourceSubscriptionMaintenance
{
    int MaintenanceIntervalMilliseconds { get; }

    SharedResourceMaintenanceResult Refresh(ulong nowTimestamp);
}

internal readonly record struct SharedResourceMaintenanceResult(
    int ExpiredSubscriptions,
    int ExpiredUseTasks);

public interface IHostPublicResourceSelfManager
{
    HostPublicResourceSelfManagerTickResult Tick(
        HostPublicResourceSelfManagerTickRequest request);
}

public readonly record struct HostPublicResourceSelfManagerTickRequest(
    HostPublicResourceCapacityShortage Shortage,
    uint MaximumNewEffectAttempts,
    uint MaximumRecoveryAttempts);

public readonly record struct HostPublicResourceSelfManagerTickResult(
    int PlannedCount,
    int UnloadedCount,
    int RecalledCount,
    int RejectedCount,
    uint NewEffectAttemptCount,
    uint RecoveryAttemptCount,
    ulong ReleasedBytes,
    ulong RestoredBytes,
    ulong SampleGeneration);
