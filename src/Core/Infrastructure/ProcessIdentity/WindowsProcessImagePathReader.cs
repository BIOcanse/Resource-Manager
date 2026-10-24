using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

/// <summary>Reads an image path through ReadWriteCore after binding the query to this process instance.</summary>
internal static class WindowsProcessImagePathReader
{
    private static readonly WindowsProcessBasicMetricReader Reader = new();

    internal static string? TryRead(Process process)
        => TryRead(process, static (processId, creationFileTime) =>
            Reader.Read(processId, creationFileTime, ProcessMetricReadMask.ImagePath).ImagePath);

    internal static string? TryRead(int processId, long creationFileTime)
        => processId > 0 && creationFileTime > 0
            ? Reader.Read(processId, creationFileTime, ProcessMetricReadMask.ImagePath).ImagePath
            : null;

    internal static string? TryRead(Process process, Func<int, long, string?> readImagePath)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readImagePath);
        try
        {
            var processId = process.Id;
            var creationFileTime = process.StartTime.ToFileTimeUtc();
            if (processId <= 0 || creationFileTime <= 0)
            {
                return null;
            }

            return TryRead(processId, creationFileTime, readImagePath);
        }
        catch (Exception error) when (error is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? TryRead(int processId, long creationFileTime,
        Func<int, long, string?> readImagePath)
    {
        ArgumentNullException.ThrowIfNull(readImagePath);
        if (processId <= 0 || creationFileTime <= 0) return null;
        try { return readImagePath(processId, creationFileTime); }
        catch (Exception error) when (error is ArgumentException
            or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }
}
