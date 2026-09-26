using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledDataHistoryPlan CompileHistory() => CompiledDataHistoryPlan.Empty;
}
