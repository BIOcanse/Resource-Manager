using Microsoft.Win32;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class IfeoOwnedRuleCleanupCoordinatorTests
{
    [Fact]
    public void DisabledPolicyReturnsCleanupFailureAndReconcileStillAppliesIndependentPolicy()
    {
        var store = new FakeOwnedRuleStore();
        store.SetImage(RegistryView.Registry64, Image("denied.exe", new IfeoRuleSnapshot("owned", Owner, @"C:\denied.exe")));
        store.ProtectWrites(RegistryView.Registry64, "denied.exe");
        var registry = new WindowsIfeoGpuLaunchInterceptionRegistry(@"C:\missing-broker.exe", store);
        var policy = new GpuPlacementProcessPolicy("software", "denied", "denied.exe", @"C:\denied.exe",
            true, [], null, DateTimeOffset.MinValue);
        Assert.Equal(GpuLaunchInterceptionStatuses.RegistryAccessDenied, registry.Apply(policy).Status);
        var independent = policy with { ProcessKey = "independent", ExecutablePath = @"C:\missing-independent.exe", StartupInterceptionEnabled = true };
        var statuses = registry.Reconcile(GpuPlacementPolicyDocument.Empty with { ProcessPolicies = [independent] }, []);
        Assert.Contains(statuses, status => status.ProcessKey == "denied.exe" && status.Status == GpuLaunchInterceptionStatuses.RegistryAccessDenied);
        Assert.Contains(statuses, status => status.ProcessKey == "independent");
    }

    [Fact]
    public void StaleFailureDoesNotBlockOtherImagesOrViewsAndIsReported()
    {
        var store = new FakeOwnedRuleStore();
        store.SetImage(RegistryView.Registry64, Image("denied.exe", new IfeoRuleSnapshot("owned", Owner, @"C:\denied.exe")));
        store.ProtectWrites(RegistryView.Registry64, "denied.exe");
        store.SetImage(RegistryView.Registry64, Image("ok.exe", new IfeoRuleSnapshot("owned", Owner, @"C:\ok.exe")));
        store.SetImage(RegistryView.Registry32, Image("ok.exe", new IfeoRuleSnapshot("owned", Owner, @"C:\ok.exe")));
        var failures = new IfeoOwnedRuleCleanupCoordinator(store, Owner).RemoveStale(
            [RegistryView.Registry64, RegistryView.Registry32], EmptyDesiredPaths());
        Assert.Equal("denied.exe", Assert.Single(failures).ImageName);
        Assert.Empty(store.GetImage(RegistryView.Registry64, "ok.exe").Rules);
        Assert.Empty(store.GetImage(RegistryView.Registry32, "ok.exe").Rules);
        Assert.Single(store.GetImage(RegistryView.Registry64, "denied.exe").Rules);
    }

    [Fact]
    public void CleanupRemovesOnlyUnchangedWritesAndRelinquishesForeignDebugger()
    {
        var rule = new IfeoRuleSnapshot("owned", Owner, @"C:\app.exe", "foreign debugger", "changed-id");
        Assert.Equal(["ResourceManagerOwner"], IfeoOwnedRuleCleanupCoordinator.OwnedValuesToRemove(rule, "broker"));
        var name = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(rule.NormalizedFilterPath!);
        var owned = rule with { Debugger = "broker", RuleId = name, RuleName = name };
        Assert.Equal(["ResourceManagerOwner", "ResourceManagerRuleId", "Debugger", "FilterFullPath"],
            IfeoOwnedRuleCleanupCoordinator.OwnedValuesToRemove(owned, "broker"));
        Assert.DoesNotContain("FilterFullPath", IfeoOwnedRuleCleanupCoordinator.OwnedValuesToRemove(
            owned with { NormalizedFilterPath = @"C:\changed.exe" }, "broker"));
        Assert.Equal(IfeoParentCleanupAction.RemoveOwner,
            IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(Image("app.exe") with { CurrentUseFilter = 2 }, Owner));
    }

    private const string Owner = WindowsIfeoGpuLaunchInterceptionRegistry.OwnerValue;

    [Fact]
    public void RemoveStale_DoesNotRequestWriteForProtectedThirdPartyImage()
    {
        var store = new FakeOwnedRuleStore();
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "protected.exe",
                new IfeoRuleSnapshot("third-party", "another-owner", @"C:\Apps\protected.exe")) with
            {
                ParentUseFilterOwned = false
            });
        store.ProtectWrites(RegistryView.Registry64, "protected.exe");
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveStale([RegistryView.Registry64], EmptyDesiredPaths());

        Assert.Empty(store.WriteRequests);
        Assert.Single(store.GetImage(RegistryView.Registry64, "protected.exe").Rules);
    }

    [Fact]
    public void RemoveStale_WritesOnlyImageContainingOwnedStaleCandidate()
    {
        var desiredPath = @"C:\Apps\current.exe";
        var store = new FakeOwnedRuleStore();
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "protected.exe",
                new IfeoRuleSnapshot("third-party", "another-owner", @"C:\Apps\protected.exe")) with
            {
                ParentUseFilterOwned = false
            });
        store.ProtectWrites(RegistryView.Registry64, "protected.exe");
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "stale.exe",
                new IfeoRuleSnapshot("ResourceManager-stale", Owner, @"C:\Apps\stale.exe")));
        store.SetImage(
            RegistryView.Registry32,
            Image(
                "current.exe",
                new IfeoRuleSnapshot(
                    WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(desiredPath),
                    Owner,
                    desiredPath)));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveStale(
            [RegistryView.Registry64, RegistryView.Registry32],
            new HashSet<string>([desiredPath], StringComparer.OrdinalIgnoreCase));

        var write = Assert.Single(store.WriteRequests);
        Assert.Equal((RegistryView.Registry64, "stale.exe"), write);
        Assert.Empty(store.GetImage(RegistryView.Registry64, "stale.exe").Rules);
        Assert.Single(store.GetImage(RegistryView.Registry32, "current.exe").Rules);
    }

    [Fact]
    public void RemoveStale_RemovesNoncanonicalOwnedRuleForDesiredPath()
    {
        var desiredPath = @"C:\Apps\current.exe";
        var store = new FakeOwnedRuleStore();
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "current.exe",
                new IfeoRuleSnapshot("ResourceManager-legacy-name", Owner, desiredPath)));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveStale(
            [RegistryView.Registry64],
            new HashSet<string>([desiredPath], StringComparer.OrdinalIgnoreCase));

        Assert.Single(store.WriteRequests);
        Assert.Empty(store.GetImage(RegistryView.Registry64, "current.exe").Rules);
    }

    [Fact]
    public void RemoveForPath_PreservesOtherOwnedRuleAndParentOwnership()
    {
        var targetPath = @"C:\Apps\shared.exe";
        var otherPath = @"D:\Apps\shared.exe";
        var store = new FakeOwnedRuleStore();
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "shared.exe",
                new IfeoRuleSnapshot(
                    WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(targetPath),
                    Owner,
                    targetPath),
                new IfeoRuleSnapshot(
                    WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(otherPath),
                    Owner,
                    otherPath)));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveForPath([RegistryView.Registry64], targetPath);

        var remaining = store.GetImage(RegistryView.Registry64, "shared.exe");
        Assert.True(remaining.ParentUseFilterOwned);
        Assert.Equal(otherPath, Assert.Single(remaining.Rules).NormalizedFilterPath);
    }

    [Fact]
    public void RemoveAll_DeletesOnlyOwnedRulesAndRelinquishesParentToThirdPartyRule()
    {
        var store = new FakeOwnedRuleStore();
        store.SetImage(
            RegistryView.Registry64,
            Image(
                "shared.exe",
                new IfeoRuleSnapshot("owned", Owner, @"C:\Apps\shared.exe"),
                new IfeoRuleSnapshot("third-party", "another-owner", @"D:\Apps\shared.exe")));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        var removed = coordinator.RemoveAll([RegistryView.Registry64]);

        Assert.Equal(1, removed);
        var remaining = store.GetImage(RegistryView.Registry64, "shared.exe");
        Assert.False(remaining.ParentUseFilterOwned);
        Assert.Equal("another-owner", Assert.Single(remaining.Rules).Owner);
    }

    [Fact]
    public void DeleteCandidate_RevalidatesOwnerAndPathBeforeMutation()
    {
        var path = @"C:\Apps\stale.exe";
        var ruleName = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(path);
        var store = new FakeOwnedRuleStore
        {
            BeforeDelete = (view, imageName, image) => image with
            {
                Rules =
                [
                    new IfeoRuleSnapshot(ruleName, "replacement-owner", path)
                ]
            }
        };
        store.SetImage(
            RegistryView.Registry64,
            Image("stale.exe", new IfeoRuleSnapshot(ruleName, Owner, path)));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveStale([RegistryView.Registry64], EmptyDesiredPaths());

        Assert.Single(store.WriteRequests);
        Assert.Equal("replacement-owner", Assert.Single(
            store.GetImage(RegistryView.Registry64, "stale.exe").Rules).Owner);
    }

    [Fact]
    public void ParentCleanup_KeepsOwnerWhileAnyOwnedRuleRemains()
    {
        var image = Image(
            "shared.exe",
            new IfeoRuleSnapshot("owned", Owner, @"C:\Apps\shared.exe"),
            new IfeoRuleSnapshot("third-party", "another-owner", @"D:\Apps\shared.exe"));

        var action = IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(image, Owner);

        Assert.Equal(IfeoParentCleanupAction.None, action);
    }

    [Fact]
    public void ParentCleanup_RemovesOnlyOwnerWhenThirdPartyFullPathRuleRemains()
    {
        var image = Image(
            "shared.exe",
            new IfeoRuleSnapshot("third-party", "another-owner", @"D:\Apps\shared.exe"));

        var action = IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(image, Owner);

        Assert.Equal(IfeoParentCleanupAction.RemoveOwner, action);
    }

    [Fact]
    public void ParentCleanup_RemovesUseFilterOnlyAfterLastFullPathRuleLeaves()
    {
        var image = Image("shared.exe");

        var action = IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(image, Owner);

        Assert.Equal(IfeoParentCleanupAction.RemoveOwnerAndUseFilter, action);
    }

    [Fact]
    public void ParentCleanup_PreservesStateWhenSnapshotIsIncomplete()
    {
        var image = Image("shared.exe") with { Complete = false };

        var action = IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(image, Owner);

        Assert.Equal(IfeoParentCleanupAction.None, action);
    }

    [Fact]
    public void RemoveStale_SettlesProvenOrphanedParentOwnership()
    {
        var store = new FakeOwnedRuleStore();
        store.SetImage(RegistryView.Registry64, Image("orphaned.exe"));
        var coordinator = new IfeoOwnedRuleCleanupCoordinator(store, Owner);

        coordinator.RemoveStale([RegistryView.Registry64], EmptyDesiredPaths());

        Assert.Single(store.WriteRequests);
        Assert.False(store.GetImage(RegistryView.Registry64, "orphaned.exe").ParentUseFilterOwned);
    }

    private static HashSet<string> EmptyDesiredPaths()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IfeoImageSnapshot Image(
        string imageName,
        params IfeoRuleSnapshot[] rules)
    {
        return new IfeoImageSnapshot(imageName, true, true, rules);
    }

    internal sealed class FakeOwnedRuleStore : IIfeoOwnedRuleStore
    {
        private readonly Dictionary<(RegistryView View, string ImageName), IfeoImageSnapshot> images = new();
        private readonly HashSet<(RegistryView View, string ImageName)> protectedWrites = new();

        public List<(RegistryView View, string ImageName)> WriteRequests { get; } = [];

        public Func<RegistryView, string, IfeoImageSnapshot, IfeoImageSnapshot>? BeforeDelete { get; init; }

        public IReadOnlyList<string> EnumerateImageNames(RegistryView view)
        {
            return images.Keys
                .Where(key => key.View == view)
                .Select(static key => key.ImageName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public IfeoImageSnapshot? ReadImage(RegistryView view, string imageName)
        {
            return images.GetValueOrDefault((view, imageName));
        }

        public int DeleteOwnedRules(
            RegistryView view,
            string imageName,
            IReadOnlyList<IfeoOwnedRuleCandidate> candidates,
            bool settleParentOwnership)
        {
            WriteRequests.Add((view, imageName));
            if (protectedWrites.Contains((view, imageName)))
            {
                throw new UnauthorizedAccessException("Protected third-party image received a write request.");
            }

            var key = (view, imageName);
            var image = images[key];
            if (BeforeDelete is not null)
            {
                image = BeforeDelete(view, imageName, image);
            }

            var removed = 0;
            var remaining = image.Rules.ToList();
            foreach (var candidate in candidates)
            {
                var index = remaining.FindIndex(rule =>
                    IfeoOwnedRuleCleanupCoordinator.MatchesCandidate(rule, candidate, Owner));
                if (index < 0)
                {
                    continue;
                }

                remaining.RemoveAt(index);
                removed++;
            }

            var updated = image with { Rules = remaining };
            if (settleParentOwnership)
            {
                var action = IfeoOwnedRuleCleanupCoordinator.DecideParentCleanup(updated, Owner);
                if (action is IfeoParentCleanupAction.RemoveOwner
                    or IfeoParentCleanupAction.RemoveOwnerAndUseFilter)
                {
                    updated = updated with { ParentUseFilterOwned = false };
                }
            }

            images[key] = updated;
            return removed;
        }

        public void SetImage(RegistryView view, IfeoImageSnapshot image)
        {
            images[(view, image.ImageName)] = image;
        }

        public IfeoImageSnapshot GetImage(RegistryView view, string imageName)
        {
            return images[(view, imageName)];
        }

        public void ProtectWrites(RegistryView view, string imageName)
        {
            protectedWrites.Add((view, imageName));
        }
    }
}
