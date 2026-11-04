namespace ResourceManager.ReadWrite.Native.Pdh;

public sealed record NativePdhSessionOptions(TimeSpan FrameReuseWindow, TimeSpan LastGoodLifetime,
    int BaselineResetIntervalMilliseconds);

/// <summary>Resolved policy and content identities. HostState belongs solely to the optional runtime adapter.</summary>
public sealed record NativePdhRuntimePlan(long RuntimePlanVersion, string BuildSha256,
    string RecreateSha256, string HotPublishSha256, uint AbiVersion,
    int BaselineResetIntervalMilliseconds, int FrameReuseWindowMilliseconds,
    int LastGoodLifetimeMilliseconds, object? HostState);

/// <summary>The host owns publication and deployment settlement; the session owns native handles.</summary>
public interface IPdhCollectorRuntime
{
    NativePdhRuntimePlan CaptureDesired();
    object BeginInitialCreate(NativePdhRuntimePlan plan);
    object BeginHotPublish(NativePdhRuntimePlan plan);
    object BeginHostRecreateAndHotPublish(NativePdhRuntimePlan plan);
    void CompleteSucceeded(object attempt);
    void CompleteFailed(object attempt, string failureCode);
}
