using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerPlacementRollbackExecutorTests
{
    [Theory]
    [InlineData("applied", HostManagerPlacementSettlementKind.Restored, 1)]
    [InlineData("previous", HostManagerPlacementSettlementKind.AlreadyRestored, 0)]
    [InlineData("foreign", HostManagerPlacementSettlementKind.OwnershipLost, 0)]
    [InlineData("reused-thread", HostManagerPlacementSettlementKind.OwnershipLost, 0)]
    [InlineData("reused-process", HostManagerPlacementSettlementKind.OwnershipLost, 0)]
    [InlineData("unavailable", HostManagerPlacementSettlementKind.RetryableFailure, 0)]
    [InlineData("exited", HostManagerPlacementSettlementKind.OwnershipLost, 0)]
    [InlineData("restricted", HostManagerPlacementSettlementKind.OwnershipLost, 0)]
    public void IdealProcessorRestoresOnlyItsExactThreadAndExpectedHint(string scenario, HostManagerPlacementSettlementKind kind, int writes)
    {
        var snapshot = new ThreadIdealProcessorPolicySnapshot(123, 456, 789, 890, new(0, 2), 0, 15, []);
        snapshot = scenario switch
        {
            "previous" => snapshot with { IdealProcessor = new(0, 0) },
            "foreign" => snapshot with { IdealProcessor = new(0, 3) },
            "reused-thread" => snapshot with { ThreadStartKey = 891 },
            "reused-process" => snapshot with { ProcessStartKey = 457 },
            "restricted" => snapshot with { AllowedMask = 4 },
            _ => snapshot
        };
        var writer = new FakeProcessPolicyWriter
        {
            IdealRead = scenario switch
            {
                "unavailable" => RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(5, "denied"),
                "exited" => RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(87, "gone"),
                _ => RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(snapshot)
            }
        };
        var settlement = CreateExecutor(process: writer).RestoreRecord(IdealRecord());
        Assert.Equal(kind, settlement.Kind);
        Assert.Equal(writes, writer.IdealWrites);
        Assert.Empty(writer.BatchRequests);
        if (writes != 0) Assert.Equal(new ThreadIdealProcessor(0, 0), writer.IdealRead.Value!.IdealProcessor);
    }

    [Fact]
    public void IdealProcessorUnconfirmedWriteRetainsItsReceipt()
    {
        var writer = new FakeProcessPolicyWriter { IgnoreIdealWrite = true,
            IdealRead = RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(new(123, 456, 789, 890, new(0, 2), 0, 15, [])) };
        var result = CreateExecutor(process: writer).RestoreRecord(IdealRecord());
        Assert.Equal(HostManagerPlacementSettlementKind.RetryableFailure, result.Kind);
        Assert.False(result.CanRemoveReceipt);
        Assert.Equal(1, writer.IdealWrites);
    }

    private static HostManagerAppliedRecord IdealRecord()
        => new(HostManagerAppliedRecordKinds.CpuThreadIdealProcessor, "ideal", new Dictionary<string, string>
        {
            ["processId"] = "123", ["processStartKey"] = "456", ["threadId"] = "789", ["threadStartKey"] = "890",
            ["previousProcessorGroup"] = "0", ["previousProcessorNumber"] = "0", ["appliedProcessorGroup"] = "0", ["appliedProcessorNumber"] = "2"
        });
}
