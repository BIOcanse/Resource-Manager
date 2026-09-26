using System.Text;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Diagnostics;

internal static class DebugDiagnosticLogStorage
{
    private const int MaximumTailLines = 1000;
    private const long MaximumReadBytes = 2L * 1024 * 1024;

    public static string ResolvePath(string contentRootPath)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(contentRootPath);
        return Path.Combine(packageRoot, "Config", "Diagnostics", "DebugLogs", "debug-log.jsonl");
    }

    public static IReadOnlyList<string> ReadTail(string path, int requestedLines)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lineCount = Math.Clamp(requestedLines, 1, MaximumTailLines);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaximumReadBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        if (start > 0)
        {
            _ = reader.ReadLine();
        }

        var queue = new Queue<string>(lineCount);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == lineCount)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return queue.ToArray();
    }
}
