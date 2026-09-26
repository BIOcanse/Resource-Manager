namespace ResourceManager.App.Domain.ProcessAttribution;

public sealed record RuntimeProcessIdentity(
    int ProcessId,
    int? ParentProcessId,
    string Name,
    string? ExecutablePath,
    bool IsSelfDescendant,
    string? FileDescription = null,
    string? ProductName = null,
    string? CompanyName = null,
    string? ApplicationUserModelId = null,
    string? WindowApplicationUserModelId = null,
    string? WindowTitle = null,
    long? StartKey = null);
