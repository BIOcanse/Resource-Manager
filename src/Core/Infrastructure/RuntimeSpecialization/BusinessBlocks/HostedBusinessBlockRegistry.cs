namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Shares concrete hosted wrappers between block factories and Generic Host adapters.</summary>
internal sealed class HostedBusinessBlockRegistry(BusinessBlockDiagnosticsRegistry diagnostics)
{
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
                blocks.Add(id, block = new(id, resolve, this, diagnostics));
            return block;
        }
    }
}
