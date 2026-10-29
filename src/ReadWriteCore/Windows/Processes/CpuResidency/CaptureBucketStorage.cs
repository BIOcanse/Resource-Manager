namespace ResourceManager.ReadWrite.Windows.Processes;

// The active window/run owns the chunks. Aggregates retain only a position.
// Allocation policy may borrow a region from its enclosing lexical Ledger scope.
internal sealed class CaptureBucketStorage(Func<int, CaptureBucketMemory>? allocate = null) : IDisposable
{
    private readonly Dictionary<long, CaptureBucketMemory> chunks = [];
    private long next;
    private bool disposed;
    internal readonly record struct Position(long Id, int Length);
    internal Position Allocate(int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = checked(++next);
        var memory = (allocate ?? CaptureBucketMemory.AllocateManaged)(length);
        var position = new Position(id, length);
        try { chunks.Add(position.Id, memory); }
        catch { memory.Dispose(); throw; }
        return position;
    }
    internal Span<byte> Bytes(Position position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return chunks[position.Id].Bytes[..position.Length];
    }
    internal void Release(Position position)
    {
        if (!chunks.TryGetValue(position.Id, out var memory)) return;
        memory.Dispose();
        chunks.Remove(position.Id);
        if (chunks.Count == 0) chunks.TrimExcess();
    }
    public void Dispose()
    {
        foreach (var chunk in chunks)
        {
            chunk.Value.Dispose();
            chunks.Remove(chunk.Key);
        }
        chunks.TrimExcess();
        disposed = true;
    }
}
