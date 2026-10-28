using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.App.Hosting;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>The generic host permits each of its aliases to enter; decisions still belong to the tree.</summary>
internal sealed class BackendHostPermitFacts
{
    private readonly Dictionary<int, Permit> permits = new();

    public BackendHostPermitFacts(StateAuthority authority, NativeControl native, IEnumerable<HostedBusinessBlockAlias> aliases)
    {
        var hostedIds = aliases.Select(alias => alias.Id).ToHashSet();
        foreach (var id in native.Graph.BlockIds)
            permits.Add(id, new Permit(authority, id, !hostedIds.Contains(id)));
    }

    internal Task SetAsync(int id, bool allowed, BusinessBlockReason reason)
    {
        Permit permit;
        lock (permits)
        {
            if (!permits.TryGetValue(id, out permit!)) throw new KeyNotFoundException($"Unknown host alias UID {id}.");
        }
        Volatile.Write(ref permit.Allowed, allowed ? 1 : 0);
        return permit.Reader.NotifyChangedAsync(reason);
    }

    private sealed class Permit
    {
        internal int Allowed;
        internal readonly StateReadFunction<bool> Reader;
        internal Permit(StateAuthority authority, int id, bool allowed)
        {
            Allowed = allowed ? 1 : 0;
            Reader = authority.ClaimReadFunction<bool>(new(StateItemUids.HostStartPermitted, id),
                () => StateRead<bool>.FromValue(Volatile.Read(ref Allowed) != 0));
        }
    }
}
