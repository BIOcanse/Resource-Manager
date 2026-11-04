using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;

namespace ResourceManager.ReadWrite.Dependencies;

/// <summary>The binding provides its directory, elevation and installer options.
/// ArchiveFiles selects runtime modules by basename; null retains the whole archive.</summary>
public sealed record DependencyInstallOptions(string InstallDirectory, bool RequiresElevation = false,
    string Arguments = "", ImmutableArray<string>? ArchiveFiles = null, bool WaitForExit = false);

/// <summary>Applies an already selected local package. Source/version selection,
/// terms confirmation and dependency readiness belong to the caller and probes.</summary>
public sealed class DependencyPackageInstaller(
    Func<ProcessStartInfo, bool, CancellationToken, ValueTask>? launchInstaller = null)
{
    private readonly Func<ProcessStartInfo, bool, CancellationToken, ValueTask> launch = launchInstaller ?? LaunchAsync;

    public async ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package,
        DependencyInstallOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(package.LocalPath);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected dependency package is missing.", path);
        var directory = Path.GetFullPath(options.InstallDirectory);
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            ExtractArchive(archive, directory, options.ArchiveFiles, cancellationToken);
            return new(package.DependencyId, "filesExtracted", null);
        }
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a ZIP, EXE or MSI dependency package.", nameof(package));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = directory,
            Arguments = options.Arguments,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = options.RequiresElevation ? "runas" : ""
        };
        await launch(start, options.WaitForExit, cancellationToken);
        return new(package.DependencyId, options.WaitForExit ? "installerCompleted" : "installerStarted", null);
    }

    public static ImmutableArray<string> ExtractArchive(ZipArchive archive, string directory,
        ImmutableArray<string>? files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (files is null)
        {
            // The BCL validates entry paths and retains runtime subdirectories.
            archive.ExtractToDirectory(directory, overwriteFiles: true);
            return [.. archive.Entries.Where(static entry => entry.Name.Length != 0).Select(static entry => entry.FullName)];
        }
        var selected = files.Value.Select(name =>
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ArgumentException("Select runtime module basenames.", nameof(files));
            var entry = archive.Entries.FirstOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return (Name: name, Entry: entry ?? throw new InvalidDataException($"The selected package does not contain {name}."));
        }).ToArray();
        Directory.CreateDirectory(directory);
        foreach (var (name, entry) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
        }
        return [.. selected.Select(static item => item.Name)];
    }

    private static async ValueTask LaunchAsync(ProcessStartInfo start, bool waitForExit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The dependency installer did not start.");
        if (!waitForExit) return;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"The dependency installer returned exit code {process.ExitCode}.");
    }
}
