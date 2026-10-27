namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Backend registrations and factories over the shared immutable control tree.</summary>
public sealed class BackendBlockGraph : BusinessBlockGraph
{
    private readonly Dictionary<int, BusinessBlockRegistration> registrations;

    public BackendBlockGraph(IEnumerable<BusinessBlockRegistration> source)
        : this(source.ToArray()) { }

    private BackendBlockGraph(BusinessBlockRegistration[] source)
        : base(ResourceManager.App.StateItems.BlockUids.Program, source.Select(declaration => new BusinessBlockNode(declaration.Id, declaration.ParentId,
            declaration.Dependencies)))
    {
        registrations = source.ToDictionary(declaration => declaration.Id, declaration => declaration with
        {
            Dependencies = base.Get(declaration.Id).Inputs
        });
    }

    internal new BusinessBlockRegistration Get(int id) => registrations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
}
