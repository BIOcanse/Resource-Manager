namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed record NativeOperationCanonicalImage(
    NativeOperationPersistenceHeader Header,
    NativeOperationOutput[] Records);

