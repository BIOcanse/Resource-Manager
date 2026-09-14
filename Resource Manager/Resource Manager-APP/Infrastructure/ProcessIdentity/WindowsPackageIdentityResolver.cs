using System.ComponentModel;
using System.Text;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsPackageIdentityResolver : IRuntimePackageIdentityResolver
{
    private static readonly string? WindowsSystemApps = NormalizePath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SystemApps"));

    private static readonly string? ProgramFilesWindowsApps = NormalizePath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsApps"));

    private static readonly string? ProgramFilesX86WindowsApps = NormalizePath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "WindowsApps"));

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
    {
        var processPath = NormalizePath(process.ExecutablePath);
        if (processPath is not null && IsUnderKnownRoot(processPath, WindowsSystemApps))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var pathPackageRoot = TryGetWindowsAppsPackageRoot(processPath);
        var packageResult = pathPackageRoot is null
            ? TryGetPackageFullName(process.ProcessId)
            : new PackageNameResult(
                PackageNameStatus.Matched,
                Path.GetFileName(pathPackageRoot));

        if (packageResult.Status == PackageNameStatus.Unavailable)
        {
            return RuntimeAttributionObservation.Unavailable;
        }
        if (packageResult.Status == PackageNameStatus.NoMatch
            || string.IsNullOrWhiteSpace(packageResult.Value))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var packageName = ExtractPackageName(packageResult.Value);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var rootPaths = pathPackageRoot is null ? [] : new[] { pathPackageRoot };
        return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
            $"appx:{NormalizeId(packageName)}",
            FormatPackageName(packageName),
            SoftwareKinds.RuntimePackage,
            SoftwareText.WindowsApp,
            rootPaths));
    }

    private static PackageNameResult TryGetPackageFullName(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return new PackageNameResult(PackageNameStatus.Unavailable, null);
        }

        try
        {
            uint length = 0;
            var result = NativeMethods.GetPackageFullName(handle, ref length, null);
            if (result == NativeMethods.AppmodelErrorNoPackage || length == 0)
            {
                return result == NativeMethods.AppmodelErrorNoPackage
                    ? new PackageNameResult(PackageNameStatus.NoMatch, null)
                    : new PackageNameResult(PackageNameStatus.Unavailable, null);
            }

            if (result != NativeMethods.ErrorInsufficientBuffer)
            {
                return new PackageNameResult(PackageNameStatus.Unavailable, null);
            }

            var builder = new StringBuilder((int)length);
            result = NativeMethods.GetPackageFullName(handle, ref length, builder);
            return result == NativeMethods.ErrorSuccess
                ? new PackageNameResult(PackageNameStatus.Matched, builder.ToString())
                : result == NativeMethods.AppmodelErrorNoPackage
                    ? new PackageNameResult(PackageNameStatus.NoMatch, null)
                    : new PackageNameResult(PackageNameStatus.Unavailable, null);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentOutOfRangeException)
        {
            return new PackageNameResult(PackageNameStatus.Unavailable, null);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private readonly record struct PackageNameResult(
        PackageNameStatus Status,
        string? Value);

    private enum PackageNameStatus : byte
    {
        NoMatch = 1,
        Unavailable = 2,
        Matched = 3
    }

    private static string? TryGetWindowsAppsPackageRoot(string? processPath)
    {
        if (processPath is null)
        {
            return null;
        }

        return TryGetWindowsAppsPackageRoot(processPath, ProgramFilesWindowsApps)
            ?? TryGetWindowsAppsPackageRoot(processPath, ProgramFilesX86WindowsApps);
    }

    private static string? TryGetWindowsAppsPackageRoot(string processPath, string? windowsAppsRoot)
    {
        if (windowsAppsRoot is null || !IsSameOrUnder(processPath, windowsAppsRoot))
        {
            return null;
        }

        var relative = Path.GetRelativePath(windowsAppsRoot, processPath);
        var separatorIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separatorIndex <= 0)
        {
            return null;
        }

        return NormalizePath(Path.Combine(windowsAppsRoot, relative[..separatorIndex]));
    }

    private static string ExtractPackageName(string packageFullName)
    {
        var packageNameEnd = packageFullName.IndexOf('_');
        return packageNameEnd > 0 ? packageFullName[..packageNameEnd] : packageFullName;
    }

    private static string FormatPackageName(string packageName)
    {
        return string.Join(' ', packageName
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0));
    }

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderKnownRoot(string candidate, string? root)
    {
        return root is not null && IsSameOrUnder(candidate, root);
    }
}
