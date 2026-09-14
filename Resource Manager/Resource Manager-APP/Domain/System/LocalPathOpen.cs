namespace ResourceManager.App.Domain.LocalSystem;

public sealed record LocalPathOpenRequest(
    string Path,
    bool Select = false);

public sealed record LocalPathOpenResult(
    string Path,
    string OpenedPath,
    string Mode,
    string Message);
