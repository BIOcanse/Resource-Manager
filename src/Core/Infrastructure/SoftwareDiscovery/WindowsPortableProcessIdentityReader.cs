using System.Diagnostics;
using System.Text;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal sealed record PortableProcessCandidate(
    int ProcessId,
    int? ParentProcessId = null,
    string? ProcessName = null,
    string? ExecutablePath = null);

internal sealed class WindowsPortableProcessIdentityReader
{
    public RuntimeProcessIdentity? TryRead(PortableProcessCandidate candidate)
    {
        if (!OperatingSystem.IsWindows() || candidate.ProcessId <= 0)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(candidate.ProcessId);
            var processName = Clean(process.ProcessName)
                ?? Clean(candidate.ProcessName)
                ?? string.Empty;
            var executablePath = TryReadExecutablePath(process)
                ?? NormalizeCandidatePath(candidate.ExecutablePath);
            if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var metadata = ReadMetadata(executablePath);
            return new RuntimeProcessIdentity(
                candidate.ProcessId,
                candidate.ParentProcessId,
                processName,
                executablePath,
                IsSelfDescendant: false,
                metadata.FileDescription,
                metadata.ProductName,
                metadata.CompanyName,
                StartKey: TryReadProcessStartKey(process));
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? TryReadExecutablePath(Process process)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            try
            {
                var size = 1024;
                var builder = new StringBuilder(size);
                if (NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size))
                {
                    return builder.ToString();
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private static long? TryReadProcessStartKey(Process process)
    {
        try
        {
            return process.StartTime.ToFileTimeUtc();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
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
