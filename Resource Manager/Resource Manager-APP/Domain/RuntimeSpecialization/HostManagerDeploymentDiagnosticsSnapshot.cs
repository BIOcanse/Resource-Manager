using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record HostManagerDeploymentDiagnosticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    ulong PublicationSequence,
    long RuntimePlanVersion,
    int SchemaVersion,
    int ProfileRevision,
    string ProfileName,
    string ProfileSource,
    string ProfileSha256,
    string PlanSha256,
    string BuildSha256,
    string RecreateSha256,
    string HotPublishSha256,
    ulong PlanEpoch,
    CompiledHostManagerBindingProvenance BindingProvenance,
    ImmutableArray<CompiledHostManagerNativeBinaryIdentity> NativeBinaries,
    HostManagerDeploymentSnapshot Deployment);
