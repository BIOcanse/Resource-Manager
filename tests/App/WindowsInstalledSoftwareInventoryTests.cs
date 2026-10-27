using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite.Information;
using ResourceManager.App.Domain.Software;
using Microsoft.Win32;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsInstalledSoftwareInventoryTests : IDisposable
{
    [Fact]
    public void UnscopedScanReleasesTheCurrentIdentityToken()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var token = identity.AccessToken;
        var expected = !identity.IsSystem;

        Assert.Equal(expected, WindowsInstalledSoftwareInventory.IncludesCurrentUser(null, () => identity));

        Assert.True(token.IsClosed);
    }

    [Fact]
    public void ScopedScanDoesNotOpenAnUnusedIdentityToken()
    {
        Assert.False(WindowsInstalledSoftwareInventory.IncludesCurrentUser("S-1-5-21-1-2-3-1001",
            () => throw new Xunit.Sdk.XunitException("A scoped scan does not need the service identity.")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreadableEntryDoesNotPreventLaterEntries(bool denied)
    {
        var visited = new List<string>();
        foreach (var name in new[] { "first", "bad", "last" })
            WindowsInstalledSoftwareInventory.ReadInventoryEntry(name, entry =>
            {
                if (entry == "bad")
                {
                    if (denied) throw new UnauthorizedAccessException();
                    throw new IOException();
                }
                visited.Add(entry);
            });
        Assert.Equal(new[] { "first", "last" }, visited);
        Assert.Throws<OperationCanceledException>(() => WindowsInstalledSoftwareInventory.ReadInventoryEntry(
            "cancel", _ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task SubscriptionFramesReuseInventoryUntilExplicitRefreshPublishesChangedContent()
    {
        var reads = 0;
        var name = "First";
        var changes = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ =>
        {
            reads++;
            return [new InstalledSoftwareEntry("fixture", name, null, null, null, [], null, "fixture")];
        }, onSoftwareContentChanged: () => changes++);
        await inventory.GetInstalledSoftwareAsync(default);
        var field = inventory.CreateBackend().Catalog.Fields.First().Field;
        using var borrow = inventory.BorrowSubscriptionCapture();
        borrow.Bind([field]);
        borrow.SynchronizeDemand([field]);
        for (var frame = 0; frame < 4; frame++)
            await borrow.CollectAsync([field], new AcceptedPermit(), default);
        Assert.Equal(1, reads);
        name = "Updated";
        await inventory.RefreshInstalledSoftwareAsync(default);
        Assert.Equal(2, reads);
        Assert.Equal(1, changes);
        await borrow.CollectAsync([field], new AcceptedPermit(), default);
        Assert.Equal(2, reads);
        Assert.Equal("Updated", Assert.Single(await inventory.GetInstalledSoftwareAsync(default)).Name);
    }
    private sealed class AcceptedPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"resource-manager-installed-inventory-{Guid.NewGuid():N}");

    [Fact]
    public void HasValidRegisteredLocation_AcceptsExistingDirectory()
    {
        var installedRoot = Directory.CreateDirectory(Path.Combine(testRoot, "InstalledApp")).FullName;

        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([installedRoot]));
    }

    [Fact]
    public void HasValidRegisteredLocation_AcceptsExistingFile()
    {
        Directory.CreateDirectory(testRoot);
        var executablePath = Path.Combine(testRoot, "app.exe");
        File.WriteAllBytes(executablePath, []);

        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([executablePath]));
    }

    [Fact]
    public void HasValidRegisteredLocation_RejectsOnlyMissingLocations()
    {
        var missingRoot = Path.Combine(testRoot, "UninstalledApp");

        Assert.False(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([missingRoot]));
    }

    [Fact]
    public void HasValidRegisteredLocation_AcceptsEntryWithoutLocationEvidence()
    {
        Assert.True(WindowsInstalledSoftwareInventory.HasValidRegisteredLocation([]));
    }

    [Fact]
    public void InventoryRootsIncludeOnlyTheSelectedUserSidAndBothRegistryViews()
    {
        const string userSid = "S-1-5-21-1-2-3-1001";
        var roots = WindowsInstalledSoftwareInventory.CreateInventoryRoots(
            [userSid, userSid, "S-1-5-18"]);

        Assert.Equal(4, roots.Count);
        Assert.Equal(
            new[] { RegistryHive.LocalMachine, RegistryHive.LocalMachine, RegistryHive.Users, RegistryHive.Users },
            roots.Select(static root => root.Hive).ToArray());
        Assert.Equal(
            new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Registry64, RegistryView.Registry32 },
            roots.Select(static root => root.View).ToArray());
        Assert.Equal(new string?[] { null, null, userSid, userSid },
            roots.Select(static root => root.UserSid).ToArray());
    }

    [Fact]
    public async Task CachedInventoryIsBoundToOneCallerSidAtATime()
    {
        var reads = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ =>
        {
            reads++;
            return [new InstalledSoftwareEntry($"entry-{reads}", $"Entry {reads}", null, null, null, [], null, "recorded")];
        });

        var first = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);
        var second = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1002", default);
        var firstAgain = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);

        Assert.Equal("Entry 1", Assert.Single(first).Name);
        Assert.Equal("Entry 2", Assert.Single(second).Name);
        Assert.Equal("Entry 3", Assert.Single(firstAgain).Name);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task CallerScopedInventoryDoesNotLeakIntoUnscopedInformationFields()
    {
        var inventory = new WindowsInstalledSoftwareInventory(_ => [
            new InstalledSoftwareEntry("user-entry", "User Entry", null, null, null, [], null, "HKU")]);

        _ = await inventory.GetInstalledSoftwareAsync(default);
        Assert.NotEmpty(inventory.ReadInformation().Devices);

        _ = await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default);

        Assert.NotEmpty(inventory.ReadInformation().Devices);
    }

    [Fact]
    public async Task SoftwareContentChangeCallbackUsesCommittedFingerprintPerSid()
    {
        const string firstSid = "S-1-5-21-1-2-3-1001";
        const string secondSid = "S-1-5-21-4-5-6-1002";
        var captures = new Queue<IReadOnlyList<InstalledSoftwareEntry>>();
        captures.Enqueue([Software("first", "First User App")]);
        captures.Enqueue([Software("second", "Second User App")]);
        captures.Enqueue([Software("first", "First User App")]);
        captures.Enqueue([Software("second", "Second User App")]);
        captures.Enqueue([Software("first", "First User App Updated")]);
        captures.Enqueue([Software("first", "First User App Updated")]);
        captures.Enqueue([Software("second", "Second User App Updated")]);
        captures.Enqueue([Software("second", "Second User App Updated")]);
        var changeCount = 0;
        var inventory = new WindowsInstalledSoftwareInventory(
            _ => captures.Dequeue(),
            onSoftwareContentChanged: () => changeCount++);

        _ = await inventory.RefreshInstalledSoftwareAsync(firstSid, CancellationToken.None);
        _ = await inventory.RefreshInstalledSoftwareAsync(secondSid, CancellationToken.None);
        _ = await inventory.RefreshInstalledSoftwareAsync(firstSid, CancellationToken.None);
        _ = await inventory.RefreshInstalledSoftwareAsync(secondSid, CancellationToken.None);

        Assert.Equal(0, changeCount);

        _ = await inventory.RefreshInstalledSoftwareAsync(firstSid, CancellationToken.None);
        Assert.Equal(1, changeCount);

        _ = await inventory.RefreshInstalledSoftwareAsync(firstSid, CancellationToken.None);
        Assert.Equal(1, changeCount);

        _ = await inventory.RefreshInstalledSoftwareAsync(secondSid, CancellationToken.None);
        Assert.Equal(2, changeCount);

        _ = await inventory.RefreshInstalledSoftwareAsync(secondSid, CancellationToken.None);
        Assert.Equal(2, changeCount);
    }

    [Fact]
    public async Task SoftwareContentChangeCallbackRunsOnlyAfterAcceptedCaptureCommit()
    {
        var reads = 0;
        var changeCount = 0;
        var inventory = new WindowsInstalledSoftwareInventory(
            _ => [Software("entry", reads++ == 0 ? "Original" : "Updated")],
            onSoftwareContentChanged: () => changeCount++);

        _ = await inventory.RefreshInstalledSoftwareAsync(CancellationToken.None);
        _ = await inventory.CaptureInformationAsync(CancellationToken.None, static _ => false);

        Assert.Equal("Original", Assert.Single(await inventory.GetInstalledSoftwareAsync(CancellationToken.None)).Name);
        Assert.Equal(0, changeCount);

        var accepted = await inventory.RefreshInstalledSoftwareAsync(CancellationToken.None);

        Assert.Equal("Updated", Assert.Single(accepted).Name);
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public async Task AlternatingTrayAndCallerReadsScanOncePerScopeUntilExplicitRefresh()
    {
        var reads = 0;
        var inventory = new WindowsInstalledSoftwareInventory(_ => [Software("app", $"version-{++reads}")]);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal("version-1", Assert.Single(await inventory.GetInstalledSoftwareAsync(default)).Name);
            Assert.Equal("version-2", Assert.Single(await inventory.GetInstalledSoftwareAsync("S-1-5-21-1-2-3-1001", default)).Name);
        }
        Assert.Equal(2, reads);
        Assert.Equal("version-3", Assert.Single(await inventory.RefreshInstalledSoftwareAsync(default)).Name);
        Assert.Equal("version-3", Assert.Single(await inventory.GetInstalledSoftwareAsync(default)).Name);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void AbsentOptionalRegistrySizeDoesNotQueryKindAndLaterValueIsRead()
    {
        object? value = null;
        var kinds = 0;
        RegistryValueKind ReadKind()
        {
            kinds++;
            if (value is null) throw new IOException("The specified registry key does not exist.");
            return RegistryValueKind.DWord;
        }
        for (var i = 0; i < 20; i++)
            Assert.Null(WindowsInstalledSoftwareInventory.ReadEstimatedSizeBytes(() => value, ReadKind));
        Assert.Equal(0, kinds);
        value = 42;
        Assert.Equal(42UL * 1024, WindowsInstalledSoftwareInventory.ReadEstimatedSizeBytes(() => value, ReadKind));
        Assert.Equal(1, kinds);
    }

    private static InstalledSoftwareEntry Software(string id, string name)
        => new(id, name, "1.0", "Vendor", null, [], null, $"registry:{id}");

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
