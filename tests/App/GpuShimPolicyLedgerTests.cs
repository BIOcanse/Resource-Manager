using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Optimization;
using PolicyFiles = Resource_Manager_APP.Tests.D3d11ProxyShimRuntimeTests.PolicyFiles;

namespace Resource_Manager_APP.Tests;

public sealed class GpuShimPolicyLedgerTests
{
    public static TheoryData<byte[]?, byte[], bool> RecoveryCases => new()
    {
        { null, [2, 4, 6], false }, { null, [2, 4, 6], true },
        { [], [2, 4, 6], false }, { [], [2, 4, 6], true },
        { [0xFF, 0, 0xEF, 0xBB, 0xBF, 13, 10], [2, 4, 6], false },
        { [0xFF, 0, 0xEF, 0xBB, 0xBF, 13, 10], [2, 4, 6], true },
        { null, [], false }, { null, [], true },
        { [1, 0, 255], [], false }, { [1, 0, 255], [], true }
    };

    [Theory]
    [MemberData(nameof(RecoveryCases))]
    public async Task ReopenedLedgerRestoresBeforeAndAfterPolicyWrite(byte[]? previous, byte[] applied, bool apply)
    {
        using var files = new PolicyFiles();
        const string target = "software:ledger-test";
        Assert.True(files.Runtime.TryWritePolicy(target, null, previous));
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord(target, applied));
        Assert.Equal(previous, files.Runtime.ReadPolicy(target));
        var path = Path.Combine(files.Root, "recovery.json");
        using (var store = OpenLedger(path))
            await store.SaveAsync(State(record), CancellationToken.None);

        // Simulate loss of all in-memory owners on either side of the external write.
        if (apply) Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        var reopenedRuntime = new D3d11ProxyShimRuntime(files);
        using (var reopened = OpenLedger(path))
        {
            var state = await reopened.LoadAsync(CancellationToken.None);
            var saved = Assert.Single(Assert.Single(state.AppliedPlacements).Records);
            Assert.True(GpuShimPolicyRecord.TryRead(saved, out var policy));
            Assert.Equal(previous, policy.PreviousValue);
            Assert.Equal(applied, policy.AppliedValue);
            var result = Executor(reopenedRuntime).RestoreRecord(saved);
            Assert.Equal(apply ? HostManagerPlacementSettlementKind.Restored
                : HostManagerPlacementSettlementKind.AlreadyRestored, result.Kind);
            Assert.True(result.CanRemoveReceipt);
            Assert.Equal(previous, reopenedRuntime.ReadPolicy(target));
            await reopened.SaveAsync(state with { AppliedPlacements = [] }, CancellationToken.None);
        }
        using var final = OpenLedger(path);
        Assert.Empty((await final.LoadAsync(CancellationToken.None)).AppliedPlacements);
    }

    [Fact]
    public void CaptureDoesNotCreateAFileAndEqualValueDoesNotClaimOwnership()
    {
        using var files = new PolicyFiles();
        byte[] value = [1, 2];
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", value));
        Assert.False(Directory.Exists(Path.GetDirectoryName(files.Runtime.GetPolicyPath("target"))));
        value[0] = 99;
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        Assert.Equal(new byte[] { 1, 2 }, files.Runtime.ReadPolicy("target"));
        Assert.Null(files.Runtime.CapturePolicyRecord("target", [1, 2]));
    }

    [Fact]
    public void ChangedValueBetweenCaptureAndApplyIsNotOverwritten()
    {
        using var files = new PolicyFiles();
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        Assert.True(files.Runtime.TryWritePolicy("target", null, [9]));
        Assert.False(files.Runtime.TryApplyPolicyRecord(record));
        var result = Executor(files.Runtime).RestoreRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, result.Kind);
        Assert.Equal(new byte[] { 9 }, files.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignValueOrDeletionAfterApplyIsNotOverwritten(bool delete)
    {
        using var files = new PolicyFiles();
        Assert.True(files.Runtime.TryWritePolicy("target", null, [1]));
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        byte[]? foreign = delete ? null : [9];
        Assert.True(files.Runtime.TryWritePolicy("target", [2], foreign));
        var result = Executor(files.Runtime).RestoreRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, result.Kind);
        Assert.True(result.CanRemoveReceipt);
        Assert.Equal(foreign, files.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData("hadValue", null)]
    [InlineData("hadValue", "True")]
    [InlineData("previousValue", null)]
    [InlineData("previousValue", "!")]
    [InlineData("appliedValue", null)]
    [InlineData("appliedValue", "!")]
    [InlineData("appliedValue", "AQ==")]
    public void InvalidReceiptRetainsEvidenceWithoutTouchingThePolicy(string field, string? replacement)
    {
        using var files = new PolicyFiles();
        Assert.True(files.Runtime.TryWritePolicy("target", null, [1]));
        var captured = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        var metadata = new Dictionary<string, string>(captured.Metadata!);
        if (replacement is null) metadata.Remove(field); else metadata[field] = replacement;
        var invalid = captured with { Metadata = metadata };
        Assert.False(GpuShimPolicyRecord.TryRead(invalid, out _));
        Assert.Throws<InvalidDataException>(() => files.Runtime.TryApplyPolicyRecord(invalid));
        var result = Executor(files.Runtime).RestoreRecord(invalid);
        Assert.Equal(HostManagerPlacementSettlementKind.InvalidReceipt, result.Kind);
        Assert.False(result.CanRemoveReceipt);
        Assert.Equal(new byte[] { 1 }, files.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public void FalseAbsenceWithPreviousBytesAndEmptyTargetAreRejected()
    {
        var valid = GpuShimPolicyRecord.Create("target", [1], [2]);
        var metadata = new Dictionary<string, string>(valid.Metadata!) { ["hadValue"] = "false" };
        Assert.False(GpuShimPolicyRecord.TryRead(valid with { Metadata = metadata }, out _));
        Assert.False(GpuShimPolicyRecord.TryRead(valid with { RecordId = " " }, out _));
        Assert.False(GpuShimPolicyRecord.TryRead(valid with { Kind = HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger }, out _));
    }

    [Fact]
    public async Task DurableStoreRejectsBlankRecordIdentityWithoutReplacingPriorLedger()
    {
        using var files = new PolicyFiles();
        var record = GpuShimPolicyRecord.Create("target", null, [2]);
        var ledger = Path.Combine(files.Root, "recovery.json");
        using (var store = OpenLedger(ledger))
        {
            await store.SaveAsync(State(record), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveAsync(State(record with { RecordId = " " }), CancellationToken.None));
        }
        using var reopened = OpenLedger(ledger);
        var saved = Assert.Single(Assert.Single((await reopened.LoadAsync(CancellationToken.None))
            .AppliedPlacements).Records);
        Assert.Equal(record.RecordId, saved.RecordId);
        Assert.Equal(record.Metadata, saved.Metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreWriteFailureKeepsTheDurableReceiptAndAppliedValue(bool originallyAbsent)
    {
        using var files = new PolicyFiles();
        byte[]? previous = originallyAbsent ? null : [1];
        Assert.True(files.Runtime.TryWritePolicy("target", null, previous));
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        var ledger = Path.Combine(files.Root, "recovery.json");
        using (var store = OpenLedger(ledger)) await store.SaveAsync(State(record), CancellationToken.None);
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        var policyPath = files.Runtime.GetPolicyPath("target");
        File.SetAttributes(policyPath, FileAttributes.ReadOnly);
        var result = Executor(new D3d11ProxyShimRuntime(files)).RestoreRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.RetryableFailure, result.Kind);
        Assert.False(result.CanRemoveReceipt);
        Assert.Equal(new byte[] { 2 }, files.Runtime.ReadPolicy("target"));
        using (var reopened = OpenLedger(ledger))
            Assert.Single((await reopened.LoadAsync(CancellationToken.None)).AppliedPlacements);
        Assert.Equal(originallyAbsent ? 0 : 1,
            Directory.EnumerateFiles(Path.GetDirectoryName(policyPath)!, "*.tmp").Count());
        File.SetAttributes(policyPath, FileAttributes.Normal);
        Assert.Equal(HostManagerPlacementSettlementKind.Restored,
            Executor(files.Runtime).RestoreRecord(record).Kind);
        Assert.Equal(previous, files.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public void ReadFailureIsRetainedInsteadOfBeingTreatedAsAnAbsentPolicy()
    {
        using var files = new PolicyFiles();
        var record = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        using var reader = new FileStream(files.Runtime.GetPolicyPath("target"), FileMode.Open,
            FileAccess.Read, FileShare.None);
        var result = Executor(files.Runtime).RestoreRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.RetryableFailure, result.Kind);
        Assert.False(result.CanRemoveReceipt);
        Assert.Throws<IOException>(() => files.Runtime.CapturePolicyRecord("target", [3]));
    }

    [Fact]
    public void ReceiptDoesNotUseAnArbitraryPathOrReinterpretAnOldTrigger()
    {
        using var files = new PolicyFiles();
        var other = Path.Combine(files.Root, "other.bin");
        File.WriteAllBytes(other, [9]);
        var captured = Assert.IsType<HostManagerAppliedRecord>(files.Runtime.CapturePolicyRecord("target", [2]));
        var record = captured with { Metadata = new Dictionary<string, string>(captured.Metadata!) { ["path"] = other } };
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        var oldTrigger = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger,
            "target", new Dictionary<string, string> { ["assignedPositionId"] = "gpu:0" });
        Assert.Equal(HostManagerPlacementSettlementKind.AlreadyRestored, Executor(files.Runtime).RestoreRecord(oldTrigger).Kind);
        Assert.Equal(new byte[] { 2 }, files.Runtime.ReadPolicy("target"));
        Assert.Equal(HostManagerPlacementSettlementKind.Restored, Executor(files.Runtime).RestoreRecord(record).Kind);
        Assert.Null(files.Runtime.ReadPolicy("target"));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(other));
    }

    private static HostManagerPlacementRollbackExecutor Executor(D3d11ProxyShimRuntime runtime)
        => new(null!, null!, runtime, NullLogger.Instance);

    private static JsonHostManagerRollbackStateStore OpenLedger(string path)
        => new(path, TimeProvider.System, TimeSpan.FromMinutes(1));

    private static HostManagerRollbackStateDocument State(HostManagerAppliedRecord record)
        => HostManagerRollbackStateDocument.Empty with
        {
            AppliedPlacements = [Placement(record)]
        };

    internal static HostManagerAppliedPlacementReceipt Placement(HostManagerAppliedRecord record)
        => new("process-target", "GPU policy test", "software", OptimizationResourceKinds.Gpu,
            [record], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
