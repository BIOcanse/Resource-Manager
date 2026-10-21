using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareInformationFieldTests
{
    [Fact]
    public async Task CanonicalFieldsShareTheOriginalInventoryPublicationAndCacheReadsNeverScan()
    {
        var reads = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ => { reads++; return [Entry()]; });
        var backend = new InformationReadWriteBackend(inventory.ReadInformation,
            token => inventory.CaptureInformationAsync(token), _ => new(true, null, []));
        Assert.Empty(backend.Catalog.Fields);
        Assert.Equal(0, reads);
        var original = await inventory.GetInstalledSoftwareAsync(default);
        var catalog = backend.Catalog;
        Assert.Equal(4, catalog.Fields.Length);
        var name = Assert.Single(catalog.Fields, item => item.Field.FieldName == "software_all_name_declared");
        Assert.Equal("windows-uninstall|" + original[0].RegistryPath, name.Field.DeviceIdentity);
        Assert.True(name.Read.Supported);
        Assert.False(name.Write.Supported);
        Assert.Empty(name.Read.Dependencies);
        Assert.Equal("App", backend.ReadCached(name.Field).Value!.Text);
        _ = await inventory.GetInstalledSoftwareAsync(default);
        Assert.Equal(1, reads);
        var next = await backend.CollectAsync([name.Field], default);
        Assert.Equal(2, reads);
        Assert.Equal(Assert.Single(next), backend.ReadCached(name.Field));
    }

    [Fact]
    public void DeclaredPathNeverBorrowsInferredRootsAndExactRegistryIdentityDoesNotUseLegacyNormalizedId()
    {
        var first = Entry() with { RegistryPath = @"LocalMachine\Registry64\Uninstall\A_B" };
        var second = Entry() with { RegistryPath = @"LocalMachine\Registry64\Uninstall\A-B" };
        var fields = SoftwareInformationFieldProjection.Create([first, second], DateTimeOffset.UtcNow);
        Assert.Equal(2, fields.Devices.Count);
        foreach (var device in fields.Devices)
        {
            Assert.Null(Assert.Single(device.Fields, item => item.Definition.Name == "software_all_install_path_declared").Value);
            Assert.Null(Assert.Single(device.Fields, item => item.Definition.Name == "software_all_version_declared").Value);
        }
    }

    [Fact]
    public async Task FailureClearsCanonicalValuesRecoveryPublishesAndCancellationPreservesDisplay()
    {
        var fault = false;
        var inventory = new WindowsInstalledSoftwareInventory(_ => fault ? throw new IOException("recorded failure") : [Entry()]);
        var first = await inventory.CaptureInformationAsync(default);
        fault = true;
        var failed = await inventory.CaptureInformationAsync(default);
        Assert.Null(failed.ObservedAt);
        Assert.Equal(first.Devices.Select(item => item.DeviceIdentity), failed.Devices.Select(item => item.DeviceIdentity));
        Assert.All(failed.Devices.SelectMany(item => item.Fields), item => Assert.Null(item.Value));
        Assert.Same(first.Devices[0].Fields[0].Definition, failed.Devices[0].Fields[0].Definition);
        fault = false;
        var recovered = await inventory.CaptureInformationAsync(default);
        Assert.NotNull(recovered.ObservedAt);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inventory.CaptureInformationAsync(canceled.Token));
        Assert.Same(recovered, inventory.ReadInformation());
    }

    [Fact]
    public async Task RetiredCommitCannotReplaceCurrentAndDirectCaptureWakesTheOriginalSignal()
    {
        var name = "first";
        var inventory = new WindowsInstalledSoftwareInventory(_ => [Entry() with { Name = name }]);
        var first = await inventory.CaptureInformationAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = inventory.SubscribeInformationAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); Assert.Same(first, stream.Current);
        var changed = stream.MoveNextAsync().AsTask();
        name = "retired";
        Assert.Same(first, await inventory.CaptureInformationAsync(default, _ => false));
        Assert.False(changed.IsCompleted);
        name = "next";
        var next = await inventory.CaptureInformationAsync(default);
        Assert.True(await changed);
        Assert.Same(next, stream.Current);
        Assert.Equal("next", Assert.Single(next.Devices).Fields[0].Value!.Value.GetString());
    }

    [Fact]
    public async Task CanceledInFlightCaptureCannotPublishAndConcurrentRefreshesUseTheOriginalGate()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var block = false;
        var active = 0; var peak = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ =>
        {
            var count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            try
            {
                if (block) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                return [Entry()];
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var first = await inventory.CaptureInformationAsync(default);
        block = true;
        using var canceled = new CancellationTokenSource();
        var capture = Task.Run(async () => await inventory.CaptureInformationAsync(canceled.Token));
        Task<DeviceInformationSnapshot>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            second = Task.Run(async () => await inventory.CaptureInformationAsync(default));
            canceled.Cancel();
            Assert.Same(first, inventory.ReadInformation());
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await capture);
        await second!;
        Assert.Equal(1, peak);
    }

    private static InstalledSoftwareEntry Entry() => new("same-legacy-id", "App", null, "Publisher", null,
        [@"D:\Inferred\App"], @"D:\Inferred\App\uninstall.exe", @"LocalMachine\Registry64\Uninstall\App");
}

