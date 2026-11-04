using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Domain.ResourceTable;

public static class ResourceTableColumnIds
{
    public const string Name = "name";
    public const string ProcessId = "pid";
    public const string Status = "status";
    public const string User = "user";
    public const string Architecture = "architecture";
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";
    public const string Network = "network";
}

// Legacy provider diagnostics still consume these types; no layout defaults or
// dashboard persistence depend on them.
public sealed record ResourceTableColumn(string Id, string Unit, bool Visible, bool Sortable, double Width);
public sealed record ResourceTableProviderState(string Id, string State, string Message);
