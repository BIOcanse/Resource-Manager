namespace ResourceManager.App.Application.CpuMonopolyProtection;

public sealed record CpuMonopolyPriorityResult(bool Succeeded, string? Reason = null, string? Detail = null);

// The lifecycle owner listens only while running. This is a fact about a successful explicit
// RM write, not a request for the protection owner to restore or change any priority.
public interface IExplicitProcessPriorityChangeSource
{
    event Action<int, string>? UserPrioritySet;
    IDisposable GuardAutomaticPriorityWrites(Func<int, string, bool> canWrite);
}

public interface ICpuMonopolyProtectionExecutor : IExplicitProcessPriorityChangeSource
{
    string? ExclusionReason(CpuMonopolyProcess process);
    CpuMonopolyPriorityResult SetNormal(CpuMonopolyProcess process, Func<bool>? stillEligible = null,
        Action<bool>? completed = null);
}
