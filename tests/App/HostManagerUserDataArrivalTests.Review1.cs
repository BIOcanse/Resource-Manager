using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerUserDataArrivalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAppliedOwnershipManifestRejectsImportAndPreservesLiveLedger(bool hasValidationNamespace)
    {
        using var fixture = new Fixture();
        if (hasValidationNamespace) _ = fixture.Scope(fixture.Target);
        var store = Assert.Single(HostManagerDurableStorePaths.Resolve(fixture.Target), item => item.Kind == "applied-ownership");
        var configuration = new NativeAppliedOwnershipCreateConfiguration
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.CreateConfigurationSize,
            LedgerInstanceLow = 7, LedgerInstanceHigh = 8, RecordCapacity = 4,
            PrimaryIndexCapacity = 8, PayloadIndexCapacity = 8,
            MaximumResidentBytes = 65536, MaximumImageBytes = 4096
        };
        using (var session = new NativeAppliedOwnershipSession(configuration))
        {
            var promote = new NativeAppliedOwnershipPromoteInput
            {
                AbiVersion = NativeAppliedOwnershipAbi.Version, StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
                ExpectedLedgerRevision = 1,
                Primary = new() { Scope = 1, TargetId = 5, SoftwareId = 6, ProcessStartKey = 100001, ProcessId = 10 },
                OriginalBinding = new()
                {
                    JournalInstanceLow = 11, JournalInstanceHigh = 12,
                    ActionIdentity = new()
                    {
                        ConfigurationGeneration = 1, PlanEpoch = 2, ActionId = 3, HostSessionIncarnation = 4,
                        TargetId = 5, SoftwareId = 6, ProcessStartKey = 100001, ProcessId = 10
                    },
                    Scope = 1, Disposition = 1, DomainMask = 1, GradeValidMask = 1,
                    ProcessFromGrade = 0, ProcessToGrade = -1, MaximumRecoveryAttempts = 3,
                    RecoveryDeadlineUtcMilliseconds = 11000
                },
                Payload = new() { Slot = 1, Generation = 1, Length = 512, DigestLow = 1, DigestHigh = 2 },
                CurrentGrades = new() { ValidMask = 1, ProcessGrade = -1 },
                PromotedAtUtcMilliseconds = 1000
            };
            Assert.Equal(NativeAppliedOwnershipStatus.Ok, session.Promote(promote));
            Assert.Equal(NativeAppliedOwnershipStatus.Ok, session.Get(promote.Primary, out var record));
            Assert.True(record.RecordRevision > 0);
            await new NativeAppliedOwnershipFileStore(store.CanonicalPath).PersistAsync(session, new byte[4096], CancellationToken.None);
        }
        File.Delete(store.ManifestPath);
        var original = File.ReadAllBytes(store.CanonicalPath);
        var authority = HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath;
        var originalAuthority = hasValidationNamespace ? File.ReadAllBytes(authority) : null;

        var error = Assert.Throws<InvalidDataException>(() => fixture.Arrive(true));

        Assert.Contains("applied-ownership", error.Message);
        Assert.Contains(store.CanonicalPath, error.Message);
        Assert.Equal(original, File.ReadAllBytes(store.CanonicalPath));
        Assert.False(File.Exists(store.ManifestPath));
        if (hasValidationNamespace)
        {
            Assert.Equal(originalAuthority, File.ReadAllBytes(authority));
            fixture.AssertUsable();
        }
        else Assert.False(File.Exists(authority));
    }

    [Fact]
    public void FailureAfterAuthorityPromotionRestoresOriginalDataAndUsableAuthority()
    {
        using var fixture = new Fixture();
        _ = fixture.Scope(fixture.Source);
        _ = fixture.Scope(fixture.Target);
        var authority = HostManagerUserDataArrival.Paths(fixture.Target).AuthorityManifestPath;
        var originalAuthority = File.ReadAllBytes(authority);
        var originalData = Path.Combine(fixture.Target, "UserData", "original.txt");
        File.WriteAllText(originalData, "original target data");
        var zip = Path.Combine(fixture.Root, "data.zip");
        UserDataArchive.Create(fixture.Source, zip);
        var injected = false;
        var committer = new Committer(afterCommit: canonical =>
        {
            if (Path.GetFileName(canonical) != "user-data-arrival.json") return;
            var incomingRoot = Directory.GetParent(Path.GetDirectoryName(canonical)!)!.FullName;
            // Preparation succeeds. The first post-promotion directory-access
            // step fails at its final target path, after authority moved too.
            File.WriteAllText(Path.Combine(incomingRoot, "Config", "WebView2"), "injected directory collision");
            injected = true;
        });

        var error = Assert.Throws<IOException>(() => UserDataArchive.Import(fixture.Target, zip,
            hostManagerArrival: new HostManagerUserDataArrival(fixture.Owner, committer)));

        Assert.True(injected);
        Assert.Contains(Path.Combine(fixture.Target, "Config", "WebView2"), error.Message);
        Assert.Equal("original target data", File.ReadAllText(originalData));
        Assert.Equal(originalAuthority, File.ReadAllBytes(authority));
        fixture.AssertUsable();
        Assert.Empty(Directory.GetDirectories(fixture.Root, "ResourceManager-import-*"));
    }
}
