using System.Runtime.InteropServices;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceManagerComputeScratchTests
{
    [Theory]
    [InlineData(1u, 1u, 5u, 0)]
    [InlineData(4096u, 32768u, 77824u, 64)]
    [InlineData(32768u, 262144u, 622592u, 134217728)]
    public void LayoutUsesActualRowsAndCheckedAlignedFullExtents(uint p, uint g, uint output, int coreRows)
    {
        var adapters = checked((int)Math.Min(2U, g));
        var layout = new ComputeScoringScratchLayout(p, g, output, coreRows, 0, 1, adapters);
        var processBytes = (ulong)Marshal.SizeOf<NativeComputeScoringProcessInput>();
        var gpuBytes = (ulong)Marshal.SizeOf<NativeComputeScoringGpuInput>();
        var coreBytes = (ulong)Marshal.SizeOf<NativeComputeScoringCpuCoreInput>();
        var outputBytes = (ulong)Marshal.SizeOf<NativeComputeScoringOutput>();
        var body = checked(processBytes * p + 8UL * p + coreBytes * (ulong)coreRows
            + gpuBytes * g + outputBytes * output + 4UL * (ulong)adapters);
        Assert.Equal(checked((body + 7) / 8 * 8), layout.Length);
        Assert.Equal(0UL, layout.CoreInputs % 8);
        Assert.Equal(0UL, layout.Outputs % 8);
        if (coreRows == 134217728)
        {
            Assert.True((ulong)layout.CoreInputCapacity * 16 > int.MaxValue);
            Assert.Equal(134217728, layout.CoreInputCapacity); // No allocation or new byte-count cap.
        }
        Assert.Equal(88, Marshal.SizeOf<NativeComputeScoringProcessInput>());
        Assert.Equal(88, Marshal.SizeOf<NativeComputeScoringGpuInput>());
        Assert.Equal(16, Marshal.SizeOf<NativeComputeScoringCpuCoreInput>());
        Assert.Equal(96, Marshal.SizeOf<NativeComputeScoringOutput>());
    }

    [Fact]
    public void LayoutRejectsOverflowingElementCounts()
    {
        Assert.Throws<OverflowException>(() => new ComputeScoringScratchLayout(uint.MaxValue, 1, 1, 0, 0, 1));
        Assert.Throws<OverflowException>(() => new ComputeScoringScratchLayout(1, uint.MaxValue, 1, 0, 0, 1));
        Assert.Throws<OverflowException>(() => new ComputeScoringScratchLayout(1, 1, uint.MaxValue, 0, 0, 1));
    }

    [Fact]
    public void RealPinReadsAndWritesAllColumnsAndRejectsTheirFullOutOfBoundsExtent()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var layout = new ComputeScoringScratchLayout(8, 16, 56, 8, 0, 1, 16);
        using var resource = owner.CreateScratch(layout.Length);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using (var lease = resource.Acquire())
        {
            using (var view = new ComputeScratchView(lease))
            {
                view.Column<NativeComputeScoringProcessInput>(layout.Processes, layout.ProcessCapacity)[7] = new() { ProcessId = 42 };
                view.Column<NativeComputeScoringGpuInput>(layout.Gpus, layout.GpuCapacity)[15] = new() { AdapterKey = 11 };
                view.Column<NativeComputeScoringCpuCoreInput>(layout.CoreInputs, layout.CoreInputCapacity)[7] = new() { CoreIndex = 7 };
                view.Column<NativeComputeScoringOutput>(layout.Outputs, layout.OutputCapacity)[55] = new() { Score = 123 };
                Assert.Equal(42U, view.Column<NativeComputeScoringProcessInput>(layout.Processes, layout.ProcessCapacity)[7].ProcessId);
                Assert.Equal(11UL, view.Column<NativeComputeScoringGpuInput>(layout.Gpus, layout.GpuCapacity)[15].AdapterKey);
                Assert.Equal(7U, view.Column<NativeComputeScoringCpuCoreInput>(layout.CoreInputs, layout.CoreInputCapacity)[7].CoreIndex);
                Assert.Equal(123D, view.Column<NativeComputeScoringOutput>(layout.Outputs, layout.OutputCapacity)[55].Score);
                view.Column<int>(layout.AdapterOrder, layout.AdapterCapacity).Fill(37);
                view.Column<double>(layout.WeightedCpuUse, layout.ProcessCapacity).Fill(12.5);
                Assert.Equal(37, view.Column<int>(layout.AdapterOrder, layout.AdapterCapacity)[15]);
                Assert.Equal(12.5, view.Column<double>(layout.WeightedCpuUse, layout.ProcessCapacity)[7]);
                Assert.Equal(0UL, owner.Tick().Released);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => ReadOutside(lease, layout.Length));
            lease.Complete();
        }
        using var next = resource.Acquire();
        using var nextView = new ComputeScratchView(next);
        Assert.Equal(37, nextView.Column<int>(layout.AdapterOrder, layout.AdapterCapacity)[15]);
    }

    private static void ReadOutside(ComputeScratchLease lease, ulong length)
    {
        using var view = new ComputeScratchView(lease);
        _ = view.Column<long>(length, 1);
    }

    [Fact]
    public void OwnerCloseWaitsForActiveUseAndRejectsNewBorrowsWhileRetiring()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var resource = owner.CreateScratch(8192);
        var reading = resource.Acquire();
        reading.Complete();
        Assert.Equal(LedgerStatus.InUse, owner.TryClose());
        Assert.Throws<ObjectDisposedException>(() => resource.Acquire());
        Assert.Throws<InvalidOperationException>(() => owner.CreateScratch(8192));
        Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes);
        reading.Dispose();
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
    }

    [Fact]
    public void UnpublishedFailureIsDiscardedAndTheSameIdentityCanBeLoadedAgain()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var resource = owner.CreateScratch(8192);
        var uid = resource.Uid;
        using (resource.Acquire()) { } // Failed operation: no Complete.
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using (var retry = resource.Acquire()) retry.Complete();
        Assert.Equal(uid, resource.Uid);
        Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void IdleReleaseUsesTheSameUidAndANewAllocationWithoutShadowBacking()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var resource = owner.CreateScratch(8192);
        var uid = resource.Uid;
        ulong allocation;
        using (var first = resource.Acquire())
        {
            allocation = first.AllocationNumber;
            first.Complete();
        }
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using var next = resource.Acquire();
        Assert.Equal(uid, resource.Uid);
        Assert.NotEqual(allocation, next.AllocationNumber);
        next.Complete();
    }

    [Fact]
    public void LazyReplacementUsesOneSlotAndSmallScratchStaysOrdinary()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var old = owner.CreateScratch(8192);
        using (var lease = old.Acquire()) lease.Complete();
        using var replacement = owner.CreateScratch(16384);
        Assert.Equal(8192UL, owner.ReadTable().RegisteredBytes); // New handle has not Push-ed.
        old.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using (var lease = replacement.Acquire()) lease.Complete();
        Assert.Equal(16384UL, owner.ReadTable().RegisteredBytes);
        using var tiny = owner.CreateScratch(128);
        using (var lease = tiny.Acquire())
        {
            Assert.Equal(0UL, lease.AllocationNumber);
            lease.Complete();
        }
        Assert.Equal(16384UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void AllEightTreeSlotsRemainUsableAlongsideTheIndependentScratchTable()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var payloads = new List<IDiskUsageTreePayload>();
        var readers = new List<IDiskUsageTreePayloadReadLease>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var builder = new DiskUsageTreeBuilder();
                var root = builder.Add(-1, "C:\\", true, 0, 0);
                for (var node = 0; node < 80; node++) builder.Add(root, $"tree-{index}-node-{node}", false, 1, 1);
                var payload = owner.Import(builder.Build());
                payloads.Add(payload);
                readers.Add(Assert.IsAssignableFrom<IDiskUsageTreePayloadReadLease>(payload.TryAcquire()));
            }
            var trees = owner.ReadTable().RegisteredBytes;
            using var scratch = owner.CreateScratch(8192);
            using (var reading = scratch.Acquire())
            {
                reading.Complete();
                Assert.Equal(trees + 8192, owner.ReadTable().RegisteredBytes);
                // Existing eight trees and scoring use retain all original slots.
                // Two operation identities must ALSO fit in the same real declaration.
                using (var oldOperation = owner.CreateScratch(8192))
                using (var candidateOperation = owner.CreateScratch(16384))
                using (var oldUse = oldOperation.Acquire())
                using (var candidateUse = candidateOperation.Acquire())
                {
                    oldUse.Complete(); candidateUse.Complete();
                    Assert.Equal(trees + 8192 + 8192 + 16384, owner.ReadTable().RegisteredBytes);
                    using (var metric = owner.CreateScratch(32768))
                    using (var metricUse = metric.Acquire())
                    {
                        metricUse.Complete();
                        Assert.Equal(trees + 8192 + 8192 + 16384 + 32768, owner.ReadTable().RegisteredBytes);
                        using var oldReport = owner.CreateScratch(40960);
                        using var candidateReport = owner.CreateScratch(49152);
                        using var oldReportUse = oldReport.Acquire();
                        using var candidateReportUse = candidateReport.Acquire();
                        oldReportUse.Complete(); candidateReportUse.Complete();
                        var registered = trees + 8192 + 8192 + 16384 + 32768 + 40960 + 49152;
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        Assert.All(readers, reader => Assert.Equal(81, reader.Tree.Count));
                        var oldSamplingBytes = SamplingOutputScratchTests.RealLayout(64, 2, 128).StorageLength;
                        var candidateSamplingBytes = SamplingOutputScratchTests.RealLayout(128, 2, 256).StorageLength;
                        using var oldSampling = owner.CreateScratch(oldSamplingBytes);
                        using var candidateSampling = owner.CreateScratch(candidateSamplingBytes);
                        using var oldSamplingUse = oldSampling.Acquire();
                        using var candidateSamplingUse = candidateSampling.Acquire();
                        oldSamplingUse.Complete(); candidateSamplingUse.Complete();
                        registered += oldSamplingBytes + candidateSamplingBytes;
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        var smartPlan = HostManagerTestPlanFactory.CreatePlan().SmartCoordinator;
                        var smartConfiguration = NativeSmartCoordinatorConfigurationWriter.Create(
                            smartPlan.Build, smartPlan.Recreate, smartPlan.HotPublish, smartPlan.ConfigurationGeneration);
                        using var smart = new NativeSmartCoordinatorWorkspace(in smartConfiguration, owner.CreateScratch);
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes); // Identity-only construction.
                        using var smartOperation = smart.BeginOperation();
                        smart.SetFeedback(0, new NativeSmartCoordinatorFeedback { ActionId = 61 });
                        Assert.Equal(61UL, smart.GetFeedback(0).ActionId);
                        var smartLayout = smart.ScratchLayout;
                        Assert.True(smartLayout.Length < owner.NativePageSize);
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        Assert.All(readers, reader => Assert.Equal(81, reader.Tree.Count));
                        var ownershipCapacity = new NativeAppliedOwnershipCapacity
                        {
                            RecordCapacity = 16, RecordSize = NativeAppliedOwnershipAbi.RecordSize,
                            MaximumImageBytes = 512, ResidentBytes = 8192
                        };
                        using var currentOwnership = new NativeAppliedOwnershipWorkspace(in ownershipCapacity, owner.CreateScratch);
                        using var candidateOwnership = new NativeAppliedOwnershipWorkspace(in ownershipCapacity, owner.CreateScratch);
                        using var currentOwnershipUse = currentOwnership.Acquire();
                        using var candidateOwnershipUse = candidateOwnership.Acquire();
                        currentOwnershipUse.Image.Span[0] = 11;
                        candidateOwnershipUse.Image.Span[0] = 22;
                        currentOwnershipUse.Complete(); candidateOwnershipUse.Complete();
                        Assert.Equal(128UL, currentOwnership.Length);
                        Assert.Equal(128UL, candidateOwnership.Length); // Header-only scratch stays below a page.
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        Assert.Equal(11, currentOwnershipUse.Image.Span[0]);
                        Assert.Equal(22, candidateOwnershipUse.Image.Span[0]);
                        var journalCapacity = new NativeTransactionJournalCapacity
                        {
                            RecordCapacity = 16,
                            MaximumImageLength = NativeTransactionJournalAbi.ImageHeaderSize +
                                16UL * NativeTransactionJournalSession.SizeOf<NativeTransactionJournalRecord>(),
                            ResidentBytes = 16384
                        };
                        using var currentJournal = new NativeTransactionJournalWorkspace(in journalCapacity, owner.CreateScratch);
                        using var candidateJournal = new NativeTransactionJournalWorkspace(in journalCapacity, owner.CreateScratch);
                        using var currentJournalUse = currentJournal.Acquire();
                        using var candidateJournalUse = candidateJournal.Acquire();
                        currentJournalUse.Image.Span[0] = 31;
                        candidateJournalUse.Image.Span[0] = 41;
                        currentJournalUse.Complete(); candidateJournalUse.Complete();
                        Assert.Equal(128UL, currentJournal.Length);
                        Assert.Equal(128UL, candidateJournal.Length); // Header-only scratch stays below a page.
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        Assert.Equal(31, currentJournalUse.Image.Span[0]);
                        Assert.Equal(41, candidateJournalUse.Image.Span[0]);
                        using var remainingRoleOutputs = new SamplingOutputCapacityBatch();
                        registered += SamplingOutputCapacityBatch.FillRemainingRoleOutputs(owner, remainingRoleOutputs);
                        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                        ResourceManagerLedgerOwner.ComputeScratch? pdhIdentity = null;
                        using var pdh = new PdhParsingScratch(length => pdhIdentity = owner.CreateScratch(length), owner.NativePageSize);
                        var additionalResources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
                        var additionalUses = new List<ComputeScratchLease>();
                        pdh.BeginSample();
                        try
                        {
                            pdh.Supply(8192);
                            registered += pdhIdentity!.Length;
                            Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                            // The 23 registered workspaces above (four persistence headers and Smart are compact), then query pools (2 * 64),
                            // model (2), portable registry per-call outputs (2), display normalization (2), placement (2), and two
                            // sampling per-operation borrows. Sampling sessions and portable registries hold no scratch between operations.
                            for (var index = 0; index < 143; index++)
                            {
                                var resource = owner.CreateScratch(8192);
                                additionalResources.Add(resource);
                                var use = resource.Acquire();
                                additionalUses.Add(use);
                                use.Complete();
                                registered += resource.Length;
                            }
                            using var excess = owner.CreateScratch(8192);
                            var blocked = Assert.Throws<LedgerException>(() => { using var unexpected = excess.Acquire(); });
                            Assert.Equal(LedgerStatus.Blocked, blocked.Status);
                            Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
                            Assert.All(readers, reader => Assert.Equal(81, reader.Tree.Count));
                            Assert.Equal(0UL, owner.Tick().Released);
                            smartOperation.Complete();
                        }
                        finally
                        {
                            foreach (var use in additionalUses) use.Dispose();
                            foreach (var resource in additionalResources) resource.Dispose();
                            pdh.EndSample();
                        }
                    }
                }
                owner.SetMode(LedgerTableMode.Optimize);
                Assert.Equal(0UL, owner.Tick().Released);
            }
            Assert.Equal(1UL, owner.Tick().Released);
            Assert.Equal(trees, owner.ReadTable().RegisteredBytes);
            Assert.All(readers, reader => Assert.Equal(81, reader.Tree.Count));
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            foreach (var payload in payloads) payload.Retire();
        }
    }
    [Fact]
    public void NativeExtentKeepsFullUlongBytesAndAlignmentSlackWithoutAllocation()
    {
        var managed = new ComputeScoringScratchLayout(8, 16, 56, 8, 0, 1, 2);
        var native = new ComputeScoringScratchLayout(8, 16, 56, 8, (ulong)int.MaxValue + 100, 64, 2);
        Assert.Equal(managed.NativeScratch, native.NativeScratch);
        Assert.Equal((ulong)int.MaxValue + 100, native.NativeScratchByteCount);
        Assert.Equal(64U, native.NativeScratchAlignment);
        Assert.Equal((native.NativeScratch + 63 + native.NativeScratchByteCount + 7) / 8 * 8, native.Length);
        Assert.Throws<OverflowException>(() => new ComputeScoringScratchLayout(8, 16, 56, 1, ulong.MaxValue, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComputeScoringScratchLayout(8, 16, 56, 1, 8, 3));
    }

    [Fact]
    public void RealAlignedRegionChecksAbsoluteAddressAndWholeExtentWithinLease()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var resource = owner.CreateScratch(8192);
        using var lease = resource.Acquire();
        using (var view = new ComputeScratchView(lease))
        {
            var pointer = view.AlignedRegion(1, 31, 32);
            Assert.Equal(0L, pointer.ToInt64() % 32);
            System.Runtime.InteropServices.Marshal.WriteByte(pointer, 30, 0x5A);
            Assert.Equal((byte)0x5A, System.Runtime.InteropServices.Marshal.ReadByte(pointer, 30));
            Assert.Equal(0UL, owner.Tick().Released);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadAlignedOutside(lease, 8192));
        lease.Complete();
    }

    private static void ReadAlignedOutside(ComputeScratchLease lease, ulong length)
    {
        using var view = new ComputeScratchView(lease);
        _ = view.AlignedRegion(1, length, 32);
    }
}
