using System.Collections.Immutable;
using System.Text.Json;

namespace ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

public sealed record CompiledFreedomPoint(
    string Address,
    int Index,
    string Id,
    string Description,
    string ValueType,
    string UpdateClass,
    string Status,
    string? PendingReason,
    string? ValueSource,
    string? SourcePath,
    JsonElement? Value,
    ImmutableArray<string> Consumers);

public sealed record CompiledFreedomPointFamily(
    ImmutableArray<CompiledFreedomPoint> Simple,
    ImmutableArray<CompiledFreedomPoint> Complex)
{
    public static CompiledFreedomPointFamily Empty { get; } = new([], []);
}

public sealed record CompiledFreedomPointTable(
    string Id,
    string Description,
    CompiledFreedomPointFamily Build,
    CompiledFreedomPointFamily Runtime,
    ImmutableArray<CompiledFreedomPointTable> Children);

public sealed record CompiledFreedomPointTree(
    string Namespace,
    string DeclarationSha256,
    ImmutableArray<CompiledFreedomPointTable> Children)
{
    public static CompiledFreedomPointTree Empty { get; } = new(string.Empty, string.Empty, []);

    public IEnumerable<CompiledFreedomPoint> EnumeratePoints()
    {
        var pending = new Stack<CompiledFreedomPointTable>(Children.Reverse());
        while (pending.TryPop(out var table))
        {
            foreach (var point in table.Build.Simple) yield return point;
            foreach (var point in table.Build.Complex) yield return point;
            foreach (var point in table.Runtime.Simple) yield return point;
            foreach (var point in table.Runtime.Complex) yield return point;
            for (var index = table.Children.Length - 1; index >= 0; index--)
            {
                pending.Push(table.Children[index]);
            }
        }
    }
}
