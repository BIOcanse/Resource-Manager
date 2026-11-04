using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.Monitoring;

// Synchronization belongs to the data item's publishing owner.
internal sealed class DataHistoryBuffer<T>
{
    private T[] values;
    private int oldest;

    internal DataHistoryBuffer(int retainedRounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedRounds, 1);
        values = new T[retainedRounds];
    }

    internal int Count { get; private set; }

    internal int RetainedRounds => values.Length;

    internal void Append(T value)
    {
        values[(oldest + Count) % values.Length] = value;
        if (Count < values.Length)
        {
            Count++;
        }
        else
        {
            oldest = (oldest + 1) % values.Length;
        }
    }

    internal void Resize(int retainedRounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedRounds, 1);
        if (retainedRounds == values.Length)
        {
            return;
        }
        var replacement = new T[retainedRounds];
        var retained = Math.Min(Count, retainedRounds);
        var first = Count - retained;
        for (var index = 0; index < retained; index++)
        {
            replacement[index] = values[(oldest + first + index) % values.Length];
        }
        values = replacement;
        oldest = 0;
        Count = retained;
    }

    internal ImmutableArray<T> Read()
    {
        var result = ImmutableArray.CreateBuilder<T>(Count);
        for (var index = 0; index < Count; index++)
        {
            result.Add(values[(oldest + index) % values.Length]);
        }
        return result.MoveToImmutable();
    }
}
