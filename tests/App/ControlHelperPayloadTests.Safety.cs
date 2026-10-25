using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperPayloadTests
{
    [Theory]
    [InlineData("disk-full")]
    [InlineData("corrupt-entry")]
    [InlineData("suspension")]
    public async Task FailedStagingExtractionOrSuspensionPreservesWorkingPayloadAndRecord(string failure)
    {
        await using var fixture = new PayloadSafetyFixture(corruptArchive: failure == "corrupt-entry");
        Assert.Equal("old release stable", (await fixture.Bridge.SendAsync("read", null, default))!.Value.GetProperty("version").GetString());
        var oldRecord = File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live));
        var suspensionCalls = 0;
        var acquisition = new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge)
        {
            OpenStagedFile = failure == "disk-full" ? path => new DiskFullStream(path) : null,
            SuspendHelper = token =>
            {
                suspensionCalls++;
                if (failure == "suspension") throw new OperationCanceledException("Recorded suspension cancellation before input closes.");
                return fixture.Bridge.SuspendForInstallationAsync(token);
            }
        };
        var revisions = new ManagementInventoryRevisionOwner();
        var manager = fixture.Manager(acquisition, revisions);
        Assert.Equal(ComponentStatusStates.UpdateAvailable, (await manager.GetStatusAsync("hardware-bridge", default))!.State);
        var result = await manager.InstallAsync("hardware-bridge", true, "verified", default);
        Assert.Equal("runtimeFilesUnavailable", result.State);
        Assert.Equal(failure == "suspension" ? 1 : 0, suspensionCalls);
        Assert.Equal("0", revisions.Current.ComponentRevision);
        Assert.Equal(oldRecord, File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live)));
        Assert.All(fixture.Files, name => Assert.Equal("old release stable", File.ReadAllText(Path.Combine(fixture.Live, name))));
        Assert.Equal("old release stable", (await fixture.Bridge.SendAsync("read", null, default))!.Value.GetProperty("version").GetString());
        Assert.Equal(1, fixture.Starts);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(fixture.Live)!, ".hardware-bridge.stage-*"));
        Assert.Equal(ComponentStatusStates.UpdateAvailable, (await manager.GetStatusAsync("hardware-bridge", default))!.State);
        Assert.Equal("0", revisions.Current.ComponentRevision);
    }

    [Fact]
    public async Task CanceledOwnedSuspensionAfterEofRestartsTheUnchangedInstallation()
    {
        await using var fixture = new PayloadSafetyFixture(waitAfterEof: true);
        Assert.NotNull(await fixture.Bridge.SendAsync("read", null, default));
        var record = File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live));
        var acquisition = new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge)
        {
            SuspendHelper = async _ =>
            {
                using var cancellation = new CancellationTokenSource();
                var pending = fixture.Bridge.SuspendForInstallationAsync(cancellation.Token).AsTask();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(fixture.EofObserved) && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(File.Exists(fixture.EofObserved));
                cancellation.Cancel();
                return await pending;
            }
        };
        var result = await fixture.Manager(acquisition).InstallAsync("hardware-bridge", true, "verified", default);
        Assert.Equal("runtimeFilesUnavailable", result.State);
        Assert.Equal(record, File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live)));
        Assert.All(fixture.Files, file => Assert.Equal("old release stable", File.ReadAllText(Path.Combine(fixture.Live, file))));
        Assert.Null(await fixture.Bridge.SendAsync("read", null, default));
        Assert.Equal(1, fixture.Starts);
        File.WriteAllText(fixture.ReleaseOld, "release");
        await fixture.WaitForFirstExit();
        Assert.Equal("old release stable", (await fixture.Bridge.SendAsync("read", null, default))!.Value.GetProperty("version").GetString());
        Assert.Equal(2, fixture.Starts);
    }

    [Fact]
    public async Task SamePinLicenseRepairPreservesHealthyFilesRecordAndRunningHelper()
    {
        await using var fixture = new PayloadSafetyFixture(current: true);
        var executable = Path.Combine(fixture.Live, "ResourceManager.HardwareBridge.exe");
        File.SetLastWriteTimeUtc(executable, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var time = File.GetLastWriteTimeUtc(executable);
        var record = File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live));
        File.Delete(Path.Combine(fixture.Live, "LICENSE"));
        Assert.NotNull(await fixture.Bridge.SendAsync("read", null, default));
        var acquisition = new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge)
            { SuspendHelper = _ => throw new InvalidOperationException("LICENSE repair must not suspend a healthy helper.") };
        var manager = fixture.Manager(acquisition, new ManagementInventoryRevisionOwner());
        var result = await manager.VerifyAsync("hardware-bridge", default);
        Assert.Equal(ComponentStatusStates.Installed, result.State);
        Assert.Equal(time, File.GetLastWriteTimeUtc(executable));
        Assert.Equal(record, File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live)));
        Assert.Equal("old release stable", File.ReadAllText(executable));
        Assert.Equal("old release stable", File.ReadAllText(Path.Combine(fixture.Live, "LICENSE")));
        Assert.Equal(1, fixture.Starts);
        Assert.NotNull(await fixture.Bridge.SendAsync("read", null, default));
    }

    [Fact]
    public async Task VerifyCannotUpdateAndInstallCannotBypassTerms()
    {
        await using var fixture = new PayloadSafetyFixture();
        var definition = OptionalDependencyCatalog.Find("amd-smu-pawnio-provider")!;
        // Test the same business policy used by both the installer owner and the installed-payload branch.
        Assert.Throws<InvalidOperationException>(() => OptionalDependencyCatalog.EnsureTerms(definition, false));
        var manager = fixture.Manager(new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge));
        var before = File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live));
        var result = await manager.VerifyAsync("hardware-bridge", default);
        Assert.Equal(ComponentStatusStates.UpdateAvailable, result.State);
        Assert.False(result.Status!.CanVerify);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(before, File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live)));
    }

    [Fact]
    public async Task NestedArchiveCannotCertifyOldRootFiles()
    {
        await using var fixture = new PayloadSafetyFixture(nestedArchive: true);
        var before = File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live));
        var result = await fixture.Manager(new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge))
            .InstallAsync("hardware-bridge", true, "verified", default);
        Assert.Equal("runtimeFilesUnavailable", result.State);
        Assert.Equal(before, File.ReadAllBytes(DependencyReleaseRecordStore.InstalledPath(fixture.Live)));
        Assert.All(fixture.Files, file => Assert.Equal("old release stable", File.ReadAllText(Path.Combine(fixture.Live, file))));
        Assert.False(Directory.Exists(Path.Combine(fixture.Live, "subdir")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"tag\":\"v0.2.1\",\"assetName\":\"release.zip\",\"assetSha256\":\"bad\"}")]
    public async Task InvalidRecordAffectsOnlyItsComponentAndIsLogged(string json)
    {
        await using var fixture = new PayloadSafetyFixture();
        File.WriteAllText(DependencyReleaseRecordStore.InstalledPath(fixture.Live), json);
        var logger = new RecordLogger<OptionalDependencyManager>();
        var dependencies = fixture.Dependencies(logger);
        var statuses = await dependencies.GetStatusesAsync(default);
        Assert.Equal(OptionalDependencyCatalog.Definitions.Count, statuses.Count);
        Assert.Equal("update_available", Assert.Single(statuses, row => row.Definition.Id == "hardware-bridge").State);
        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, message => message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentReadBetweenReplacementAndActionPublicationDoesNotDoubleRevision()
    {
        await using var fixture = new PayloadSafetyFixture();
        var sampler = new PausingSampler();
        var revisions = new ManagementInventoryRevisionOwner();
        var manager = new ComponentManager(fixture.Dependencies(), sampler, new ProviderProbe(),
            new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge), revisions);
        _ = await manager.GetStatusAsync("hardware-bridge", default);
        var updating = manager.InstallAsync("hardware-bridge", true, "verified", default);
        await sampler.ActionReadReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ComponentStatusStates.Installed, (await manager.GetStatusAsync("hardware-bridge", default))!.State);
        Assert.Equal("0", revisions.Current.ComponentRevision);
        sampler.Release.TrySetResult();
        Assert.Equal(ComponentStatusStates.Installed, (await updating).Status!.State);
        Assert.Equal("1", revisions.Current.ComponentRevision);
        _ = await manager.GetStatusesAsync(default);
        Assert.Equal("1", revisions.Current.ComponentRevision);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("cache")]
    [InlineData("stage")]
    public async Task WritersRejectReparsePointsWithoutChangingTheirTarget(string location)
    {
        await using var fixture = new PayloadSafetyFixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var marker = Path.Combine(outside, "marker");
        File.WriteAllText(marker, "must remain untouched");
        string link;
        if (location == "root")
        {
            var saved = fixture.Live + ".saved";
            Directory.Move(fixture.Live, saved);
            link = fixture.Live;
            CreateLink(link, outside, junction: true);
            try
            {
                Assert.False((await new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge)
                    .InstallAsync(fixture.Definition, default)).Satisfied);
            }
            finally { Directory.Delete(link); Directory.Move(saved, fixture.Live); }
        }
        else if (location == "cache")
        {
            link = Path.Combine(fixture.Root, "Misc", "DependencyInstallers", "hardware-bridge");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            CreateLink(link, outside, junction: true);
            try { await Assert.ThrowsAsync<IOException>(() => fixture.Dependencies().DownloadAsync("hardware-bridge", true, "verified", default)); }
            finally { Directory.Delete(link); }
        }
        else
        {
            using var transaction = new DependencyPayloadTransaction(fixture.Live, true);
            link = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(fixture.Live)!, ".hardware-bridge.stage-*"));
            Directory.Delete(link);
            CreateLink(link, outside, junction: true);
            try
            {
                using var memory = new MemoryStream(fixture.NewArchive);
                using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
                Assert.Throws<IOException>(() => transaction.Prepare(archive, fixture.Files, [], true, null, default));
            }
            finally { Directory.Delete(link); }
        }
        Assert.Equal("must remain untouched", File.ReadAllText(marker));
        Assert.Single(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task LinkedRecordIsNotCurrentAndLinkedPayloadCannotPassNoOpVerification()
    {
        await using var fixture = new PayloadSafetyFixture(current: true);
        var recordPath = DependencyReleaseRecordStore.InstalledPath(fixture.Live);
        var record = File.ReadAllBytes(recordPath);
        var outsideRecord = Path.Combine(fixture.Root, "record-input.json");
        File.Move(recordPath, outsideRecord);
        CreateLink(recordPath, outsideRecord, junction: false);
        try { Assert.True((await fixture.Dependencies().GetStatusAsync("hardware-bridge", default))!.UpdateAvailable); }
        finally { File.Delete(recordPath); File.Move(outsideRecord, recordPath); }

        var license = Path.Combine(fixture.Live, "LICENSE");
        var outside = Path.Combine(fixture.Root, "license-input");
        File.Move(license, outside);
        CreateLink(license, outside, junction: false);
        try
        {
            Assert.False((await fixture.Dependencies().GetStatusAsync("hardware-bridge", default))!.PayloadFilesPresent);
            var result = await fixture.Manager(new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge))
                .VerifyAsync("hardware-bridge", default);
            Assert.Equal("runtimeFilesUnavailable", result.State);
            Assert.Equal(0, fixture.Handler.Requests);
            Assert.Equal(record, File.ReadAllBytes(recordPath));
        }
        finally { File.Delete(license); File.Move(outside, license); }
    }

    [Fact]
    public async Task PostPublishStatusFailureStillPublishesExactlyOneContentRevision()
    {
        await using var fixture = new PayloadSafetyFixture();
        var revisions = new ManagementInventoryRevisionOwner();
        var manager = new ComponentManager(fixture.Dependencies(), new FailingStatusSampler(), new ProviderProbe(),
            new DependencyPayloadAcquisition(fixture.Client, fixture.Environment, fixture.Bridge), revisions);
        _ = await manager.GetStatusAsync("hardware-bridge", default);
        await Assert.ThrowsAsync<IOException>(() => manager.InstallAsync("hardware-bridge", true, "verified", default));
        Assert.Equal("1", revisions.Current.ComponentRevision);
        Assert.Equal(ComponentStatusStates.Installed, (await manager.GetStatusAsync("hardware-bridge", default))!.State);
        Assert.Equal("1", revisions.Current.ComponentRevision);
        Assert.All(fixture.Files, file => Assert.Equal("new release stable", File.ReadAllText(Path.Combine(fixture.Live, file))));
    }

    [Fact]
    public async Task CacheWriterRejectsHardLinkedAsset()
    {
        await using var fixture = new PayloadSafetyFixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "Misc", "DependencyInstallers", "hardware-bridge")).FullName;
        var outside = Path.Combine(fixture.Root, "external-asset.zip");
        File.WriteAllBytes(outside, fixture.NewArchive);
        var asset = Path.Combine(directory, fixture.Definition.InstallerFileName);
        CreateLink(asset, outside, junction: false);
        try { await Assert.ThrowsAsync<IOException>(() => fixture.Dependencies().DownloadAsync("hardware-bridge", true, "verified", default)); }
        finally { File.Delete(asset); }
        Assert.Equal(fixture.NewArchive, File.ReadAllBytes(outside));
    }

    private static void CreateLink(string link, string target, bool junction)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", junction ? "/J" : "/H", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class PayloadSafetyFixture : IAsyncDisposable
    {
        internal readonly ResourceManager.App.Domain.Dependencies.OptionalDependencyDefinition Definition = OptionalDependencyCatalog.Find("hardware-bridge")!;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "rm-payload-safety-" + Guid.NewGuid().ToString("N"));
        internal string Live => Path.Combine(Root, "Dependencies", "hardware-bridge");
        internal string[] Files => [.. Definition.PayloadSource!.FileNames, "LegacyRuntime.dll"];
        internal readonly TestEnvironment Environment;
        internal readonly HardwareBridgeClient Bridge;
        internal readonly ArchiveHandler Handler;
        internal readonly HttpClient Client;
        internal readonly byte[] NewArchive;
        internal string EofObserved => Path.Combine(Root, "eof-observed");
        internal string ReleaseOld => Path.Combine(Root, "release-old");
        private readonly List<Process> observers = [];
        private readonly bool waitAfterEof;
        internal int Starts;

        internal PayloadSafetyFixture(bool current = false, bool corruptArchive = false, bool nestedArchive = false, bool waitAfterEof = false)
        {
            this.waitAfterEof = waitAfterEof;
            Directory.CreateDirectory(Live);
            foreach (var name in Files) File.WriteAllText(Path.Combine(Live, name), "old release stable");
            var oldArchive = Archive(Files, "old release stable");
            NewArchive = current ? oldArchive : Archive(Files.Select(name => nestedArchive ? "subdir/" + name : name), "new release stable");
            if (corruptArchive)
            {
                var offset = 30 + BitConverter.ToUInt16(NewArchive, 26) + BitConverter.ToUInt16(NewArchive, 28);
                NewArchive[offset] = (byte)((NewArchive[offset] & 0xF8) | 7); // Invalid DEFLATE block type, central directory remains intact.
            }
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(Live),
                new(current ? Definition.PayloadSource!.VerifiedTag : "v0.2.0", Definition.PayloadSource!.VerifiedAssetName,
                    Convert.ToHexString(SHA256.HashData(oldArchive))));
            Environment = new TestEnvironment(Root);
            Bridge = new HardwareBridgeClient(Environment, Start);
            Handler = new ArchiveHandler(NewArchive);
            Client = new HttpClient(Handler);
        }

        internal OptionalDependencyManager Dependencies(ILogger<OptionalDependencyManager>? logger = null)
            => new(Client, Environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(Client, NullLogger<DependencyInstallerSourceResolver>.Instance), Bridge, logger: logger);
        internal ComponentManager Manager(IDependencyPayloadAcquisition acquisition, ManagementInventoryRevisionOwner? revisions = null)
            => new(Dependencies(), new Sampler(), new ProviderProbe(), acquisition, revisions);

        private Process? Start(ProcessStartInfo start)
        {
            var wait = waitAfterEof && Starts == 0;
            var script = $$"""
                $version=[IO.File]::ReadAllText('{{start.FileName}}')
                $held=[IO.File]::Open('{{start.FileName}}',[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                while ($null -ne [Console]::In.ReadLine()) {
                    [Console]::Out.WriteLine((@{ok=$true; version=$version} | ConvertTo-Json -Compress))
                    [Console]::Out.Flush()
                }
                $held.Dispose()
                if (${{wait.ToString().ToLowerInvariant()}}) {
                    [IO.File]::WriteAllText('{{EofObserved}}','eof')
                    $deadline=[DateTime]::UtcNow.AddSeconds(10)
                    while (-not [IO.File]::Exists('{{ReleaseOld}}') -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 10 }
                }
                """;
            var info = new ProcessStartInfo(Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            var observer = Process.GetProcessById(process.Id);
            _ = observer.SafeHandle;
            observers.Add(observer);
            Starts++;
            return process;
        }

        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(ReleaseOld, "release");
            Bridge.Dispose();
            foreach (var observer in observers)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
            Client.Dispose();
            DependencyInstallPaths.DeleteOwnedTree(Root);
        }

        internal Task WaitForFirstExit() => observers[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static byte[] Archive(IEnumerable<string> files, string contents)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(file, CompressionLevel.Optimal).Open());
                writer.Write(contents);
            }
        return memory.ToArray();
    }

    private sealed class DiskFullStream(string path) : Stream
    {
        private readonly FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { file.Write(buffer, offset, Math.Min(count, 5)); throw new IOException("Recorded disk full after a partial staging write."); }
        public override void Flush() => file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
    }

    private sealed class RecordLogger<T> : ILogger<T>
    {
        internal readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class PausingSampler : IMetricSampler
    {
        internal readonly TaskCompletionSource ActionReadReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public async Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref reads) == 2) { ActionReadReached.TrySetResult(); await Release.Task.WaitAsync(token); }
            return await new Sampler().GetSnapshotAsync(request, token);
        }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FailingStatusSampler : IMetricSampler
    {
        private int reads;
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => Interlocked.Increment(ref reads) == 2 ? throw new IOException("Recorded post-publish status read failure.")
                : new Sampler().GetSnapshotAsync(request, token);
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
