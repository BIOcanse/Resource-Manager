using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Static control tree and separate data declarations. Data edges never control execution.</summary>
public sealed class BackendBlockGraph
{
    public const string Program = "$program";
    private readonly Dictionary<string, BusinessBlockRegistration> registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> children = new(StringComparer.Ordinal);
    public IReadOnlyList<BusinessBlockNode> Nodes { get; }
    public IReadOnlyList<string> BlockIds { get; }

    public BackendBlockGraph(IEnumerable<BusinessBlockRegistration> source)
    {
        foreach (var declaration in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.ParentId);
            if (declaration.Id == Program || !registrations.TryAdd(declaration.Id, declaration with
                {
                    Dependencies = Array.AsReadOnly(declaration.Dependencies.ToArray()),
                    Outputs = Array.AsReadOnly(declaration.Outputs.ToArray())
                }))
                throw new ArgumentException($"Duplicate business block or multiple parents: {declaration.Id}");
        }
        foreach (var block in registrations.Values)
        {
            if (block.ParentId != Program) _ = Get(block.ParentId);
            foreach (var input in block.Dependencies) _ = Get(input);
        }
        foreach (var parent in registrations.Keys.Prepend(Program))
            children.Add(parent, Array.AsReadOnly(registrations.Values.Where(value => value.ParentId == parent)
                .Select(value => value.Id).ToArray()));
        var order = new List<string>(registrations.Count);
        var pending = new Queue<string>(Children(Program));
        while (pending.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var child in Children(id)) pending.Enqueue(child);
        }
        if (order.Count != registrations.Count) throw new InvalidOperationException("Business block control cycle.");
        BlockIds = order.AsReadOnly();
        Nodes = Array.AsReadOnly(order.Select(id => new BusinessBlockNode(id, Get(id).ParentId,
            Get(id).Dependencies, Get(id).Outputs)).ToArray());
    }

    internal BusinessBlockRegistration Get(string id) => registrations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
    public IReadOnlyList<string> Children(string parentId) => children.TryGetValue(parentId, out var result)
        ? result : throw new KeyNotFoundException($"Unknown business block parent: {parentId}");
}
