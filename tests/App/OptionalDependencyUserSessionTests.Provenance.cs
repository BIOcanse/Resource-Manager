using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed partial class OptionalDependencyUserSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ManagedDriverRecordRequiresVerifiedInstallerExitAndInstalledProbe(int exitCode)
        => await VerifyManagedDriverRecordAsync(exitCode, cancelWhileWaiting: false);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task CanceledManagedDriverWaitUsesActualInstallerExitForRecord(int exitCode)
        => await VerifyManagedDriverRecordAsync(exitCode, cancelWhileWaiting: true);

    private async Task VerifyManagedDriverRecordAsync(int exitCode, bool cancelWhileWaiting)
    {
        const string id = "hardware-sensors-pawnio-provider";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var cache = Directory.CreateDirectory(Path.Combine(testRoot, "Misc", "DependencyInstallers", id)).FullName;
        var installer = Path.Combine(cache, definition.InstallerFileName);
        File.WriteAllText(installer, "recorded installer bytes, never executed");
        var release = new DependencyReleaseRecord(definition.ReleaseSource!.VerifiedTag, definition.ReleaseSource.VerifiedAssetName,
            DependencyReleaseRecordStore.Hash(installer));
        DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.DownloadPath(installer), release);
        var managed = Directory.CreateDirectory(Path.Combine(testRoot, "Dependencies", id)).FullName;
        File.WriteAllText(Path.Combine(managed, "PawnIOLib.dll"), "recorded existing runtime");
        var broker = new CompletingBroker();
        var environment = new HostEnvironment(testRoot);
        using var bridge = new HardwareBridgeClient(environment);
        using var client = new HttpClient();
        var manager = new OptionalDependencyManager(client, environment, new NoTracking(), new EmptyInventory(), new NoSourceResolution(), bridge, broker);
        using var cancellation = new CancellationTokenSource();
        Assert.True((await manager.GetStatusAsync(id, default))!.UpdateAvailable);
        var launch = manager.LaunchInstallerAsync(
            id,
            true,
            "verified",
            cancellation.Token,
            new SoftwareOperationCaller(UserSid, 7));
        await broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(broker.Requests).WaitForExit);
        Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(managed)));
        Assert.False(launch.IsCompleted);
        if (cancelWhileWaiting)
        {
            cancellation.Cancel();
            await broker.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(launch.IsCompleted);
            Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(managed)));
        }
        broker.Completed.TrySetResult(exitCode);
        if (exitCode == 0)
        {
            Assert.Equal("installerCompleted", (await launch).State);
            Assert.Equal(release, DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(managed)));
            var status = (await manager.GetStatusAsync(id, default))!;
            Assert.Equal("installed", status.State);
            Assert.False(status.UpdateAvailable);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => launch);
            Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(managed)));
            Assert.True((await manager.GetStatusAsync(id, default))!.UpdateAvailable);
        }
    }

    [Theory]
    [InlineData("hardware-sensors-pawnio-provider")]
    [InlineData("shared-webview2-runtime")]
    public async Task TamperedExeIsRejectedBeforeAnyUserSessionLaunch(string id)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var cache = Directory.CreateDirectory(Path.Combine(testRoot, "Misc", "DependencyInstallers", id)).FullName;
        var installer = Path.Combine(cache, definition.InstallerFileName);
        File.WriteAllText(installer, "original recorded asset");
        DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.DownloadPath(installer),
            new(definition.ReleaseSource?.VerifiedTag ?? "unversioned", definition.InstallerFileName, DependencyReleaseRecordStore.Hash(installer)));
        File.WriteAllText(installer, "tampered asset");
        var broker = new CompletingBroker();
        var environment = new HostEnvironment(testRoot);
        using var bridge = new HardwareBridgeClient(environment);
        using var client = new HttpClient();
        var manager = new OptionalDependencyManager(client, environment, new NoTracking(), new EmptyInventory(), new NoSourceResolution(), bridge, broker);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.LaunchInstallerAsync(id, true, null, default,
            new SoftwareOperationCaller(UserSid, 7)));
        Assert.Empty(broker.Requests);
    }

    private sealed class CompletingBroker : IInteractiveUserSessionBroker
    {
        internal readonly List<UserShellExecuteRequest> Requests = [];
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<uint> GetActiveSessionIds() => [7u];
        public string? GetUserSid(uint sessionId) => UserSid;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string path) => throw new NotSupportedException();
        public uint LaunchNativeUi(uint sessionId, string path, string arguments) => throw new NotSupportedException();
        public uint LaunchUserShell(uint sessionId, string expectedSid, string path, string arguments)
            => throw new InvalidOperationException("A release installer must request completion evidence.");
        public async Task<int> LaunchUserShellAndWaitAsync(uint sessionId, string expectedSid, string path, string arguments, CancellationToken token)
        {
            Assert.Equal(UserSid, expectedSid);
            Assert.Equal(7u, sessionId);
            Requests.Add(UserShellExecuteRequest.Decode(arguments[(UserShellExecuteRequest.Argument.Length + 1)..]));
            Started.TrySetResult();
            using var cancellationRegistration = token.Register(
                () => { CancellationObserved.TrySetResult(); });
            return await Completed.Task;
        }
    }
}
