using System.Diagnostics;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Domain.LocalSystem;

namespace ResourceManager.App.Infrastructure.LocalSystem;

public sealed class WindowsExplorerPathOpener : ILocalPathOpener
{
    public LocalPathOpenResult Open(LocalPathOpenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException("路径不能为空。");
        }

        var normalizedPath = NormalizePath(request.Path);
        var fileExists = File.Exists(normalizedPath);
        var directoryExists = Directory.Exists(normalizedPath);
        if (!fileExists && !directoryExists)
        {
            throw new InvalidOperationException("路径不存在。");
        }

        var selectFile = request.Select || fileExists;
        var openedPath = selectFile
            ? normalizedPath
            : Path.TrimEndingDirectorySeparator(normalizedPath);
        var arguments = selectFile
            ? $"/select,{Quote(openedPath)}"
            : Quote(openedPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true
        });

        return new LocalPathOpenResult(
            normalizedPath,
            openedPath,
            selectFile ? "Select" : "Open",
            selectFile ? "已在资源管理器中定位路径。" : "已在资源管理器中打开目录。");
    }

    private static string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("只支持绝对本地路径。");
        }

        return fullPath;
    }

    private static string Quote(string value)
    {
        if (value.Contains('"'))
        {
            throw new InvalidOperationException("路径不能包含双引号。");
        }

        return $"\"{value}\"";
    }
}
