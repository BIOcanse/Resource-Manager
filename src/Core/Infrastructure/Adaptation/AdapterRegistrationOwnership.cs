using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed record AdapterRegistrationOwnership(
    IReadOnlyList<string> Roots, IReadOnlyList<LoopbackApiProcess> Processes)
{
    internal static AdapterRegistrationOwnership Verify(AdapterSoftwareRegistrationRequest registration,
        AdapterRegistrationCaller caller, LoopbackApiProcess markerOwner,
        Func<int, LoopbackApiProcess?> readProcess)
    {
        if (string.IsNullOrWhiteSpace(caller.WindowsSid))
            throw new UnauthorizedAccessException("The registration caller is unavailable.");
        var roots = registration.ProgramRootPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(root => !IsExistingPathWithoutReparsePoints(root, directory: true)))
            throw new UnauthorizedAccessException("Program roots must exist without reparse points.");
        List<LoopbackApiProcess> processes = [markerOwner];
        foreach (var declaration in registration.Processes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(declaration.ExecutablePath)
                && !IsExistingPathWithoutReparsePoints(NormalizePath(declaration.ExecutablePath), directory: false))
                throw new UnauthorizedAccessException("A declared executable does not exist or uses a reparse point.");
            if (declaration.ProcessId is not { } pid) continue;
            var process = readProcess(pid);
            if (process is null || !string.IsNullOrWhiteSpace(declaration.ExecutablePath)
                && !StringComparer.OrdinalIgnoreCase.Equals(process.ExecutablePath, NormalizePath(declaration.ExecutablePath)))
                throw new UnauthorizedAccessException("A declared process is unavailable or its image does not match.");
            processes.Add(process);
        }
        foreach (var process in processes)
        {
            if ((!caller.IsAdministrator && process.Caller.WindowsSid != caller.WindowsSid)
                || !roots.Any(root => IsWithinRoot(process.ExecutablePath, root))
                || !IsExistingPathWithoutReparsePoints(process.ExecutablePath, directory: false))
                throw new UnauthorizedAccessException("The resource endpoint or a declared process is not owned by this registration.");
        }
        if (roots.Any(root => !processes.Any(process => IsWithinRoot(process.ExecutablePath, root))))
            throw new UnauthorizedAccessException("Each program root needs an observed endpoint or declared process image.");
        return new(roots, processes.Distinct().ToArray());
    }

    internal bool StillOwned(Func<int, LoopbackApiProcess?> readProcess)
        => Roots.All(root => IsExistingPathWithoutReparsePoints(root, directory: true))
            && Processes.All(process => readProcess(process.Caller.ProcessId) == process
                && IsExistingPathWithoutReparsePoints(process.ExecutablePath, directory: false));

    internal static string NormalizePath(string path)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

    internal static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, NormalizePath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static bool IsExistingPathWithoutReparsePoints(string path, bool directory)
    {
        try
        {
            var current = NormalizePath(path);
            var first = true;
            for (var depth = 0; depth < 128; depth++)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || ((attributes & FileAttributes.Directory) != 0) != (first ? directory : true)) return false;
                var parent = Path.GetDirectoryName(current);
                if (parent is null) return true;
                current = parent;
                first = false;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
