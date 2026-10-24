using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.ProcessIdentity;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal sealed record PortableProcessCandidate(
    int ProcessId,
    int? ParentProcessId = null,
    string? ProcessName = null,
    string? ExecutablePath = null,
    long? StartKey = null,
    long? StartedNoLaterThan = null)
{
    internal bool HasExited { get; set; }
}

internal sealed class WindowsPortableProcessIdentityReader
{
    private readonly WindowsProcessBasicMetricNativeApi api = new();
    public RuntimeProcessIdentity? TryRead(PortableProcessCandidate candidate)
    {
        if (!OperatingSystem.IsWindows() || candidate.ProcessId <= 4 || candidate.HasExited)
        {
            return null;
        }

        var handle = api.Open(candidate.ProcessId);
        if (handle == IntPtr.Zero)
        {
            candidate.HasExited = Marshal.GetLastWin32Error() == 87; // ERROR_INVALID_PARAMETER: no such PID.
            return null;
        }
        try
        {
            if (!WindowsProcessBasicMetricNativeApi.GetProcessTimes(handle, out var created, out var exited, out _, out _))
                return null;
            var birth = created.ToUInt64();
            if (exited.ToUInt64() != 0 || candidate.StartKey is > 0 && birth != (ulong)candidate.StartKey.Value
                || candidate.StartedNoLaterThan is > 0 && birth > (ulong)candidate.StartedNoLaterThan.Value)
            {
                candidate.HasExited = true;
                return null;
            }
            if (birth == 0 || birth > long.MaxValue) return null;
            var executablePath = api.ImagePath(handle) ?? NormalizeCandidatePath(candidate.ExecutablePath);
            if (string.IsNullOrWhiteSpace(executablePath)) return null;
            var processName = Clean(Path.GetFileNameWithoutExtension(executablePath)) ?? Clean(candidate.ProcessName);
            if (processName is null) return null;
            var metadata = ReadMetadata(executablePath);
            if (!WindowsProcessBasicMetricNativeApi.GetProcessTimes(handle, out _, out exited, out _, out _)) return null;
            if (exited.ToUInt64() != 0) { candidate.HasExited = true; return null; }
            return new RuntimeProcessIdentity(candidate.ProcessId, candidate.ParentProcessId,
                processName, executablePath, IsSelfDescendant: false,
                metadata.FileDescription, metadata.ProductName, metadata.CompanyName, StartKey: (long)birth);
        }
        finally { api.Close(handle); }
    }

    private static string? NormalizeCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim().Trim('"');
        return Path.IsPathFullyQualified(value) && File.Exists(value)
            ? Path.GetFullPath(value)
            : null;
    }

    private static ProcessFileMetadata ReadMetadata(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new ProcessFileMetadata(
                Clean(version.FileDescription),
                Clean(version.ProductName),
                Clean(version.CompanyName));
        }
        catch (Exception ex) when (ex is ArgumentException
            or FileNotFoundException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return ProcessFileMetadata.Empty;
        }
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record ProcessFileMetadata(
        string? FileDescription,
        string? ProductName,
        string? CompanyName)
    {
        public static ProcessFileMetadata Empty { get; } = new(null, null, null);
    }
}
