using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class ManagementInventoryRevisionOwnerTests
{
    [Fact]
    public void ObserveSoftwareContent_AdvancesOnlyForChangedContent()
    {
        var owner = new ManagementInventoryRevisionOwner();
        var original = new[] { Software("app:one", "Editor") };

        owner.ObserveSoftwareContent("S-1-5-21-1", original);
        var initial = owner.Current;
        owner.ObserveSoftwareContent("S-1-5-21-1", [.. original]);

        Assert.Equal(initial, owner.Current);

        owner.ObserveSoftwareContent("S-1-5-21-1", [Software("app:one", "Editor 2")]);

        Assert.Equal("1", owner.Current.SoftwareRevision);
        Assert.Equal("0", owner.Current.ComponentRevision);
        Assert.Equal("0", owner.Current.BrowserRuntimeRevision);
    }

    [Fact]
    public void ObserveComponents_AdvancesOnlyForChangedContent()
    {
        var owner = new ManagementInventoryRevisionOwner();
        var original = new[] { Component("dependency:one", "ReadyToInstall") };

        owner.ObserveComponents(null, original);
        var initial = owner.Current;
        owner.ObserveComponents(null, [.. original]);

        Assert.Equal(initial, owner.Current);

        owner.ObserveComponents(null, [Component("dependency:one", "Installed")]);

        Assert.Equal("0", owner.Current.SoftwareRevision);
        Assert.Equal("1", owner.Current.ComponentRevision);
        Assert.Equal("0", owner.Current.BrowserRuntimeRevision);
    }

    [Fact]
    public void ObserveComponentsKeepsUserSpecificStatusFingerprintsInSeparateScopes()
    {
        const string firstSid = "S-1-5-21-1-2-3-1001";
        const string secondSid = "S-1-5-21-4-5-6-1002";
        var owner = new ManagementInventoryRevisionOwner();
        var firstUser = new[] { Component("dependency:one", "Installed") };
        var secondUser = new[] { Component("dependency:one", "ReadyToInstall") };

        owner.ObserveComponents(firstSid, firstUser);
        owner.ObserveComponents(secondSid, secondUser);
        var initial = owner.Current;
        owner.ObserveComponents(firstSid, [.. firstUser]);
        owner.ObserveComponents(secondSid, [.. secondUser]);

        Assert.Equal(initial, owner.Current);

        owner.ObserveComponents(firstSid, [Component("dependency:one", "ReadyToInstall")]);

        Assert.Equal("1", owner.Current.ComponentRevision);
    }

    [Fact]
    public async Task SubscribeAsyncPublishesInitialCurrentValueAndOnlyChangedContent()
    {
        const string sid = "S-1-5-21-1";
        var owner = new ManagementInventoryRevisionOwner();
        var original = new[] { Software("app:one", "Editor") };
        owner.ObserveSoftwareContent(sid, original);
        await using var enumerator = owner.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(owner.Current, enumerator.Current);

        var pending = enumerator.MoveNextAsync().AsTask();
        owner.ObserveSoftwareContent(sid, [.. original]);
        await Task.Delay(75);
        Assert.False(pending.IsCompleted);

        owner.ObserveSoftwareContent(sid, [Software("app:one", "Editor 2")]);

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("1", enumerator.Current.SoftwareRevision);
        Assert.Equal("0", enumerator.Current.ComponentRevision);
        Assert.Equal("0", enumerator.Current.BrowserRuntimeRevision);
    }

    [Fact]
    public async Task ObserveSoftwareInventoryContentChangedAdvancesAndPublishesSoftwareRevision()
    {
        var owner = new ManagementInventoryRevisionOwner();
        await using var enumerator = owner.SubscribeAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("0", enumerator.Current.SoftwareRevision);

        var pending = enumerator.MoveNextAsync().AsTask();
        owner.ObserveSoftwareInventoryContentChanged();

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("1", enumerator.Current.SoftwareRevision);
        Assert.Equal("0", enumerator.Current.ComponentRevision);
        Assert.Equal("0", enumerator.Current.BrowserRuntimeRevision);
    }

    [Fact]
    public void ObserveBrowserRuntimeSnapshot_IgnoresCaptureTimeAndAdvancesOnContentChange()
    {
        var owner = new ManagementInventoryRevisionOwner();
        var original = BrowserRuntimeSnapshot("1.0", DateTimeOffset.UnixEpoch);

        owner.ObserveBrowserRuntimeSnapshot(original);
        var initial = owner.Current;
        owner.ObserveBrowserRuntimeSnapshot(original with { CapturedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) });

        Assert.Equal(initial, owner.Current);

        owner.ObserveBrowserRuntimeSnapshot(BrowserRuntimeSnapshot("2.0", DateTimeOffset.UnixEpoch.AddMinutes(2)));

        Assert.Equal("0", owner.Current.SoftwareRevision);
        Assert.Equal("0", owner.Current.ComponentRevision);
        Assert.Equal("1", owner.Current.BrowserRuntimeRevision);
    }

    private static SoftwareRecord Software(string id, string name)
        => new(
            id,
            name,
            SoftwareKinds.Other,
            "installed",
            [],
            [@"C:\Apps\Editor"],
            new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"),
            null);

    private static ComponentStatus Component(string id, string state)
    {
        var definition = new ComponentDefinition(
            id,
            "Vendor",
            "https://example.invalid/source",
            null,
            "dependency",
            false,
            false,
            false,
            []);
        return new ComponentStatus(
            definition,
            state,
            @"C:\Dependencies\one",
            @"C:\Dependencies\one\installer",
            null,
            false,
            state == "Installed",
            false,
            state != "Installed",
            state != "Installed",
            true,
            [],
            BackendMessage.Create(
                BackendMessageDomains.Dependency,
                state == "Installed"
                    ? BackendMessageCodes.Dependency.ComponentFilesExtracted
                    : BackendMessageCodes.Dependency.ComponentFilesUnverified));
    }

    private static BrowserRuntimeSnapshot BrowserRuntimeSnapshot(string version, DateTimeOffset capturedAt)
    {
        var runtime = new BrowserRuntimeEntry(
            "webview2:test",
            "WebView2",
            "WebView2Runtime",
            version,
            @"C:\WebView2\msedgewebview2.exe",
            @"C:\WebView2",
            "Managed",
            true,
            true);
        return new BrowserRuntimeSnapshot(runtime, null, [runtime], "shared-webview2-runtime", capturedAt);
    }
}
