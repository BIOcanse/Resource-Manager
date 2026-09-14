using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

internal sealed class FreedomPointCompilation
{
    private readonly CompiledFreedomPointTree declarations;
    private readonly JsonElement profile;
    private readonly JsonElement cpuConfiguration;
    private readonly Dictionary<string, CompiledFreedomPoint> points;
    private readonly Dictionary<string, HashSet<string>> consumers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

    internal FreedomPointCompilation(CompiledFreedomPointTree declarations, JsonElement profile, JsonElement cpuConfiguration)
    {
        this.declarations = declarations;
        this.profile = profile;
        this.cpuConfiguration = cpuConfiguration;
        points = declarations.EnumeratePoints().ToDictionary(static point => point.Address, StringComparer.Ordinal);
    }

    public T Consume<T>(string address, string consumer)
    {
        if (!points.TryGetValue(address, out var point) || point.Status != "active")
        {
            throw new InvalidDataException($"Freedom point '{address}' is not declared active.");
        }
        if (!point.Consumers.Contains(consumer, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Freedom point '{address}' does not declare consumer '{consumer}'.");
        }
        var value = ResolveValue(point);
        FreedomPointRegistry.ValidateValue(value, point.ValueType, address);
        T result;
        try
        {
            result = value.Deserialize<T>(FreedomPointRegistry.JsonOptions)
                ?? throw new JsonException("A compiled freedom point cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Freedom point '{address}' does not satisfy its consumer type.", exception);
        }
        if (!consumers.TryGetValue(address, out var actualConsumers))
        {
            actualConsumers = new HashSet<string>(StringComparer.Ordinal);
            consumers.Add(address, actualConsumers);
            values.Add(address, value.Clone());
        }
        actualConsumers.Add(consumer);
        return result;
    }

    public CompiledFreedomPointTree Complete()
    {
        foreach (var point in points.Values)
        {
            if (point.Status == "active" && (!consumers.TryGetValue(point.Address, out var actual)
                || !actual.SetEquals(point.Consumers)))
            {
                throw new InvalidDataException($"Freedom point '{point.Address}' has an unconsumed declaration.");
            }
        }

        var tables = new Dictionary<CompiledFreedomPointTable, CompiledFreedomPointTable>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(CompiledFreedomPointTable Table, bool ChildrenVisited)>();
        foreach (var root in declarations.Children.Reverse()) pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            if (!item.ChildrenVisited)
            {
                pending.Push((item.Table, true));
                foreach (var child in item.Table.Children.Reverse()) pending.Push((child, false));
                continue;
            }
            tables.Add(item.Table, item.Table with
            {
                Build = PublishFamily(item.Table.Build),
                Runtime = PublishFamily(item.Table.Runtime),
                Children = item.Table.Children.Select(child => tables[child]).ToImmutableArray()
            });
        }
        return declarations with { Children = declarations.Children.Select(root => tables[root]).ToImmutableArray() };
    }

    private CompiledFreedomPointFamily PublishFamily(CompiledFreedomPointFamily family)
        => new(PublishValues(family.Simple), PublishValues(family.Complex));

    private ImmutableArray<CompiledFreedomPoint> PublishValues(ImmutableArray<CompiledFreedomPoint> items)
        => items.Select(point => point.Status == "active"
            ? point with { Value = values[point.Address] }
            : point).ToImmutableArray();

    private JsonElement ResolveValue(CompiledFreedomPoint point)
    {
        if (point.ValueSource == "registry") return point.Value!.Value;
        var current = point.ValueSource == "cpu_configuration" ? cpuConfiguration : profile;
        foreach (var name in point.SourcePath!.Split('/').Skip(1))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                throw new InvalidDataException($"Freedom point '{point.Address}' source '{point.SourcePath}' is missing.");
            }
        }
        return current;
    }
}
