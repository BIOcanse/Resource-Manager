namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Static control tree and separate data declarations. Data edges never control execution.</summary>
public class BusinessBlockGraph
{
    public int Program { get; }
    private readonly Dictionary<int, BusinessBlockNode> declarations = new();
    private readonly Dictionary<int, IReadOnlyList<int>> children = new();
    public IReadOnlyList<BusinessBlockNode> Nodes { get; }
    public IReadOnlyList<int> BlockIds { get; }

    public BusinessBlockGraph(int program, IEnumerable<BusinessBlockNode> source)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(program, 1);
        Program = program;
        foreach (var declaration in source)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(declaration.Id, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(declaration.ParentId, 1);
            if (declaration.Id == Program || !declarations.TryAdd(declaration.Id, declaration with
                {
                    Inputs = Array.AsReadOnly(declaration.Inputs.ToArray())
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
        var order = new List<int>(declarations.Count);
        var pending = new Queue<int>(Children(Program));
        while (pending.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var child in Children(id)) pending.Enqueue(child);
        }
        if (order.Count != declarations.Count) throw new InvalidOperationException("Business block control cycle.");
        BlockIds = order.AsReadOnly();
        Nodes = Array.AsReadOnly(order.Select(Get).ToArray());
    }

    internal BusinessBlockNode Get(int id) => declarations.TryGetValue(id, out var value)
        ? value : throw new KeyNotFoundException($"Unknown business block: {id}");
    public IReadOnlyList<int> Children(int parentId) => children.TryGetValue(parentId, out var result)
        ? result : throw new KeyNotFoundException($"Unknown business block parent: {parentId}");
}
