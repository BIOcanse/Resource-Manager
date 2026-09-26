namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum HostManagerDeploymentOperation : byte
{
    InitialCreate = 1,
    HotPublish = 2,
    HostRecreate = 3,
    HostRecreateAndHotPublish = 4
}

public enum HostManagerDeploymentAttemptSettlement : byte
{
    Applied = 1,
    Failed = 2,
    Stale = 3
}

public enum HostManagerFailureResolution : byte
{
    None = 0,
    Recovered = 1,
    Superseded = 2
}

internal readonly record struct HostManagerDeploymentAttemptToken(
    ulong AttemptId,
    ulong PublicationSequence,
    HostManagerModuleKind Module);

public sealed record HostManagerDeploymentAttemptSnapshot(
    ulong AttemptId,
    ulong PublicationSequence,
    HostManagerModuleKind Module,
    HostManagerDeploymentOperation Operation,
    HostManagerPendingLifecycle Lifecycle,
    ulong PlanEpoch,
    string BuildSha256,
    string RecreateSha256,
    string HotPublishSha256,
    DateTimeOffset StartedAtUtc);
