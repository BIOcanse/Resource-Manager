using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.Indexing;

internal static class WindowsFileIndexCallerAccess
{
    internal static bool TryOpenCallerToken(HttpContext context, out SafeAccessTokenHandle token)
    {
        if (!WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out token)) return false;
        if (caller.ProcessId != Environment.ProcessId) return true;
        token.Dispose();
        token = SafeAccessTokenHandle.InvalidHandle;
        return false;
    }

    internal static IReadOnlyList<SoftwareFileSearchResult> FilterSearch(
        SafeAccessTokenHandle token, IReadOnlyList<SoftwareFileSearchResult> results)
        => WindowsIdentity.RunImpersonated(token, () =>
        {
            var access = CreatePathFilterCore();
            var visible = new List<SoftwareFileSearchResult>();
            foreach (var result in results)
            {
                if (access(result.RootPath, result.RelativePath)) visible.Add(result);
            }
            return (IReadOnlyList<SoftwareFileSearchResult>)visible;
        });

    internal static Func<string, string?, bool> CreatePathFilter(SafeAccessTokenHandle token)
    {
        var filter = CreatePathFilterCore();
        return (root, relative) => WindowsIdentity.RunImpersonated(token, () => filter(root, relative));
    }

    private static Func<string, string?, bool> CreatePathFilterCore()
    {
        // Request-local cache; never share one user's visibility with another request.
        var directories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool CanList(string path)
        {
            if (!directories.TryGetValue(path, out var allowed))
            {
                allowed = CanReadRoot(path);
                if (directories.Count < 4096) directories[path] = allowed;
            }
            return allowed;
        }
        return (root, relative) =>
        {
            try
            {
                var absoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (!CanList(absoluteRoot)) return false;
                if (relative is null) return true;
                if (Path.IsPathRooted(relative)) return false;
                var fullPath = Path.GetFullPath(Path.Combine(absoluteRoot, relative));
                var prefix = Path.EndsInDirectorySeparator(absoluteRoot) ? absoluteRoot : absoluteRoot + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var parent = Path.GetDirectoryName(fullPath);
                for (var depth = 0; !string.Equals(parent, absoluteRoot, StringComparison.OrdinalIgnoreCase); depth++)
                {
                    if (parent is null || depth >= 256 || !parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || !CanList(parent)) return false;
                    parent = Path.GetDirectoryName(parent);
                }
                return CanReadFile(fullPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return false; }
        };
    }

    private static bool CanReadRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return CanReadFile(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            using var handle = CreateFile(path, 0x0001, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // FILE_LIST_DIRECTORY, shared R/W/delete, OPEN_EXISTING, backup semantics.
            return !handle.IsInvalid;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private static bool CanReadFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
