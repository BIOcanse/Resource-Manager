namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Static control tree and separate data declarations. Data edges never control execution.</summary>
public class BusinessBlockGraph
{
    public const string Program = "$program";
    private readonly Dictionary<string, BusinessBlockNode> declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> children = new(StringComparer.Ordinal);
    public IReadOnlyList<BusinessBlockNode> Nodes { get; }
    public IReadOnlyList<string> BlockIds { get; }

    public BusinessBlockGraph(IEnumerable<BusinessBlockNode> source)
    {
        foreach (var declaration in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.ParentId);
            if (declaration.Id == Program || !declarations.TryAdd(declaration.Id, declaration with
                {
                    Inputs = Array.AsReadOnly(declaration.Inputs.ToArray()),
                    Outputs = Array.AsReadOnly(declaration.Outputs.ToArray())
                }))
                throw new ArgumentException($"Duplicate business block or multiple parents: {declaration.Id}");
        }
        foreach (var block in declarations.Values)
        {
            if (block.ParentId != Program) _ = Get(block.ParentId);
            foreach (var input in block.Inputs) _ = Get(input);
        }
        foreach (var parent in declarations.Keys.Prepend(Program))
            children.Add(parent, Array.AsReadOnly(declarations.Values.Where(value => value.ParentId == parent)
                .Select(value => value.Id).ToArray()));
        var order = new List<string>(declarations.Count);
        var pending = new Queue<string>(Children(Program));
        while (pending.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var child in Children(id)) pending.Enqueue(child);
        }
        if (order.Count != declarations.Count) throw new InvalidOperationException("Business block control cycle.");
        BlockIds = order.AsReadOnly();
        Nodes = Array.AsReadOnly(order.Select(Get).ToArray());
    }

    internal BusinessBlockNode Get(string id) => declarations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
    public IReadOnlyList<string> Children(string parentId) => children.TryGetValue(parentId, out var result)
        ? result : throw new KeyNotFoundException($"Unknown business block parent: {parentId}");
}
