using System.Diagnostics;
using System.IO.Compression;
using ResourceManager.ReadWrite.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class DependencyPackageInstallerTests
{
    [Fact]
    public async Task SelectedArchiveKeepsRuntimeSubdirectoriesAndModuleSelectionKeepsOnlyDeclaredFiles()
    {
        using var fixture = new PackageFixture();
        var archive = fixture.PathOf("chosen.zip");
        fixture.WriteArchive(archive, "release/RyzenSMU.bin", "release/IntelMSR.bin", "runtimes/native/transport.dll");
        var module = fixture.PathOf("modules/RyzenSMU.bin");
        var moduleResult = await new DependencyPackageInstaller().InstallAsync(new("amd", archive),
            new(Path.GetDirectoryName(module)!, ArchiveFiles: ["RyzenSMU.bin"]), default);
        Assert.Equal("filesExtracted", moduleResult.Status);
        Assert.Equal("release/RyzenSMU.bin", File.ReadAllText(module));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "modules/IntelMSR.bin")));
        var native = fixture.PathOf("whole/runtimes/native/transport.dll");
        var amd = fixture.PathOf("whole/release/RyzenSMU.bin");
        var intel = fixture.PathOf("whole/release/IntelMSR.bin");
        await new DependencyPackageInstaller().InstallAsync(new("helper", archive), new(Path.Combine(fixture.Root, "whole")), default);
        Assert.True(File.Exists(native)); Assert.True(File.Exists(amd)); Assert.True(File.Exists(intel));
    }

    [Fact]
    public async Task MissingModuleAndEscapingArchiveAreNotReportedAsInstalled()
    {
        using var fixture = new PackageFixture();
        var package = fixture.PathOf("missing.zip");
        fixture.WriteArchive(package, "unrelated.txt");
        var installer = new DependencyPackageInstaller();
        var destination = Path.Combine(fixture.Root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(new("module", package), new(destination, ArchiveFiles: ["IntelMSR.bin"]), default));
        Assert.False(Directory.Exists(destination));
        var escaping = fixture.PathOf("escaping.zip");
        fixture.WriteArchive(escaping, "../escaped.txt");
        await Assert.ThrowsAsync<IOException>(async () => await installer.InstallAsync(new("helper", escaping), new(destination), default));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.txt")));
        if (Directory.Exists(destination)) Directory.Delete(destination);
    }

    [Fact]
    public async Task InstallerLaunchAndCompletionStayDistinctAndCancellationDoesNotLaunch()
    {
        using var fixture = new PackageFixture();
        var package = fixture.PathOf("chosen.exe");
        File.WriteAllText(package, "recorded installer, never executed");
        var working = fixture.PathOf("target/placeholder.txt");
        var launches = new List<(ProcessStartInfo Start, bool Wait)>();
        var installer = new DependencyPackageInstaller((start, wait, token) =>
        { token.ThrowIfCancellationRequested(); launches.Add((start, wait)); return ValueTask.CompletedTask; });
        var options = new DependencyInstallOptions(Path.GetDirectoryName(working)!, true, "/selected");
        Assert.Equal("installerStarted", (await installer.InstallAsync(new("driver", package), options, default)).Status);
        Assert.Equal("installerCompleted", (await installer.InstallAsync(new("driver", package), options with { WaitForExit = true }, default)).Status);
        Assert.Equal(new[] { false, true }, launches.Select(static item => item.Wait));
        Assert.All(launches, entry =>
        {
            Assert.Equal("runas", entry.Start.Verb); Assert.True(entry.Start.UseShellExecute);
            Assert.Equal(ProcessWindowStyle.Hidden, entry.Start.WindowStyle);
            Assert.Equal(package, entry.Start.FileName); Assert.Equal("/selected", entry.Start.Arguments);
        });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await installer.InstallAsync(new("driver", package), options, cancellation.Token));
        Assert.Equal(2, launches.Count);
    }

    [Fact]
    public async Task DefaultInstallerRunnerWaitsForTheSelectedProcessAndRejectsItsFailureCode()
    {
        using var fixture = new PackageFixture();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        // Exercise the real process runner with a command that only returns an
        // exit code. It installs nothing and does not open a hardware provider.
        var installer = new DependencyPackageInstaller();
        Assert.Equal("installerCompleted", (await installer.InstallAsync(new("recorded", executable),
            new(fixture.Root, Arguments: "/c exit /b 0", WaitForExit: true), default)).Status);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await installer.InstallAsync(new("recorded", executable),
            new(fixture.Root, Arguments: "/c exit /b 7", WaitForExit: true), default));
        Assert.Contains("exit code 7", failure.Message);
    }

    private sealed class PackageFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-selected-package-" + Guid.NewGuid().ToString("N"))).FullName;
        private readonly List<string> files = [];
        private readonly HashSet<string> directories = [];
        internal string PathOf(string relative)
        {
            var path = Path.Combine(Root, relative); files.Add(path);
            var parent = Path.GetDirectoryName(path)!;
            while (parent != Root) { directories.Add(parent); parent = Path.GetDirectoryName(parent)!; }
            return path;
        }
        internal void WriteArchive(string path, params string[] entries)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var name in entries) { using var output = new StreamWriter(archive.CreateEntry(name).Open()); output.Write(name); }
        }
        public void Dispose()
        {
            foreach (var file in files) File.Delete(file);
            foreach (var directory in directories.OrderByDescending(static item => item.Length))
                if (Directory.Exists(directory)) Directory.Delete(directory);
            Directory.Delete(Root);
        }
    }
}
