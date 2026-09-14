namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerAdapterPrivateResourceRecreatePlan(int StateCapacity);

public sealed record CompiledHostManagerAdapterPrivateResourceHotPublishPlan(
    int MaximumSnapshotAgeMilliseconds,
    int MaximumFutureClockSkewMilliseconds,
    int SettlementIntervalMilliseconds,
    int RequestTimeoutMilliseconds,
    int MaximumResponseBytes,
    int MaximumConcurrentReads,
    int MaximumCycleDurationMilliseconds,
    byte ActiveIncrement,
    byte DecayNumerator,
    byte DecayDenominator);

public sealed record CompiledHostManagerProcessPolicyExecutorHotPublishPlan(
    int MaximumBatchItems);

public sealed record CompiledHostManagerPdhCollectorRecreatePlan(
    int BaselineResetIntervalMilliseconds);

public sealed record CompiledHostManagerPdhCollectorHotPublishPlan(
    int FrameReuseWindowMilliseconds,
    int LastGoodLifetimeMilliseconds);
