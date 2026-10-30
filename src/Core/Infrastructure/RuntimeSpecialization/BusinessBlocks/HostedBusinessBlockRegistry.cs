namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Shares concrete hosted wrappers between block factories and Generic Host adapters.</summary>
internal sealed class HostedBusinessBlockRegistry(BusinessBlockDiagnosticsRegistry diagnostics,
    IEnumerable<BusinessBlockRegistration> registrations, HostedRunResultFacts? failures = null)
{
    private readonly Dictionary<int, HostedBusinessBlockExecution> executions = registrations
        .Where(registration => registration.HostedExecution is not null)
        .ToDictionary(registration => registration.Id, registration => registration.HostedExecution!);
    private readonly object sync = new();
    private readonly Dictionary<int, HostedBusinessBlock> blocks = new();
    private bool hostStopping;
    internal bool IsHostStopping { get { lock (sync) return hostStopping; } }
    internal void BeginHostStopping() { lock (sync) hostStopping = true; }

    internal HostedBusinessBlock GetOrAdd(int id, Func<IHostedService> resolve)
    {
        lock (sync)
        {
            if (!blocks.TryGetValue(id, out var block))
                blocks.Add(id, block = new(id, resolve, this, diagnostics,
                    executions.GetValueOrDefault(id) ?? HostedBusinessBlockExecution.EntryExitOrBackground, failures));
            return block;
        }
    }
}
