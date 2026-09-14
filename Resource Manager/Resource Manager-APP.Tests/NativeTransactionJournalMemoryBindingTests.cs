using ResourceManager.App.Infrastructure.NativeCore;
using Xunit;

namespace ResourceManager.App.Tests;

public sealed class NativeTransactionJournalMemoryBindingTests
{
    [Theory]
    [InlineData(1U, 0, 3)]
    [InlineData(1U, 3, 1)]
    [InlineData(2U, 3, 0)]
    public void ProcessMemoryBinding_UsesAnIndependentTaggedPriorityState(
        uint disposition,
        int fromPriority,
        int toPriority)
    {
        var binding = CreateMemoryBinding(
            (NativeTransactionJournalDisposition)disposition,
            fromPriority,
            toPriority);

        Assert.True(binding.IsValid);
    }

    [Theory]
    [InlineData(1U, -1, 3)]
    [InlineData(1U, 0, 0)]
    [InlineData(1U, 0, 6)]
    [InlineData(2U, 0, 0)]
    [InlineData(2U, 3, 2)]
    public void ProcessMemoryBinding_RejectsInvalidPriorityTransitions(
        uint disposition,
        int fromPriority,
        int toPriority)
    {
        var binding = CreateMemoryBinding(
            (NativeTransactionJournalDisposition)disposition,
            fromPriority,
            toPriority);

        Assert.False(binding.IsValid);
    }

    [Fact]
    public void ProcessMemoryBinding_RejectsCpuTagOrCpuGradeContamination()
    {
        var canonical = CreateMemoryBinding(NativeTransactionJournalDisposition.Apply, 0, 3);

        Assert.False((canonical with
        {
            GradeValidMask = NativeTransactionJournalGradeValidity.Process
        }).IsValid);
        Assert.False((canonical with
        {
            Domain = NativeTransactionJournalDomain.Process
        }).IsValid);
        Assert.False((canonical with { CpuFromGrade = 1 }).IsValid);
        Assert.False((canonical with { GpuToGrade = 1 }).IsValid);
    }

    private static NativeTransactionJournalPayloadBinding CreateMemoryBinding(
        NativeTransactionJournalDisposition disposition,
        int fromPriority,
        int toPriority)
        => new(
            JournalInstanceLow: 1,
            JournalInstanceHigh: 2,
            ActionIdentity: new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = 3,
                PlanEpoch = 4,
                ActionId = 5,
                HostSessionIncarnation = 6,
                TargetId = 7,
                SoftwareId = 8,
                ProcessStartKey = 9,
                ProcessId = 10
            },
            NativeTransactionJournalScope.Process,
            disposition,
            NativeTransactionJournalDomain.PhysicalMemory,
            NativeTransactionJournalGradeValidity.Memory,
            ProcessFromGrade: fromPriority,
            ProcessToGrade: toPriority,
            CpuFromGrade: 0,
            CpuToGrade: 0,
            GpuFromGrade: 0,
            GpuToGrade: 0,
            StableSystemStatus: 0,
            StableSystemError: 0,
            MaximumRecoveryAttempts: 3,
            RecoveryDeadlineUtcMilliseconds: 100,
            AtomicGroupId: 0,
            GroupMemberIndex: 0,
            GroupMemberCount: 0);
}
