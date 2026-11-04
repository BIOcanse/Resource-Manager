namespace ResourceManager.Shared.Runtime.Sessions;

public static class SessionAgentPackageRoot
{
    // Installed packages use Bin or the legacy Current payload directory. A standalone
    // final image contains sibling executable directories directly under its own root.
    public static string Resolve(string executableDirectory, string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);
        var payload = new DirectoryInfo(Path.GetFullPath(executableDirectory)).Parent
            ?? throw new InvalidDataException("The session agent has no package payload directory.");
        return payload.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase)
            || payload.Name.Equals("Current", StringComparison.OrdinalIgnoreCase)
                ? payload.Parent?.FullName ?? throw new InvalidDataException("The installed payload has no package root.")
                : payload.FullName;
    }
}
