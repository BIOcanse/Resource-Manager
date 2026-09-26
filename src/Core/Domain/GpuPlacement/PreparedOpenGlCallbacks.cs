namespace ResourceManager.App.Domain.GpuPlacement;

public sealed class PreparedOpenGlCallbacks
{
    private readonly byte[] bytes;
    internal PreparedOpenGlCallbacks(ReadOnlySpan<byte> source) => bytes = source.ToArray();
    internal ReadOnlyMemory<byte> Bytes => bytes;
}
