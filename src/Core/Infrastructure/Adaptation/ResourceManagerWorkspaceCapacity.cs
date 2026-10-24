namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed record ResourceManagerWorkspaceCapacity(
    string Uid, string WorkArea, string Rows, ulong Bytes, bool InLedger);
