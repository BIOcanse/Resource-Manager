namespace ResourceManager.App.Domain.Software;

public sealed record InstalledSoftwareEntry(
    string Id,
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    IReadOnlyList<string> RootPaths,
    string? UninstallString,
    string RegistryPath,
    ulong? ReportedSizeBytes = null)
{
    public InstalledSoftwareEntry(string Id, string Name, string? Version, string? Publisher,
        string? InstallLocation, IReadOnlyList<string> RootPaths, string? UninstallString, string RegistryPath)
        : this(Id, Name, Version, Publisher, InstallLocation, RootPaths, UninstallString, RegistryPath, null) { }

    public void Deconstruct(out string Id, out string Name, out string? Version, out string? Publisher,
        out string? InstallLocation, out IReadOnlyList<string> RootPaths, out string? UninstallString, out string RegistryPath)
    {
        Id = this.Id; Name = this.Name; Version = this.Version; Publisher = this.Publisher;
        InstallLocation = this.InstallLocation; RootPaths = this.RootPaths;
        UninstallString = this.UninstallString; RegistryPath = this.RegistryPath;
    }
}

