namespace ResourceManager.Launcher;

// Sole visible migration declaration for the previously inline behavior values.
// Maintenance connects this input to public FreedomPoint declarations. Native
// code has no fallback/default values; callers supply the entire input once.
internal readonly record struct ServiceActionResultParameters(
    uint ParentTimeoutMilliseconds, uint ConnectTimeoutMilliseconds,
    uint MaximumFrameBytes, uint TruncateUtf16Units,
    uint InputBufferBytes, uint OutputBufferBytes)
{
    internal static ServiceActionResultParameters Baseline => new(100_000, 3_000, 16_384, 4_000, 4_096, 4_096);
}
