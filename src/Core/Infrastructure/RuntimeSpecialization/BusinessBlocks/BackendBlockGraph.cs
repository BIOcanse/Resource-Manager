namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Backend registrations and factories over the shared immutable control tree.</summary>
public sealed class BackendBlockGraph : BusinessBlockGraph
{
    private readonly Dictionary<string, BusinessBlockRegistration> registrations;

    public BackendBlockGraph(IEnumerable<BusinessBlockRegistration> source)
        : this(source.ToArray()) { }

    private BackendBlockGraph(BusinessBlockRegistration[] source)
        : base(source.Select(declaration => new BusinessBlockNode(declaration.Id, declaration.ParentId,
            declaration.Dependencies, declaration.Outputs)))
    {
        registrations = source.ToDictionary(declaration => declaration.Id, declaration => declaration with
        {
            Dependencies = base.Get(declaration.Id).Inputs,
            Outputs = base.Get(declaration.Id).Outputs
        }, StringComparer.Ordinal);
    }

    internal new BusinessBlockRegistration Get(string id) => registrations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
}
