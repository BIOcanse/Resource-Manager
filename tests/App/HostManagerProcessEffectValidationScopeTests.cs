using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerProcessEffectValidationScopeTests
{
    [Fact]
    public void ProductionUnscopedPreservesExistingBehaviorWithoutProbeOrJournal()
    {
        using var fixture = ScopeFixture.Create();
        var snapshot = fixture.Authority.Capture();

        var admitted = fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            new HostManagerComputeProcessIdentity(77, 7001),
            out var permit);

        Assert.True(admitted);
        Assert.NotNull(permit);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(
            permit,
            default));
        Assert.True(fixture.Authority.TryCommitNativeTransactionJournal(
            permit,
            default));
        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.ProductionUnscoped,
            snapshot.State);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void StaleProductionUnscopedSnapshotCannotCrossOpenTransition()
    {
        using var fixture = ScopeFixture.Create();
        var stale = fixture.Authority.Capture();
        var allowed = new HostManagerComputeProcessIdentity(124, 12401);
        var outside = new HostManagerComputeProcessIdentity(125, 12501);
        fixture.Probe.Set(allowed, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(allowed);
        var probeCountAfterOpen = fixture.Probe.CallCount;

        Assert.False(fixture.Authority.TryBeginAdmission(
            stale,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "stale-unscoped-single",
            outside,
            out var singlePermit));
        Assert.Null(singlePermit);
        Assert.False(fixture.Authority.TryBeginBatchAdmission(
            stale,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "stale-unscoped-batch",
            [outside],
            out var batchPermit));
        Assert.Null(batchPermit);
        Assert.Equal(probeCountAfterOpen, fixture.Probe.CallCount);
    }

    [Fact]
    public void OpenStateCommitFailureRollsBackAndAllowsImmediateRetry()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(111, 11101);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        fixture.Committer.FailCommitCallNumber =
            fixture.Committer.CommitCallCount + 2;

        Assert.Throws<InvalidOperationException>(() => fixture.Open(
            identity,
            allowAutomaticMemoryCleanup: true));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.AuditFencePath));
        Assert.True(fixture.Authority.Capture().IsProductionUnscoped);
        Assert.False(fixture.Authority.Capture().AutomaticMemoryCleanupAllowed);

        Assert.Equal("active", fixture.Open(identity).State);
    }

    [Fact]
    public void OpenCleanFenceLostAcknowledgmentIsRecognizedAsCommitted()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(112, 11201);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        fixture.Committer.FailAfterCommitCallNumber =
            fixture.Committer.CommitCallCount + 3;

        var opened = fixture.Open(identity);

        Assert.Equal("active", opened.State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("active", fixture.Authority.GetStatus().State);
    }

    [Fact]
    public void InterruptedOpeningCleanupCompletesAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(113, 11301);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        fixture.Committer.FailAfterCommitCallNumber =
            fixture.Committer.CommitCallCount + 2;
        fixture.Committer.FailDeleteCallNumber =
            fixture.Committer.DeleteCallCount + 1;

        Assert.Throws<InvalidOperationException>(() => fixture.Open(identity));
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);

        var restarted = fixture.CreateRestartedAuthority();

        Assert.True(restarted.Capture().IsProductionUnscoped);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void CaptureReusesPublishedSnapshotAndChangesOnlyAtControlTransitions()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(97, 9701);
        var unscoped = fixture.Authority.Capture();

        Assert.Same(unscoped, fixture.Authority.Capture());
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        var active = fixture.Authority.Capture();
        Assert.NotSame(unscoped, active);
        Assert.Same(active, fixture.Authority.Capture());
        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.Active,
            active.State);

        Assert.True(fixture.Authority.TryBeginAdmission(
            active,
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        var pending = fixture.Authority.Capture();
        Assert.NotSame(active, pending);
        Assert.Same(pending, fixture.Authority.Capture());
        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.FaultedBlocked,
            pending.State);

        var identity3 = CreateJournalIdentity(identity, actionId: 3);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(
            permit,
            identity3));
        Assert.True(fixture.Authority.TryCommitNativeTransactionJournal(
            permit,
            identity3));
        var handedOff = fixture.Authority.Capture();
        Assert.NotSame(pending, handedOff);
        Assert.Same(handedOff, fixture.Authority.Capture());
        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.Active,
            handedOff.State);
    }

    [Fact]
    public void AutomaticMemoryCleanupCapabilityRequiresExplicitOpenAndIsRevokedOnClose()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(126, 12601);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);

        var opened = fixture.Open(
            identity,
            allowAutomaticMemoryCleanup: true,
            allowNonAdaptedMemoryTransaction: false);
        var active = fixture.Authority.Capture();

        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.Active,
            active.State);
        Assert.True(active.AutomaticMemoryCleanupAllowed);
        Assert.False(active.NonAdaptedMemoryTransactionAllowed);

        Assert.Equal("closed", fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
        var closed = fixture.Authority.Capture();

        Assert.True(closed.IsProductionUnscoped);
        Assert.False(closed.AutomaticMemoryCleanupAllowed);
        Assert.False(closed.NonAdaptedMemoryTransactionAllowed);
    }

    [Fact]
    public void ActiveScopeAdmitsOnlyExactAllowlistedJobMemberAndPersistsAudit()
    {
        using var fixture = ScopeFixture.Create();
        var allowed = new HostManagerComputeProcessIdentity(81, 8101);
        fixture.Probe.Set(allowed, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(allowed);
        var snapshot = fixture.Authority.Capture();

        Assert.True(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            allowed,
            out var allowedPermit));
        Assert.NotNull(allowedPermit);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        var identity1 = CreateJournalIdentity(allowed, actionId: 1);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(
            allowedPermit,
            identity1));
        Assert.True(fixture.Authority.TryCommitNativeTransactionJournal(
            allowedPermit,
            identity1));
        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            new HostManagerComputeProcessIdentity(82, 8201),
            out var deniedPermit));
        Assert.Null(deniedPermit);

        var status = fixture.Authority.GetStatus();
        Assert.Equal("active", status.State);
        Assert.Equal(opened.ScopeId, status.ScopeId);
        Assert.Collection(
            status.AuditRecords,
            record =>
            {
                Assert.Equal("nativeProcessPolicyTransaction", record.EffectFamily);
                Assert.Equal("allowedScoped", record.Decision);
            },
            record => Assert.Equal("deniedNotAllowlisted", record.Decision));
        Assert.True(File.Exists(fixture.StatePath));

        var restarted = fixture.CreateRestartedAuthority();
        var restartedStatus = restarted.GetStatus();
        Assert.Equal(status.DocumentSha256, restartedStatus.DocumentSha256);
        Assert.Equal(2, restartedStatus.AuditRecords.Count);
        Assert.Equal("faultedBlocked", restartedStatus.State);
        Assert.True(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void MembershipDriftIsDeniedBeforeEffectAndRecorded()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(83, 8301);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        var snapshot = fixture.Authority.Capture();
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.IdentityMismatch, 87);

        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "restore-pre-ponr",
            identity,
            out var permit));
        Assert.Null(permit);

        var record = Assert.Single(fixture.Authority.GetStatus().AuditRecords);
        Assert.Equal("deniedIdentityMismatch", record.Decision);
        Assert.Equal(87, record.SystemError);
    }

    [Fact]
    public void ExpiredScopeRemainsFailClosedUntilAuthenticatedClose()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(84, 8401);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity, fixture.Time.GetUtcNow().AddMinutes(10));
        fixture.Time.Advance(TimeSpan.FromMinutes(11));
        var snapshot = fixture.Authority.Capture();

        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.ExpiredBlocked,
            snapshot.State);
        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "decision-pre-ponr",
            identity,
            out var permit));
        Assert.Null(permit);
        Assert.Equal("expiredBlocked", fixture.Authority.GetStatus().State);

        var closed = fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken));
        Assert.Equal("closed", closed.State);
        Assert.Equal("deniedExpired", Assert.Single(closed.AuditRecords).Decision);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("closed", fixture.Authority.GetStatus().State);
        Assert.True(fixture.Authority.Capture().IsProductionUnscoped);
        Assert.Equal("closed", fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
    }

    [Fact]
    public void LostClosedReceiptCommitAcknowledgmentCanRetryInProcess()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(108, 10801);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        var request = new HostManagerProcessEffectValidationScopeCloseRequest(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        fixture.Committer.FailAfterCommitCallNumber =
            fixture.Committer.CommitCallCount + 2;

        Assert.Throws<IOException>(() => fixture.Authority.Close(request));
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);

        Assert.Equal("closed", fixture.Authority.Close(request).State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("closed", fixture.Authority.GetStatus().State);
        Assert.True(fixture.Authority.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void InterruptedClosedReceiptCommitCompletesAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(109, 10901);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        fixture.Committer.FailCommitCallNumber =
            fixture.Committer.CommitCallCount + 2;

        Assert.Throws<IOException>(() => fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)));
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));

        var restarted = fixture.CreateRestartedAuthority();

        Assert.True(restarted.Capture().IsProductionUnscoped);
        Assert.Equal("closed", restarted.GetStatus().State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("closed", restarted.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
    }

    [Fact]
    public void NewOpenRetiresDurableClosedReceiptBeforePublishingNextScope()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(111, 11101);
        var secondIdentity = new HostManagerComputeProcessIdentity(112, 11201);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        var request = new HostManagerProcessEffectValidationScopeCloseRequest(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(request).State);

        var second = fixture.Open(secondIdentity);

        Assert.Equal("active", second.State);
        Assert.NotEqual(first.ScopeId, second.ScopeId);
        Assert.Equal("active", fixture.Authority.GetStatus().State);
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.Authority.Close(request));
    }

    [Fact]
    public void ClosedReceiptRetirementLostCommitAcknowledgmentIsRecognizedAsCommitted()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(126, 12601);
        var secondIdentity = new HostManagerComputeProcessIdentity(127, 12701);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        var closeRequest = new HostManagerProcessEffectValidationScopeCloseRequest(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);
        fixture.Committer.FailAfterCommitCallNumber =
            fixture.Committer.CommitCallCount + 1;

        var second = fixture.Open(secondIdentity);

        Assert.Equal("active", second.State);
        Assert.NotEqual(first.ScopeId, second.ScopeId);
        Assert.Equal("active", fixture.Authority.GetStatus().State);
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.Authority.Close(closeRequest));
    }

    [Fact]
    public void ClosedReceiptRetirementAmbiguousCommitAcknowledgmentFaultsClosed()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(128, 12801);
        var secondIdentity = new HostManagerComputeProcessIdentity(129, 12901);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        var closeRequest = new HostManagerProcessEffectValidationScopeCloseRequest(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);
        fixture.Committer.FailAfterCommitMutation = File.Delete;
        fixture.Committer.FailAfterCommitCallNumber =
            fixture.Committer.CommitCallCount + 1;

        var failure = Assert.Throws<InvalidOperationException>(() =>
            fixture.Open(secondIdentity));

        Assert.Contains("could not be reconciled", failure.Message);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        Assert.False(fixture.Authority.Capture().IsProductionUnscoped);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Authority.Close(closeRequest));
        Assert.True(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void DurableClosedReceiptRejectsForeignReleaseProofAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(113, 11301);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        var request = new HostManagerProcessEffectValidationScopeCloseRequest(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(request).State);
        var restarted = fixture.CreateRestartedAuthority();

        Assert.Equal("closed", restarted.GetStatus().State);
        Assert.Throws<UnauthorizedAccessException>(() => restarted.Close(request with
        {
            ScopeId = Guid.NewGuid()
        }));
        Assert.Throws<UnauthorizedAccessException>(() => restarted.Close(request with
        {
            RunNonce = Guid.NewGuid()
        }));
        Assert.Throws<UnauthorizedAccessException>(() => restarted.Close(request with
        {
            ReleaseToken = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        }));
        Assert.Equal("closed", restarted.Close(request).State);
    }

    [Fact]
    public void ClosedReceiptRetirementFailureStaysClosedUntilOpenRetry()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(114, 11401);
        var secondIdentity = new HostManagerComputeProcessIdentity(115, 11501);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        Assert.Equal("closed", fixture.Authority.Close(new(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
        fixture.Committer.FailDeleteCallNumber =
            fixture.Committer.DeleteCallCount + 1;

        Assert.Throws<IOException>(() => fixture.Open(secondIdentity));
        Assert.Equal("closed", fixture.Authority.GetStatus().State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));

        var second = fixture.Open(secondIdentity);
        Assert.Equal("active", second.State);
        Assert.NotEqual(first.ScopeId, second.ScopeId);
    }

    [Fact]
    public void InterruptedClosedReceiptRetirementCompletesAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(121, 12101);
        var secondIdentity = new HostManagerComputeProcessIdentity(122, 12201);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        Assert.Equal("closed", fixture.Authority.Close(new(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
        fixture.Committer.FailDeleteCallNumber =
            fixture.Committer.DeleteCallCount + 2;

        Assert.Throws<IOException>(() => fixture.Open(secondIdentity));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));

        var restarted = fixture.CreateRestartedAuthority();
        Assert.Equal("productionUnscoped", restarted.GetStatus().State);
        Assert.True(restarted.Capture().IsProductionUnscoped);
        Assert.False(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void OrphanClosedReceiptFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(123, 12301);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        Assert.Equal("closed", fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
        File.Delete(fixture.StatePath);

        var restarted = fixture.CreateRestartedAuthority();

        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.False(restarted.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void InvalidNewOpenEnvelopeDoesNotRetireDurableClosedReceipt()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(116, 11601);
        var secondIdentity = new HostManagerComputeProcessIdentity(117, 11701);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.ExactMember);
        var first = fixture.Open(firstIdentity);
        var closeRequest = new HostManagerProcessEffectValidationScopeCloseRequest(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);

        Assert.Throws<ArgumentException>(() => fixture.Authority.Open(new(
            Guid.NewGuid(),
            $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
            fixture.Time.GetUtcNow().AddMinutes(10),
            "short",
            [new(
                secondIdentity.ProcessId,
                checked((long)secondIdentity.ProcessStartKey))])));

        Assert.Equal("closed", fixture.Authority.GetStatus().State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);
    }

    [Fact]
    public void NonMemberNewOpenDoesNotRetireDurableClosedReceipt()
    {
        using var fixture = ScopeFixture.Create();
        var firstIdentity = new HostManagerComputeProcessIdentity(118, 11801);
        var secondIdentity = new HostManagerComputeProcessIdentity(119, 11901);
        fixture.Probe.Set(firstIdentity, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(secondIdentity, WindowsJobMembershipStatus.NotMember);
        var first = fixture.Open(firstIdentity);
        var closeRequest = new HostManagerProcessEffectValidationScopeCloseRequest(
            first.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken);
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);

        Assert.Throws<InvalidOperationException>(() => fixture.Open(secondIdentity));

        Assert.Equal("closed", fixture.Authority.GetStatus().State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
        Assert.Equal("closed", fixture.Authority.Close(closeRequest).State);
    }

    [Fact]
    public void OrphanCleanFenceStillFailsClosed()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(110, 11001);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        File.Delete(fixture.StatePath);

        Assert.Equal(
            "faultedBlocked",
            fixture.CreateRestartedAuthority().GetStatus().State);
    }

    [Fact]
    public void OrphanClosingFenceFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(120, 12001);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        fixture.Committer.FailCommitCallNumber =
            fixture.Committer.CommitCallCount + 2;

        Assert.Throws<IOException>(() => fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)));
        File.Delete(fixture.StatePath);

        Assert.Equal(
            "faultedBlocked",
            fixture.CreateRestartedAuthority().GetStatus().State);
        Assert.True(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void TamperedPersistentScopeFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(85, 8501);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        var json = File.ReadAllText(fixture.StatePath);
        File.WriteAllText(
            fixture.StatePath,
            json.Replace("\"processId\": 85", "\"processId\": 86", StringComparison.Ordinal));

        var restarted = fixture.CreateRestartedAuthority();
        var snapshot = restarted.Capture();

        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.FaultedBlocked,
            snapshot.State);
        Assert.False(restarted.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            identity,
            out var permit));
        Assert.Null(permit);
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
    }

    [Fact]
    public void ScopeAuditPersistenceFailureLeavesIntentFenceAndFaultsAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(87, 8701);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        var snapshot = fixture.Authority.Capture();
        fixture.Committer.FailCommitCallNumber = fixture.Committer.CommitCallCount + 2;

        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            identity,
            out var permit));
        Assert.Null(permit);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));

        var restarted = fixture.CreateRestartedAuthority();
        var restartedSnapshot = restarted.Capture();
        Assert.Equal(
            HostManagerProcessEffectValidationScopeState.FaultedBlocked,
            restartedSnapshot.State);
        Assert.False(restarted.TryBeginAdmission(
            restartedSnapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            identity,
            out var restartedPermit));
        Assert.Null(restartedPermit);

        var closed = restarted.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken));
        Assert.Equal("closed", closed.State);
        Assert.True(File.Exists(fixture.StatePath));
        Assert.True(File.Exists(fixture.AuditFencePath));
    }

    [Fact]
    public void FirstIntentPersistenceFailureCannotResumeOldCleanScopeAfterRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(93, 9301);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        var snapshot = fixture.Authority.Capture();
        fixture.Committer.FailCommitCallNumber = fixture.Committer.CommitCallCount + 1;

        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            identity,
            out var permit));
        Assert.Null(permit);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);

        var restarted = fixture.CreateRestartedAuthority();
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.False(restarted.TryBeginAdmission(
            restarted.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "candidate",
            identity,
            out var restartedPermit));
        Assert.Null(restartedPermit);
    }

    [Fact]
    public void AuditCommittedFenceFailureKeepsCommittedAuditFailClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(88, 8801);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        var snapshot = fixture.Authority.Capture();
        fixture.Committer.FailCommitCallNumber = fixture.Committer.CommitCallCount + 3;

        Assert.False(fixture.Authority.TryBeginAdmission(
            snapshot,
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.Null(permit);
        Assert.Equal(
            "allowedScoped",
            Assert.Single(fixture.Authority.GetStatus().AuditRecords).Decision);

        var restarted = fixture.CreateRestartedAuthority();
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.Equal(
            "allowedScoped",
            Assert.Single(restarted.GetStatus().AuditRecords).Decision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DurableHandoffFailureNeverReachesAReusableCleanAdmission(
        int failingHandoffCommitOffset)
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(94, 9401);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        fixture.Committer.FailCommitCallNumber =
            fixture.Committer.CommitCallCount + failingHandoffCommitOffset;

        var journalIdentity = CreateJournalIdentity(identity, actionId: 2);
        var declared = fixture.Authority.TryDeclareNativeTransactionJournal(
            permit,
            journalIdentity);
        Assert.False(declared && fixture.Authority.TryCommitNativeTransactionJournal(
            permit,
            journalIdentity));
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        Assert.Equal(
            "faultedBlocked",
            fixture.CreateRestartedAuthority().GetStatus().State);
    }

    [Fact]
    public void UnsettledWrapperCannotOverwriteTheFirstDurableHandoffFailure()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(98, 9801);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        fixture.Committer.FailCommitCallNumber =
            fixture.Committer.CommitCallCount + 1;

        Assert.False(fixture.Authority.TryDeclareNativeTransactionJournal(
            permit,
            CreateJournalIdentity(identity, actionId: 4)));
        var firstFailure = fixture.Authority.GetStatus().Failure;
        Assert.Contains("handoff-declare-failed", firstFailure);

        fixture.Authority.MarkAdmissionUnsettled(permit, "outer-wrapper");

        Assert.Equal(firstFailure, fixture.Authority.GetStatus().Failure);
    }

    [Fact]
    public void MemoryCleanupAdmissionIsOneExactDurableBatch()
    {
        using var fixture = ScopeFixture.Create();
        var first = new HostManagerComputeProcessIdentity(95, 9501);
        var second = new HostManagerComputeProcessIdentity(96, 9601);
        fixture.Probe.Set(first, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(second, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open([first, second]);

        Assert.True(fixture.Authority.TryBeginBatchAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "decision-batch-pre-ponr",
            [second, first],
            out var permit));
        Assert.NotNull(permit);
        var batch = new HostManagerMemoryCleanupAttemptBatch(
            Guid.NewGuid(),
            new HashSet<HostManagerComputeProcessIdentity> { first, second },
            AttemptGeneration: 7);
        Assert.True(fixture.Authority.TryDeclareMemoryCleanupAttemptBatch(
            permit,
            batch,
            attemptGeneration: 7));
        Assert.True(fixture.Authority.TryCommitMemoryCleanupAttemptBatch(
            permit,
            batch,
            attemptGeneration: 7));

        var status = fixture.Authority.GetStatus();
        Assert.Equal("active", status.State);
        Assert.Equal(2, status.AuditRecords.Count);
        Assert.All(
            status.AuditRecords,
            static record => Assert.Equal("allowedScoped", record.Decision));
    }

    [Fact]
    public void CommittedNativeHandoffSurvivesRestartAndAuthorizesOnlyExactRecovery()
    {
        using var fixture = ScopeFixture.Create();
        var process = new HostManagerComputeProcessIdentity(98, 9801);
        fixture.Probe.Set(process, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(process);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            process,
            out var permit));
        var identity = CreateJournalIdentity(process, actionId: 11);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(permit!, identity));
        Assert.True(fixture.Authority.TryCommitNativeTransactionJournal(permit!, identity));

        var restarted = fixture.CreateRestartedAuthority();
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.True(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            identity));
        var foreign = CreateJournalIdentity(process, actionId: 12);
        Assert.False(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            foreign));
    }

    [Fact]
    public void DeclaredHandoffRestartReconcilesExactTargetAndAbandonsProvenAbsence()
    {
        using var exactFixture = ScopeFixture.Create();
        var exactProcess = new HostManagerComputeProcessIdentity(99, 9901);
        exactFixture.Probe.Set(exactProcess, WindowsJobMembershipStatus.ExactMember);
        _ = exactFixture.Open(exactProcess);
        Assert.True(exactFixture.Authority.TryBeginAdmission(
            exactFixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            exactProcess,
            out var exactPermit));
        var exactIdentity = CreateJournalIdentity(exactProcess, actionId: 13);
        Assert.True(exactFixture.Authority.TryDeclareNativeTransactionJournal(
            exactPermit!,
            exactIdentity));
        var exactRestart = exactFixture.CreateRestartedAuthority();
        Assert.True(exactRestart.ReconcileDeclaredNativeHandoff([exactIdentity]));
        Assert.True(exactRestart.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            exactIdentity));

        using var absentFixture = ScopeFixture.Create();
        var absentProcess = new HostManagerComputeProcessIdentity(100, 10001);
        absentFixture.Probe.Set(absentProcess, WindowsJobMembershipStatus.ExactMember);
        _ = absentFixture.Open(absentProcess);
        Assert.True(absentFixture.Authority.TryBeginAdmission(
            absentFixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            absentProcess,
            out var absentPermit));
        var absentIdentity = CreateJournalIdentity(absentProcess, actionId: 14);
        Assert.True(absentFixture.Authority.TryDeclareNativeTransactionJournal(
            absentPermit!,
            absentIdentity));
        var absentRestart = absentFixture.CreateRestartedAuthority();
        Assert.True(absentRestart.ReconcileDeclaredNativeHandoff([]));
        Assert.False(absentRestart.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            absentIdentity));
    }

    [Fact]
    public void JournalPreparedLostAckFinalizesCommittedHandoffOnRestart()
    {
        using var fixture = ScopeFixture.Create();
        var process = new HostManagerComputeProcessIdentity(101, 10101);
        fixture.Probe.Set(process, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(process);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "fresh-apply-pre-ponr",
            process,
            out var permit));
        var identity = CreateJournalIdentity(process, actionId: 15);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(permit!, identity));
        fixture.Committer.FailCommitCallNumber = fixture.Committer.CommitCallCount + 2;
        Assert.False(fixture.Authority.TryCommitNativeTransactionJournal(permit!, identity));

        var restarted = fixture.CreateRestartedAuthority();
        Assert.True(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            identity));
    }

    [Fact]
    public void PriorHandoffCanDeriveRecoveryAdmissionWithoutJobProbe()
    {
        using var fixture = ScopeFixture.Create();
        var process = new HostManagerComputeProcessIdentity(102, 10201);
        fixture.Probe.Set(process, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(process);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "fresh-apply-pre-ponr",
            process,
            out var sourcePermit));
        var source = CreateJournalIdentity(process, actionId: 16);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(
            sourcePermit!,
            source));
        Assert.True(fixture.Authority.TryCommitNativeTransactionJournal(
            sourcePermit!,
            source));
        var probeCalls = fixture.Probe.CallCount;

        var restarted = fixture.CreateRestartedAuthority();
        Assert.True(restarted.TryBeginRecoveryAdmission(
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "owned-restore-pre-ponr",
            process,
            source,
            out var recoveryPermit));
        Assert.Equal(probeCalls, fixture.Probe.CallCount);
        var restore = CreateJournalIdentity(process, actionId: 17);
        Assert.True(restarted.TryDeclareNativeTransactionJournal(
            recoveryPermit!,
            restore));
        Assert.True(restarted.TryCommitNativeTransactionJournal(
            recoveryPermit!,
            restore));
        Assert.Contains(
            restarted.GetStatus().AuditRecords,
            static record => record.Decision == "allowedPriorHandoffRecovery");

        var wrongSource = CreateJournalIdentity(process, actionId: 18);
        Assert.False(restarted.TryBeginRecoveryAdmission(
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "owned-restore-pre-ponr",
            process,
            wrongSource,
            out _));
        Assert.Equal(probeCalls, fixture.Probe.CallCount);
    }

    [Fact]
    public void DeclaredMemoryCleanupBatchReconcilesAsOneExactTarget()
    {
        using var fixture = ScopeFixture.Create();
        var first = new HostManagerComputeProcessIdentity(103, 10301);
        var second = new HostManagerComputeProcessIdentity(104, 10401);
        fixture.Probe.Set(first, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(second, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open([first, second]);
        Assert.True(fixture.Authority.TryBeginBatchAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "decision-batch-pre-ponr",
            [first, second],
            out var permit));
        var batch = new HostManagerMemoryCleanupAttemptBatch(
            Guid.NewGuid(),
            new HashSet<HostManagerComputeProcessIdentity> { first, second },
            AttemptGeneration: 9);
        Assert.True(fixture.Authority.TryDeclareMemoryCleanupAttemptBatch(
            permit!,
            batch,
            attemptGeneration: 9));

        var restarted = fixture.CreateRestartedAuthority();
        Assert.True(restarted.ReconcileDeclaredMemoryCleanupHandoff([batch]));
        Assert.True(restarted.ReconcileDeclaredMemoryCleanupHandoff([]));
    }

    [Fact]
    public void DeclaredMemoryCleanupBatchRejectsSamePrimaryIdentityWithChangedProcessSet()
    {
        using var fixture = ScopeFixture.Create();
        var first = new HostManagerComputeProcessIdentity(105, 10501);
        var second = new HostManagerComputeProcessIdentity(106, 10601);
        fixture.Probe.Set(first, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(second, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open([first, second]);
        Assert.True(fixture.Authority.TryBeginBatchAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "decision-batch-pre-ponr",
            [first, second],
            out var permit));
        var batchId = Guid.NewGuid();
        var declared = new HostManagerMemoryCleanupAttemptBatch(
            batchId,
            new HashSet<HostManagerComputeProcessIdentity> { first, second },
            AttemptGeneration: 10);
        Assert.True(fixture.Authority.TryDeclareMemoryCleanupAttemptBatch(
            permit!,
            declared,
            attemptGeneration: 10));
        var changed = new HostManagerMemoryCleanupAttemptBatch(
            batchId,
            new HashSet<HostManagerComputeProcessIdentity>
            {
                first,
                second with { ProcessStartKey = second.ProcessStartKey + 1 }
            },
            AttemptGeneration: 10);

        var restarted = fixture.CreateRestartedAuthority();

        Assert.False(restarted.ReconcileDeclaredMemoryCleanupHandoff([changed]));
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
    }

    [Fact]
    public void CommittedMemoryCleanupBatchRejectsSamePrimaryIdentityWithRemovedProcess()
    {
        using var fixture = ScopeFixture.Create();
        var first = new HostManagerComputeProcessIdentity(107, 10701);
        var second = new HostManagerComputeProcessIdentity(108, 10801);
        fixture.Probe.Set(first, WindowsJobMembershipStatus.ExactMember);
        fixture.Probe.Set(second, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open([first, second]);
        Assert.True(fixture.Authority.TryBeginBatchAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
            "decision-batch-pre-ponr",
            [first, second],
            out var permit));
        var batchId = Guid.NewGuid();
        var committed = new HostManagerMemoryCleanupAttemptBatch(
            batchId,
            new HashSet<HostManagerComputeProcessIdentity> { first, second },
            AttemptGeneration: 11);
        Assert.True(fixture.Authority.TryDeclareMemoryCleanupAttemptBatch(
            permit!,
            committed,
            attemptGeneration: 11));
        Assert.True(fixture.Authority.TryCommitMemoryCleanupAttemptBatch(
            permit!,
            committed,
            attemptGeneration: 11));
        var changed = new HostManagerMemoryCleanupAttemptBatch(
            batchId,
            new HashSet<HostManagerComputeProcessIdentity> { first },
            AttemptGeneration: 11);

        var restarted = fixture.CreateRestartedAuthority();

        Assert.False(restarted.ReconcileDeclaredMemoryCleanupHandoff([changed]));
        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
    }

    [Fact]
    public void MemoryCleanupProcessSetHashIsOrderIndependent()
    {
        var first = new HostManagerComputeProcessIdentity(109, 10901);
        var second = new HostManagerComputeProcessIdentity(110, 11001);

        var forward = HostManagerProcessEffectValidationDurableHandoffDocument
            .ComputeMemoryCleanupProcessSetSha256([first, second]);
        var reverse = HostManagerProcessEffectValidationDurableHandoffDocument
            .ComputeMemoryCleanupProcessSetSha256([second, first]);

        Assert.Equal(forward, reverse);
        Assert.Matches("^[0-9A-F]{64}$", forward);
    }

    [Fact]
    public void MissingOrTamperedAuditFenceFailsClosedAcrossRestart()
    {
        using var missingFixture = ScopeFixture.Create();
        var missingIdentity = new HostManagerComputeProcessIdentity(89, 8901);
        missingFixture.Probe.Set(missingIdentity, WindowsJobMembershipStatus.ExactMember);
        _ = missingFixture.Open(missingIdentity);
        File.Delete(missingFixture.AuditFencePath);

        Assert.Equal(
            "faultedBlocked",
            missingFixture.CreateRestartedAuthority().GetStatus().State);

        using var tamperedFixture = ScopeFixture.Create();
        var tamperedIdentity = new HostManagerComputeProcessIdentity(90, 9001);
        tamperedFixture.Probe.Set(tamperedIdentity, WindowsJobMembershipStatus.ExactMember);
        _ = tamperedFixture.Open(tamperedIdentity);
        var fenceJson = File.ReadAllText(tamperedFixture.AuditFencePath);
        File.WriteAllText(
            tamperedFixture.AuditFencePath,
            fenceJson.Replace(
                "\"committedAuditRecordCount\": 0",
                "\"committedAuditRecordCount\": 1",
                StringComparison.Ordinal));

        Assert.Equal(
            "faultedBlocked",
            tamperedFixture.CreateRestartedAuthority().GetStatus().State);
    }

    [Fact]
    public void ValidAuditFenceFromAnotherScopeFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        using var foreignFixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(91, 9101);
        var foreignIdentity = new HostManagerComputeProcessIdentity(92, 9201);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        foreignFixture.Probe.Set(foreignIdentity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        _ = foreignFixture.Open(foreignIdentity);
        File.Copy(
            foreignFixture.AuditFencePath,
            fixture.AuditFencePath,
            overwrite: true);

        Assert.Equal(
            "faultedBlocked",
            fixture.CreateRestartedAuthority().GetStatus().State);
    }

    [Fact]
    public void PreviousRuntimeAuthorizesOnlyItsExactCommittedRecoveryTarget()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(101, 10101);
        var journalIdentity = CreateJournalIdentity(identity, actionId: 11);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        Assert.True(TransferNative(fixture.Authority, permit, journalIdentity));

        var restarted = fixture.CreateRestartedAuthority();

        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.False(restarted.TryBeginAdmission(
            restarted.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "fresh-effect",
            identity,
            out var freshPermit));
        Assert.Null(freshPermit);
        Assert.True(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            journalIdentity));
        Assert.False(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            CreateJournalIdentity(identity, actionId: 12)));
    }

    [Fact]
    public void RestartReconcilesDeclaredHandoffAgainstExactAuthoritativeJournal()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(102, 10201);
        var journalIdentity = CreateJournalIdentity(identity, actionId: 13);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        Assert.True(fixture.Authority.TryDeclareNativeTransactionJournal(
            permit,
            journalIdentity));

        var restarted = fixture.CreateRestartedAuthority();
        Assert.True(restarted.ReconcileDeclaredNativeHandoff([journalIdentity]));
        Assert.True(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            journalIdentity));

        Assert.True(restarted.ReconcileDeclaredNativeHandoff([]));
        Assert.False(restarted.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            journalIdentity));
        Assert.Equal("closed", restarted.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
    }

    [Fact]
    public void NonRuntimeFaultCannotUseCommittedRecoveryAuthority()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(103, 10301);
        var journalIdentity = CreateJournalIdentity(identity, actionId: 14);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var permit));
        Assert.NotNull(permit);
        Assert.True(TransferNative(fixture.Authority, permit, journalIdentity));
        fixture.Committer.FailCommitCallNumber = fixture.Committer.CommitCallCount + 1;

        Assert.False(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "fault-injection",
            identity,
            out _));
        Assert.False(fixture.Authority.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            journalIdentity));
    }

    [Fact]
    public void AuthoritativeRetirementPreservesLiveParentChainThenAllowsClose()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(104, 10401);
        var source = CreateJournalIdentity(identity, actionId: 15);
        var child = CreateJournalIdentity(identity, actionId: 16);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        var opened = fixture.Open(identity);
        Assert.True(fixture.Authority.TryBeginAdmission(
            fixture.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "native-process-policy-pre-ponr",
            identity,
            out var sourcePermit));
        Assert.NotNull(sourcePermit);
        Assert.True(TransferNative(fixture.Authority, sourcePermit, source));
        Assert.True(fixture.Authority.TryBeginRecoveryAdmission(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            "owned-restore-pre-ponr",
            identity,
            source,
            out var childPermit));
        Assert.NotNull(childPermit);
        Assert.True(TransferNative(fixture.Authority, childPermit, child));

        Assert.True(fixture.Authority.ReconcileDeclaredNativeHandoff([child]));
        Assert.True(fixture.Authority.TryAuthorizeNativeRecovery(
            HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
            source));
        Assert.Throws<InvalidOperationException>(() => fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)));

        Assert.True(fixture.Authority.ReconcileDeclaredNativeHandoff([]));
        Assert.Equal("closed", fixture.Authority.Close(new(
            opened.ScopeId!.Value,
            fixture.RunNonce,
            fixture.ReleaseToken)).State);
    }

    [Fact]
    public void DurableScopeUsesProtectedDirectoryAndFileAcls()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(105, 10501);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);

        Assert.True(new DirectoryInfo(fixture.Root).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new DirectoryInfo(Path.GetDirectoryName(
            fixture.AuthorityManifestPath)!).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new FileInfo(fixture.StatePath).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new FileInfo(fixture.AuditFencePath).GetAccessControl()
            .AreAccessRulesProtected);
        Assert.True(new FileInfo(fixture.AuthorityManifestPath).GetAccessControl()
            .AreAccessRulesProtected);
    }

    [Fact]
    public void EmptyExistingStorageDirectoryMustStillPassNamespaceValidation()
    {
        using var safeFixture = ScopeFixture.Create();
        using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
                   safeFixture.StatePath,
                   create: true))
        {
        }
        Assert.Equal(
            "productionUnscoped",
            safeFixture.CreateRestartedAuthority().GetStatus().State);

        using var unsafeFixture = ScopeFixture.Create();
        var unsafeContainerRoot = unsafeFixture.ContainerRoot;
        var directory = Directory.CreateDirectory(unsafeFixture.Root);
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        Assert.Equal(
            "faultedBlocked",
            unsafeFixture.CreateRestartedAuthority().GetStatus().State);
        unsafeFixture.Dispose();
        Assert.False(ScopeFixture.ExistsExact(unsafeContainerRoot));
    }

    [Fact]
    public void OpenStorageSharingUncertaintyLatchesFailClosed()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(120, 12001);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        using (WindowsProcessEffectValidationScopeStorage.AcquireDirectory(
                   fixture.StatePath,
                   create: true))
        {
        }
        using var blocker = new FileStream(
            fixture.StatePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Open(identity));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        Assert.False(fixture.Authority.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void WholeStorageDirectoryDisappearanceFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(121, 12101);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        _ = fixture.Open(identity);
        fixture.DeleteStorageTreeForTest();

        var restarted = fixture.CreateRestartedAuthority();

        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.False(restarted.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void EmptyStorageDirectoryDisappearanceFailsClosedBeforeOpen()
    {
        using var fixture = ScopeFixture.Create();
        var identity = new HostManagerComputeProcessIdentity(122, 12201);
        fixture.Probe.Set(identity, WindowsJobMembershipStatus.ExactMember);
        fixture.DeleteStorageTreeForTest();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Open(identity));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal("faultedBlocked", fixture.Authority.GetStatus().State);
        Assert.False(fixture.Authority.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void AuthorityManifestDisappearanceFailsClosedAcrossRestart()
    {
        using var fixture = ScopeFixture.Create();
        fixture.DeleteAuthorityTreeForTest();

        var restarted = fixture.CreateRestartedAuthority();

        Assert.Equal("faultedBlocked", restarted.GetStatus().State);
        Assert.False(restarted.Capture().IsProductionUnscoped);
    }

    [Fact]
    public void ExtraFileAceAndInheritedParentAclBothFailClosedOnRestart()
    {
        using var fileFixture = ScopeFixture.Create();
        var fileIdentity = new HostManagerComputeProcessIdentity(106, 10601);
        fileFixture.Probe.Set(fileIdentity, WindowsJobMembershipStatus.ExactMember);
        _ = fileFixture.Open(fileIdentity);
        var fileSecurity = new FileInfo(fileFixture.StatePath).GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        new FileInfo(fileFixture.StatePath).SetAccessControl(fileSecurity);
        Assert.Equal(
            "faultedBlocked",
            fileFixture.CreateRestartedAuthority().GetStatus().State);

        using var directoryFixture = ScopeFixture.Create();
        var directoryIdentity = new HostManagerComputeProcessIdentity(107, 10701);
        directoryFixture.Probe.Set(
            directoryIdentity,
            WindowsJobMembershipStatus.ExactMember);
        _ = directoryFixture.Open(directoryIdentity);
        var directory = new DirectoryInfo(directoryFixture.Root);
        var directorySecurity = directory.GetAccessControl();
        directorySecurity.SetAccessRuleProtection(
            isProtected: false,
            preserveInheritance: true);
        directory.SetAccessControl(directorySecurity);
        Assert.Equal(
            "faultedBlocked",
            directoryFixture.CreateRestartedAuthority().GetStatus().State);
    }

    private static NativeTransactionJournalIdentity CreateJournalIdentity(
        HostManagerComputeProcessIdentity process,
        ulong actionId)
        => new()
        {
            ConfigurationGeneration = 1,
            PlanEpoch = 2,
            ActionId = actionId,
            HostSessionIncarnation = 3,
            TargetId = 4,
            SoftwareId = 5,
            ProcessStartKey = process.ProcessStartKey,
            ProcessId = checked((uint)process.ProcessId)
        };

    private static bool TransferNative(
        HostManagerProcessEffectValidationScopeAuthority authority,
        HostManagerProcessEffectValidationAdmissionPermit permit,
        in NativeTransactionJournalIdentity identity)
        => authority.TryDeclareNativeTransactionJournal(permit, identity)
            && authority.TryCommitNativeTransactionJournal(permit, identity);

    private sealed class ScopeFixture : IDisposable
    {
        private const int MaximumCleanupEntries = 256;
        private const int MaximumCleanupDepth = 8;
        private const string TempRootPrefix = "rm-process-effect-scope-";
        private readonly string containerRoot;

        private ScopeFixture(
            string containerRoot,
            string storageRoot,
            string statePath,
            string authorityManifestPath,
            MutableTimeProvider time,
            RecordingJobMembershipProbe probe,
            RecordingScopeCommitter committer,
            HostManagerProcessEffectValidationScopeAuthority authority)
        {
            this.containerRoot = containerRoot;
            Root = storageRoot;
            StatePath = statePath;
            AuthorityManifestPath = authorityManifestPath;
            Time = time;
            Probe = probe;
            Committer = committer;
            Authority = authority;
        }

        internal string Root { get; }
        internal string ContainerRoot => containerRoot;
        internal string StatePath { get; }
        internal string AuditFencePath => $"{StatePath}.audit-fence.json";
        internal string AuthorityManifestPath { get; }
        internal MutableTimeProvider Time { get; }
        internal RecordingJobMembershipProbe Probe { get; }
        internal RecordingScopeCommitter Committer { get; }
        internal HostManagerProcessEffectValidationScopeAuthority Authority { get; }
        internal Guid RunNonce { get; } = Guid.NewGuid();
        internal string ReleaseToken { get; } = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        internal static ScopeFixture Create()
        {
            var containerRoot = Path.Combine(
                Path.GetTempPath(),
                $"rm-process-effect-scope-{Guid.NewGuid():N}");
            var storageRoot = Path.Combine(containerRoot, "Validation");
            var statePath = Path.Combine(storageRoot, "scope.json");
            var authorityManifestPath = Path.Combine(
                containerRoot,
                "Authority",
                "process-effect-scope.json");
            var time = new MutableTimeProvider(
                new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
            var probe = new RecordingJobMembershipProbe();
            var committer = new RecordingScopeCommitter();
            return new(
                containerRoot,
                storageRoot,
                statePath,
                authorityManifestPath,
                time,
                probe,
                committer,
                new HostManagerProcessEffectValidationScopeAuthority(
                    statePath,
                    time,
                    probe,
                    committer,
                    authorityManifestPath: authorityManifestPath));
        }

        internal HostManagerProcessEffectValidationScopeStatus Open(
            HostManagerComputeProcessIdentity identity,
            DateTimeOffset? expiresAt = null,
            bool allowAutomaticMemoryCleanup = false,
            bool allowNonAdaptedMemoryTransaction = true)
            => Open(
                [identity],
                expiresAt,
                allowAutomaticMemoryCleanup,
                allowNonAdaptedMemoryTransaction);

        internal HostManagerProcessEffectValidationScopeStatus Open(
            IReadOnlyList<HostManagerComputeProcessIdentity> identities,
            DateTimeOffset? expiresAt = null,
            bool allowAutomaticMemoryCleanup = false,
            bool allowNonAdaptedMemoryTransaction = true)
            => Authority.Open(new(
                RunNonce,
                $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
                expiresAt ?? Time.GetUtcNow().AddMinutes(10),
                ReleaseToken,
                identities.Select(static identity => new
                    HostManagerProcessEffectValidationIdentity(
                        identity.ProcessId,
                        checked((long)identity.ProcessStartKey)))
                    .ToArray(),
                allowAutomaticMemoryCleanup,
                allowNonAdaptedMemoryTransaction));

        internal HostManagerProcessEffectValidationScopeAuthority CreateRestartedAuthority()
            => new(
                StatePath,
                Time,
                Probe,
                Committer,
                authorityManifestPath: AuthorityManifestPath);

        internal void DeleteStorageTreeForTest()
            => DeleteOwnedTree(Root);

        internal void DeleteAuthorityTreeForTest()
            => DeleteOwnedTree(Path.GetDirectoryName(AuthorityManifestPath)!);

        internal static bool ExistsExact(string path)
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public void Dispose() => DeleteOwnedTree(containerRoot);

        private void DeleteOwnedTree(string root)
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var canonicalContainer = Path.GetFullPath(containerRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var containerName = Path.GetFileName(canonicalContainer);
            var nonceText = containerName.StartsWith(
                TempRootPrefix,
                StringComparison.Ordinal)
                ? containerName[TempRootPrefix.Length..]
                : string.Empty;
            if (!string.Equals(
                    Path.GetDirectoryName(canonicalContainer),
                    tempRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(nonceText, "N", out _))
            {
                throw new InvalidOperationException(
                    "The validation scope test root escaped the system temporary directory.");
            }

            var canonicalRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var containerPrefix = canonicalContainer + Path.DirectorySeparatorChar;
            if (!canonicalRoot.Equals(
                    canonicalContainer,
                    StringComparison.OrdinalIgnoreCase)
                && !canonicalRoot.StartsWith(
                    containerPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The validation scope cleanup target escaped its owned test root.");
            }

            if (!ExistsExact(canonicalRoot))
            {
                return;
            }
            RequireRealDirectory(canonicalRoot);

            var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
            var stack = new Stack<(string Path, int Depth)>();
            var directories = new List<(string Path, int Depth)>();
            var files = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                canonicalRoot
            };
            stack.Push((canonicalRoot, 0));
            var entryCount = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!current.Path.Equals(
                        canonicalContainer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    RestoreDirectoryAccess(current.Path);
                }
                foreach (var entry in new DirectoryInfo(current.Path)
                             .EnumerateFileSystemInfos())
                {
                    entryCount++;
                    if (entryCount > MaximumCleanupEntries)
                    {
                        throw new InvalidOperationException(
                            "The validation scope test cleanup exceeded its entry bound.");
                    }

                    var entryPath = Path.GetFullPath(entry.FullName);
                    if (!entryPath.StartsWith(
                            rootPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The validation scope test cleanup entry escaped its root.");
                    }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "The validation scope test cleanup rejected a reparse point.");
                    }

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        var depth = current.Depth + 1;
                        if (depth > MaximumCleanupDepth || !visited.Add(entryPath))
                        {
                            throw new InvalidOperationException(
                                "The validation scope test cleanup exceeded its directory bound.");
                        }
                        directories.Add((entryPath, depth));
                        stack.Push((entryPath, depth));
                    }
                    else
                    {
                        files.Add(entryPath);
                    }
                }
            }

            foreach (var file in files)
            {
                RequireContainedPath(canonicalRoot, file);
                RestoreFileAccess(file);
                File.Delete(file);
            }
            directories.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
            foreach (var child in directories)
            {
                RequireContainedPath(canonicalRoot, child.Path);
                RequireRealDirectory(child.Path);
                Directory.Delete(child.Path, recursive: false);
            }
            RequireRealDirectory(canonicalRoot);
            Directory.Delete(canonicalRoot, recursive: false);
        }

        private static void RequireContainedPath(string root, string path)
        {
            var prefix = root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The validation scope test cleanup path escaped its root.");
            }
        }

        private static void RequireRealDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The validation scope test cleanup target is not a real directory.");
            }
        }

        private static void RestoreDirectoryAccess(string path)
        {
            var current = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "The validation scope test cleanup has no current Windows identity.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(current);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }

        private static void RestoreFileAccess(string path)
        {
            var current = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "The validation scope test cleanup has no current Windows identity.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(current);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan value) => current += value;
    }

    private sealed class RecordingJobMembershipProbe : IWindowsJobMembershipProbe
    {
        private readonly Dictionary<HostManagerComputeProcessIdentity, WindowsJobMembershipResult>
            results = [];

        internal int CallCount { get; private set; }

        internal void Set(
            HostManagerComputeProcessIdentity identity,
            WindowsJobMembershipStatus status,
            int systemError = 0)
            => results[identity] = new(status, systemError);

        public WindowsJobMembershipResult Probe(
            string jobName,
            HostManagerComputeProcessIdentity identity)
        {
            CallCount++;
            Assert.StartsWith(
                "Global\\ResourceManager-NonAdaptedOptimizationLab-",
                jobName,
                StringComparison.Ordinal);
            return results.GetValueOrDefault(
                identity,
                new WindowsJobMembershipResult(
                    WindowsJobMembershipStatus.NotMember,
                    0));
        }
    }

    private sealed class RecordingScopeCommitter
        : IHostManagerProcessEffectValidationScopeFileCommitter
    {
        internal int CommitCallCount { get; private set; }
        internal int? FailCommitCallNumber { get; set; }
        internal int? FailAfterCommitCallNumber { get; set; }
        internal Action<string>? FailAfterCommitMutation { get; set; }
        internal int DeleteCallCount { get; private set; }
        internal int? FailDeleteCallNumber { get; set; }

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            bool replaceExisting)
        {
            CommitCallCount++;
            if (FailCommitCallNumber == CommitCallCount)
            {
                FailCommitCallNumber = null;
                throw new IOException("injected commit failure");
            }
            File.Move(temporaryPath, canonicalPath, overwrite: replaceExisting);
            if (FailAfterCommitCallNumber == CommitCallCount)
            {
                FailAfterCommitCallNumber = null;
                var mutation = FailAfterCommitMutation;
                FailAfterCommitMutation = null;
                mutation?.Invoke(canonicalPath);
                throw new IOException("injected lost commit acknowledgment");
            }
        }

        public void DeleteExact(string canonicalPath)
        {
            DeleteCallCount++;
            if (FailDeleteCallNumber == DeleteCallCount)
            {
                FailDeleteCallNumber = null;
                throw new IOException("injected delete failure");
            }
            if (File.Exists(canonicalPath))
            {
                File.Delete(canonicalPath);
            }
        }
    }
}
