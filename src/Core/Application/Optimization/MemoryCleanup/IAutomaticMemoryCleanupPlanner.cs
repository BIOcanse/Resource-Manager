using ResourceManager.App.Domain.Optimization.MemoryCleanup;

namespace ResourceManager.App.Application.Optimization.MemoryCleanup;

public interface IAutomaticMemoryCleanupPlanner : IDisposable
{
    AutomaticMemoryCleanupPlanResult Plan(AutomaticMemoryCleanupPlanRequest request);

    void Complete(IReadOnlyList<AutomaticMemoryCleanupFeedback> feedback);

    void Reset();
}
