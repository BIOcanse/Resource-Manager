using System.Collections.Concurrent;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Authoritative scheduling facts. Each fact has its own lock and declared readers.</summary>
internal sealed class BusinessBlockFactAuthority
{
    private readonly Dictionary<string, Cell> cells = new(StringComparer.Ordinal);
    public void Declare(string key, bool value = false) => cells.TryAdd(key, new(key, value));
    public void Subscribe(string key, BusinessBlockDecisionNode node) => Get(key).Readers.Add(node);
    public BusinessBlockFact Read(string key)
    {
        var cell = Get(key);
        lock (cell.Sync) return cell.Value;
    }
    public IReadOnlyList<BusinessBlockFact> ReadAll() => cells.Keys.Select(Read).ToArray();
    public Task Publish(string key, bool value, BusinessBlockReason reason)
        => Publish(key, () => new(key, value, [reason]), reason);

    public Task PublishDemand(string id, ConcurrentDictionary<string, BusinessBlockDemand> demands, BusinessBlockReason reason)
        => Publish(BusinessBlockScheduler.DemandFact(id), () =>
        {
            var current = demands.Values.Where(demand => demand.BlockId == id).Select(demand => demand.Reason).ToArray();
            return new(BusinessBlockScheduler.DemandFact(id), current.Length > 0, current);
        }, reason);

    private Task Publish(string key, Func<BusinessBlockFact> readCurrent, BusinessBlockReason reason)
    {
        var cell = Get(key);
        lock (cell.Sync) cell.Value = readCurrent();
        return Task.WhenAll(cell.Readers.Select(node => node.Wake(key, reason)));
    }
    private Cell Get(string key) => cells.TryGetValue(key, out var cell) ? cell
        : throw new KeyNotFoundException($"Undeclared business block fact: {key}");
    private sealed class Cell(string key, bool value)
    {
        public object Sync { get; } = new();
        public BusinessBlockFact Value { get; set; } = new(key, value, []);
        public List<BusinessBlockDecisionNode> Readers { get; } = [];
    }
}
