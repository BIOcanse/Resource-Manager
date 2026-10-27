using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.CpuTopology;

// Both transports publish this same current value. Available values retain the
// existing snapshot shape; unavailable values carry an owner-authored reason.
[JsonDerivedType(typeof(CpuCoreResidencySnapshot))]
[JsonDerivedType(typeof(CpuCoreResidencyUnavailable))]
public abstract record CpuCoreResidencyObservation;

public sealed record CpuCoreResidencyUnavailable(string ReasonCode) : CpuCoreResidencyObservation
{
    public string State => "unavailable";
}

public static class CpuCoreResidencyUnavailableReasons
{
    public const string NotRequested = "cpu-residency-not-requested";
    public const string WindowWarming = "cpu-residency-window-warming";
    public const string EventsLost = "cpu-residency-events-lost";
    public const string SessionRestarted = "cpu-residency-session-restarted";
    public const string SessionUnavailable = "cpu-residency-session-unavailable";
    public const string CaptureFailed = "cpu-residency-capture-failed";
}
