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
    public void ExplicitComponentRepairPublishesOnceAndKeepsOtherScopesIndependent()
    {
        var owner = new ManagementInventoryRevisionOwner();
        var missing = Component("dependency:one", ComponentStatusStates.PayloadMissing);
        var repaired = missing with { State = ComponentStatusStates.Installed };
        var otherUserRepaired = repaired with { State = ComponentStatusStates.InstalledUnverified };
        var unrelated = Component("dependency:two", ComponentStatusStates.Installed);
        owner.ObserveComponents(null, [missing, unrelated]);
        owner.ObserveComponents("user:one", [missing, unrelated]);
        owner.ObserveComponents("user:two", [missing, unrelated]);

        owner.ObserveComponentContentChanged(null, repaired);
        Assert.Equal("1", owner.Current.ComponentRevision);
        owner.ObserveComponents(null, [repaired, unrelated]);
        owner.ObserveComponent("user:one", repaired);
        owner.ObserveComponents("user:one", [repaired, unrelated]);
        owner.ObserveComponents("user:two", [otherUserRepaired, unrelated]);
        Assert.Equal("1", owner.Current.ComponentRevision);

        owner.ObserveComponents("user:two", [otherUserRepaired, unrelated with { State = ComponentStatusStates.Missing }]);
        Assert.Equal("2", owner.Current.ComponentRevision);
        owner.ObserveComponents("user:one", [repaired, unrelated]);
        Assert.Equal("2", owner.Current.ComponentRevision);
    }

    [Fact]
    public void ComponentChangeLeaseSuppressesInFlightReadsAndCommitPublishesOnceAcrossScopes()
    {
        const string componentId = "dependency:one";
        const string firstSid = "user:one";
        const string secondSid = "user:two";
        const string unseededSid = "user:unseeded";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "ReadyToInstall");
        var changed = Component(componentId, "Installed");
        var unrelated = Component("dependency:two", "Installed");
        owner.ObserveComponents(null, [original, unrelated]);
        owner.ObserveComponents(firstSid, [original, unrelated]);
        owner.ObserveComponents(secondSid, [original, unrelated]);
        var initial = owner.Current;

        using (owner.BeginComponentChange(componentId))
        {
            Assert.Throws<InvalidOperationException>(() => owner.BeginComponentChange(componentId));
            owner.ObserveComponents(null, [changed, unrelated]);
            owner.ObserveComponent(firstSid, changed);
            owner.ObserveComponents(unseededSid, [changed, unrelated]);
            Assert.Equal(initial, owner.Current);

            owner.ObserveComponentContentChanged(null, changed);
            Assert.Equal("1", owner.Current.ComponentRevision);
        }

        owner.ObserveComponents(null, [changed, unrelated]);
        owner.ObserveComponents(firstSid, [changed, unrelated]);
        owner.ObserveComponents(secondSid, [changed, unrelated]);
        owner.ObserveComponents(unseededSid, [changed, unrelated]);

        Assert.Equal("1", owner.Current.ComponentRevision);
    }

    [Fact]
    public void StaleComponentObservationsAreIgnoredAfterCommittedChange()
    {
        const string componentId = "dependency:one";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "ReadyToInstall");
        var changed = Component(componentId, "Installed");
        owner.ObserveComponents(null, [original]);
        var observationBeforeChange = owner.CaptureComponentObservationToken();

        using (owner.BeginComponentChange(componentId))
        {
            owner.ObserveComponentContentChanged(null, changed);
        }

        owner.ObserveComponent(null, original, observationBeforeChange);
        owner.ObserveComponents(null, [original], observationBeforeChange);

        Assert.Equal("1", owner.Current.ComponentRevision);
        owner.ObserveComponents(null, [changed], owner.CaptureComponentObservationToken());
        Assert.Equal("1", owner.Current.ComponentRevision);
    }

    [Fact]
    public void DelayedObservationStartedInsideFailedChangeIsDiscardedAfterRollbackAndLeaseDisposal()
    {
        const string componentId = "dependency:one";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "Installed");
        var transientMissing = Component(componentId, ComponentStatusStates.PayloadMissing);
        owner.ObserveComponents(null, [original]);
        var initial = owner.Current;

        var failedChange = owner.BeginComponentChange(componentId);
        // This token is captured while the transaction has live->backup in flight. The GET
        // reads the temporary absence, then its result is delayed until rollback/disposal.
        var delayedRead = owner.CaptureComponentObservationToken();
        // Disposing this uncommitted lease represents a successful rollback.
        failedChange.Dispose();

        owner.ObserveComponent(null, transientMissing, delayedRead);
        Assert.Equal(initial, owner.Current);

        owner.ObserveComponent(null, original, owner.CaptureComponentObservationToken());
        Assert.Equal(initial, owner.Current);
    }

    [Fact]
    public void DisposedCommittedLeaseDoesNotReleaseNewComponentReservation()
    {
        const string componentId = "dependency:one";
        var owner = new ManagementInventoryRevisionOwner();
        var changed = Component(componentId, "Installed");
        var completedLease = owner.BeginComponentChange(componentId);

        owner.ObserveComponentContentChanged(null, changed);
        var nextLease = owner.BeginComponentChange(componentId);
        completedLease.Dispose();

        Assert.Throws<InvalidOperationException>(() => owner.BeginComponentChange(componentId));
        Assert.Equal("1", owner.Current.ComponentRevision);
        nextLease.Dispose();
    }

    [Fact]
    public void UnknownStatusComponentCommitSeedsLaterScopesWithoutAnotherRevision()
    {
        const string componentId = "dependency:one";
        const string userSid = "user:one";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "ReadyToInstall");
        var changed = Component(componentId, "Installed");
        var unrelated = Component("dependency:two", "ReadyToInstall");
        var changedUnrelated = Component("dependency:two", "Installed");
        owner.ObserveComponents(null, [original, unrelated]);
        owner.ObserveComponents(userSid, [original, unrelated]);

        using (owner.BeginComponentChange(componentId))
        {
            owner.ObserveComponentContentChanged(componentId);
        }

        Assert.Equal("1", owner.Current.ComponentRevision);
        owner.ObserveComponents(null, [changed, unrelated]);
        owner.ObserveComponent(userSid, changed);
        owner.ObserveComponents(userSid, [changed, unrelated]);
        Assert.Equal("1", owner.Current.ComponentRevision);

        owner.ObserveComponents(userSid, [changed, changedUnrelated]);
        Assert.Equal("2", owner.Current.ComponentRevision);
    }

    [Fact]
    public void DisposingUncommittedComponentChangePreservesFingerprintAndReleasesComponentId()
    {
        const string componentId = "dependency:one";
        const string unseededSid = "user:unseeded";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "ReadyToInstall");
        var changed = Component(componentId, "Installed");
        var unrelated = Component("dependency:two", "Installed");
        owner.ObserveComponents(null, [original, unrelated]);
        var initial = owner.Current;

        var failedChange = owner.BeginComponentChange(componentId);
        owner.ObserveComponents(null, [changed, unrelated]);
        owner.ObserveComponent(null, changed);
        owner.ObserveComponents(unseededSid, [changed, unrelated]);
        failedChange.Dispose();

        Assert.Equal(initial, owner.Current);
        owner.ObserveComponents(null, [original, unrelated]);
        owner.ObserveComponents(unseededSid, [original, unrelated]);
        Assert.Equal(initial, owner.Current);
        using var retry = owner.BeginComponentChange(componentId);
    }

    [Fact]
    public void ComponentChangeLeaseStillObservesUnrelatedComponentChanges()
    {
        const string componentId = "dependency:one";
        var owner = new ManagementInventoryRevisionOwner();
        var original = Component(componentId, "ReadyToInstall");
        var changed = Component(componentId, "Installed");
        var unrelated = Component("dependency:two", "ReadyToInstall");
        var changedUnrelated = Component("dependency:two", "Installed");
        owner.ObserveComponents(null, [original, unrelated]);

        using (owner.BeginComponentChange(componentId))
        {
            owner.ObserveComponents(null, [changed, changedUnrelated]);
            Assert.Equal("1", owner.Current.ComponentRevision);

            owner.ObserveComponentContentChanged(null, changed);
            Assert.Equal("2", owner.Current.ComponentRevision);
        }

        owner.ObserveComponents(null, [changed, changedUnrelated]);
        Assert.Equal("2", owner.Current.ComponentRevision);
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
