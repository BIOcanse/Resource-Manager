namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Read-only native tree metadata plus the host's UID-to-entry factories.</summary>
public sealed class BackendBlockGraph : BusinessBlockGraph
{
    private readonly Dictionary<int, BusinessBlockRegistration> registrations;
    internal BackendBlockGraph(IEnumerable<BusinessBlockRegistration> source, BusinessBlockGraph tree)
        : base(tree.Program, tree.Nodes) => registrations = BackendNativeControlFactory
            .WithWrappers(source, tree.BlockIds).ToDictionary(value => value.Id);
    internal new BusinessBlockRegistration Get(int id) => registrations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
}
