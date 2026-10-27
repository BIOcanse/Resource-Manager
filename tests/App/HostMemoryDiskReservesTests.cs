using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.MemoryProtection;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryDiskReservesTests(ITestOutputHelper output)
{
    [Fact]
    public void FailedEntryRetirementRemainsVisibleAndDisablesTheWholeReserveUntilRetried()
    {
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 4);
        var second = directories.Setting(1, 4);
        Assert.True(reserve.Prepare([first, second]).Built);
        var path = directories.ReserveFile(0);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            var failed = reserve.Prepare([second]);
            Assert.False(failed.Built);
            Assert.NotNull(failed.Reason);
            Assert.Equal(first.SizeBytes + second.SizeBytes, failed.RemainingBytes);
            var pending = Assert.Single(failed.Entries, entry => entry.Location == first.Location);
            Assert.False(pending.Built);
            Assert.Equal(0UL, pending.ConfiguredSizeBytes);
            Assert.Equal(first.SizeBytes, pending.RemainingBytes);
            Assert.Null(reserve.TryAcquire());
        }
        finally { File.SetAttributes(path, attributes); }
        var retried = reserve.Rebuild();
        Assert.True(retried.Built, retried.Reason);
        Assert.Equal(second.Location, Assert.Single(retried.Entries).Location);
        Assert.False(File.Exists(path));
        Assert.Equal(second.SizeBytes, retried.RemainingBytes);
    }

    [Fact]
    public void OrderedBudgetsUseActualClustersAndConcatenatedPartsAreTheCompleteLogicalImage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 2);
        var second = directories.Setting(1, 3);
        var initial = reserve.Prepare([first, second]);
        Assert.True(initial.Built, initial.Reason);
        Assert.Equal(first.SizeBytes + second.SizeBytes, initial.ConfiguredSizeBytes);
        Assert.Equal(initial.ConfiguredSizeBytes, initial.RemainingBytes);
        Assert.Equal(directories.First, initial.Entries[0].Location);
        Assert.Equal(directories.Second, initial.Entries[1].Location);
        Assert.Equal(directories.Clusters, initial.Entries.Select(entry => entry.ClusterBytes));

        using var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        Assert.True(lease.CanFit(0));
        Assert.True(lease.CanFit(first.SizeBytes));
        Assert.True(lease.CanFit(first.SizeBytes + 1));
        Assert.True(lease.CanFit(initial.RemainingBytes - directories.Clusters[1] + 1));
        Assert.True(lease.CanFit(initial.RemainingBytes));
        Assert.False(lease.CanFit(initial.RemainingBytes + 1));
        Assert.False(lease.CanFit(ulong.MaxValue));
        using var slice = lease.CreateSlice("ordered.dmp");
        var image = Image(checked((int)(first.SizeBytes + 1)));
        slice.Write(image);
        Assert.Equal(image.Length, slice.Length);
        Assert.Equal(image.Length, slice.Position);
        Assert.Equal(0UL, reserve.ReadStatus().Entries[0].RemainingBytes);
        Assert.Equal(second.SizeBytes - directories.Clusters[1], reserve.ReadStatus().Entries[1].RemainingBytes);
        AssertFundedAllocation(reserve, directories, initial.RemainingBytes, lease.SlicePaths);
        slice.Position = 0;
        var logical = new byte[image.Length];
        slice.ReadExactly(logical);
        Assert.Equal(image, logical);
        slice.Dispose();

        var paths = lease.SlicePaths;
        Assert.Equal(new[] { System.IO.Path.Combine(directories.First, "ordered.dmp"),
            System.IO.Path.Combine(directories.Second, "ordered.dmp.part0002") }, paths);
        Assert.Equal((long)first.SizeBytes, new FileInfo(paths[0]).Length);
        Assert.Equal(1, new FileInfo(paths[1]).Length);
        Assert.Equal(image, Concatenate(paths));
        Assert.Equal(lease.RemainingBytes, reserve.ReadStatus().RemainingBytes);
    }

    [Fact]
    public void SeekIntoLaterEntryFundsThePrefixAndCrossBoundaryRewritesNeverChargeTwice()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 2);
        var second = directories.Setting(1, 4);
        var initial = reserve.Prepare([first, second]);
        Assert.True(initial.Built, initial.Reason);
        using var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        using var slice = lease.CreateSlice("seek.dmp");
        var lastOffset = checked((long)(first.SizeBytes + directories.Clusters[1] + 17));
        slice.Seek(lastOffset, SeekOrigin.Begin);
        slice.WriteByte(91);
        var afterGrowth = reserve.ReadStatus();
        Assert.Equal(0UL, afterGrowth.Entries[0].RemainingBytes);
        Assert.Equal(second.SizeBytes - directories.Clusters[1] * 2, afterGrowth.Entries[1].RemainingBytes);
        var expected = new byte[checked((int)lastOffset + 1)];
        expected[(int)lastOffset] = 91;
        slice.Position = 0;
        slice.Write("MDMP"u8);
        "MDMP"u8.CopyTo(expected);
        byte[] boundaryPatch = [11, 22, 33, 44, 55, 66];
        slice.Seek((long)first.SizeBytes - 2, SeekOrigin.Begin);
        slice.Write(boundaryPatch);
        boundaryPatch.CopyTo(expected, (int)first.SizeBytes - 2);
        Assert.Equal(afterGrowth.RemainingBytes, reserve.ReadStatus().RemainingBytes);
        AssertFundedAllocation(reserve, directories, initial.RemainingBytes, lease.SlicePaths);
        slice.Seek(-expected.Length, SeekOrigin.End);
        var logical = new byte[expected.Length];
        slice.ReadExactly(logical);
        Assert.Equal(expected, logical);

        // Shortening a dump does not return paid clusters to the reserve. Regrowth to the
        // previous end consumes no second allocation budget on either entry.
        slice.SetLength(4);
        slice.SetLength(expected.Length);
        Assert.Equal(afterGrowth.RemainingBytes, reserve.ReadStatus().RemainingBytes);
        AssertFundedAllocation(reserve, directories, initial.RemainingBytes, lease.SlicePaths);
        Array.Clear(expected, 4, expected.Length - 4);
        slice.Position = 0;
        slice.ReadExactly(logical);
        Assert.Equal(expected, logical);
        slice.Dispose();
        Assert.Equal(expected, Concatenate(lease.SlicePaths));
    }

    [Fact]
    public void OneFailedEntryMakesTheWholeReserveUnavailableAndPreservesItsFailureReason()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 2);
        var invalid = directories.Setting(1, 2) with { SizeBytes = directories.Clusters[1] * 2 + 1 };
        var status = reserve.Prepare([first, invalid]);
        Assert.False(status.Built);
        Assert.True(status.Entries[0].Built, status.Entries[0].Reason);
        Assert.False(status.Entries[1].Built);
        Assert.Equal(status.Entries[1].Reason, status.Reason);
        Assert.Contains($"cluster-bytes={directories.Clusters[1]}", status.Reason!);
        Assert.Equal(first.SizeBytes + invalid.SizeBytes, status.ConfiguredSizeBytes);
        Assert.Equal(first.SizeBytes, status.RemainingBytes);
        Assert.Equal(first.SizeBytes, Allocation(directories.ReserveFile(0)));
        Assert.Empty(Directory.EnumerateFiles(directories.Second, "*.bin"));
        Assert.Null(reserve.TryAcquire());
        Assert.False(reserve.Rebuild().Built);
        Assert.Null(reserve.TryAcquire());
    }

    [Fact]
    public void UnchangedPrepareReorderReopenAndSliceRemovalPreserveConsumedSpaceUntilExplicitRebuild()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        var first = directories.Setting(0, 2);
        var second = directories.Setting(1, 3);
        ulong consumedRemaining;
        string[] reserveFiles;
        using (var reserve = new HostMemoryDiskReserves())
        {
            Assert.True(reserve.Prepare([first, second]).Built);
            reserveFiles = [directories.ReserveFile(0), directories.ReserveFile(1)];
            using (var lease = reserve.TryAcquire())
            {
                Assert.NotNull(lease);
                using var slice = lease.CreateSlice("persist.dmp");
                slice.Write(Image((int)first.SizeBytes + 1));
            }
            consumedRemaining = second.SizeBytes - directories.Clusters[1];
            var unchanged = reserve.Prepare([first, second]);
            Assert.Equal(consumedRemaining, unchanged.RemainingBytes);
            Assert.Equal(0UL, unchanged.Entries[0].RemainingBytes);
            Assert.Equal(consumedRemaining, unchanged.Entries[1].RemainingBytes);
            var reordered = reserve.Prepare([second, first]);
            Assert.True(reordered.Built, reordered.Reason);
            Assert.Equal(directories.Second, reordered.Entries[0].Location);
            Assert.Equal(directories.First, reordered.Entries[1].Location);
            Assert.Equal(consumedRemaining, reordered.Entries[0].RemainingBytes);
            Assert.Equal(0UL, reordered.Entries[1].RemainingBytes);
            Assert.Equal(consumedRemaining, reordered.RemainingBytes);
            Assert.Equal(reserveFiles[0], directories.ReserveFile(0));
            Assert.Equal(reserveFiles[1], directories.ReserveFile(1));
        }
        File.Delete(System.IO.Path.Combine(directories.First, "persist.dmp"));
        File.Delete(System.IO.Path.Combine(directories.Second, "persist.dmp.part0002"));
        using (var reopened = new HostMemoryDiskReserves())
        {
            var status = reopened.Prepare([second, first]);
            Assert.True(status.Built, status.Reason);
            Assert.Equal(consumedRemaining, status.RemainingBytes);
            Assert.Equal(consumedRemaining, reopened.Prepare([second, first]).RemainingBytes);
            Assert.Equal(0UL, Allocation(directories.ReserveFile(0)));
            Assert.Equal(consumedRemaining, Allocation(directories.ReserveFile(1)));
            var rebuilt = reopened.Rebuild();
            Assert.True(rebuilt.Built, rebuilt.Reason);
            Assert.Equal(first.SizeBytes + second.SizeBytes, rebuilt.RemainingBytes);
            Assert.Equal(first.SizeBytes, Allocation(directories.ReserveFile(0)));
            Assert.Equal(second.SizeBytes, Allocation(directories.ReserveFile(1)));
        }
    }

    [Fact]
    public void RemovingAnEntryRetiresOnlyItsReserveAndLeavesEverySliceInPlace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 2);
        var second = directories.Setting(1, 3);
        Assert.True(reserve.Prepare([first, second]).Built);
        var retiredReserve = directories.ReserveFile(0);
        var retainedReserve = directories.ReserveFile(1);
        var image = Image((int)first.SizeBytes + 1);
        string[] oldSlices;
        using (var lease = reserve.TryAcquire())
        {
            Assert.NotNull(lease);
            using var slice = lease.CreateSlice("retired.dmp");
            slice.Write(image);
            oldSlices = lease.SlicePaths.ToArray();
        }
        var changed = reserve.Prepare([second]);
        Assert.True(changed.Built, changed.Reason);
        Assert.Equal(directories.Second, Assert.Single(changed.Entries).Location);
        Assert.Equal(second.SizeBytes, changed.ConfiguredSizeBytes);
        Assert.Equal(second.SizeBytes - directories.Clusters[1], changed.RemainingBytes);
        Assert.False(File.Exists(retiredReserve));
        Assert.False(File.Exists(System.IO.Path.Combine(directories.First, ".rm-host-memory-reserve.json")));
        Assert.Equal(retainedReserve, directories.ReserveFile(1));
        Assert.Equal(image, Concatenate(oldSlices));
        using var next = reserve.TryAcquire();
        Assert.NotNull(next);
        using var nextSlice = next.CreateSlice("second.dmp");
        nextSlice.Write("MDMP"u8);
        Assert.Equal(System.IO.Path.Combine(directories.Second, "second.dmp"), Assert.Single(next.SlicePaths));
        Assert.Equal(second.SizeBytes - directories.Clusters[1] * 2, next.RemainingBytes);
    }

    [Fact]
    public void ExhaustedFirstEntryIsSkippedAndTheRemainingEntryStillWritesACompleteImage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directories = new OwnedDirectories(output);
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 1);
        var second = directories.Setting(1, 2);
        Assert.True(reserve.Prepare([first, second]).Built);
        using (var lease = reserve.TryAcquire())
        {
            Assert.NotNull(lease);
            using var initialSlice = lease.CreateSlice("ordered.dmp");
            initialSlice.SetLength((long)first.SizeBytes);
        }
        using var next = reserve.TryAcquire();
        Assert.NotNull(next);
        Assert.True(next.CanFit(second.SizeBytes));
        Assert.False(next.CanFit(second.SizeBytes + 1));
        using var slice = next.CreateSlice("empty-first.dmp");
        var image = Image((int)directories.Clusters[1] + 1);
        slice.Write(image);
        slice.Dispose();
        Assert.Equal(System.IO.Path.Combine(directories.Second, "empty-first.dmp.part0002"), Assert.Single(next.SlicePaths));
        Assert.False(File.Exists(System.IO.Path.Combine(directories.First, "empty-first.dmp")));
        Assert.Equal(image, Concatenate(next.SlicePaths));
        Assert.Equal(0UL, next.RemainingBytes);
        Assert.Equal(0UL, reserve.ReadStatus().RemainingBytes);
    }

    [Fact]
    public void ConfiguredReserveTestRootProvidesActualDistinctVolumeCoverage()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(SecondTestRoot()))
        {
            output.WriteLine("Neither RM_MEMORY_RESERVE_TEST_ROOT nor RM_REVIEW_SCRATCH is configured. Other facts verify logical two-entry behavior; this run does not claim physical cross-volume coverage.");
            return;
        }
        using var directories = new OwnedDirectories(output);
        Assert.False(string.Equals(VolumeRoot(directories.First), VolumeRoot(directories.Second), StringComparison.OrdinalIgnoreCase),
            "The configured reserve test root and Path.GetTempPath must identify different volume roots for the cross-volume validation gate.");
        using var reserve = new HostMemoryDiskReserves();
        var first = directories.Setting(0, 1);
        var second = directories.Setting(1, 1);
        var status = reserve.Prepare([first, second]);
        Assert.True(status.Built, status.Reason);
        using var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        using var slice = lease.CreateSlice("cross.dmp");
        var image = Image((int)first.SizeBytes + 1);
        slice.Write(image);
        slice.Dispose();
        Assert.Equal(2, lease.SlicePaths.Count);
        Assert.Equal(image, Concatenate(lease.SlicePaths));
        AssertFundedAllocation(reserve, directories, status.RemainingBytes, lease.SlicePaths);
    }

    private static void AssertFundedAllocation(HostMemoryDiskReserves reserve, OwnedDirectories directories,
        ulong original, IReadOnlyList<string> slices)
    {
        var status = reserve.ReadStatus();
        Assert.Equal(status.Entries.Aggregate(0UL, (total, entry) => total + entry.RemainingBytes), status.RemainingBytes);
        var allocatedSlices = slices.Aggregate(0UL, (total, path) => total + Allocation(path));
        Assert.Equal(original, allocatedSlices + status.RemainingBytes);
        Assert.Equal(status.RemainingBytes, Allocation(directories.ReserveFile(0)) + Allocation(directories.ReserveFile(1)));
    }

    private static byte[] Image(int size)
    {
        var image = new byte[size];
        for (var index = 0; index < image.Length; index++) image[index] = (byte)((index * 31 + 7) % 251);
        "MDMP"u8.CopyTo(image);
        return image;
    }

    private static byte[] Concatenate(IReadOnlyList<string> paths) => paths.SelectMany(File.ReadAllBytes).ToArray();

    private sealed class OwnedDirectories : IDisposable
    {
        public string First { get; }
        public string Second { get; }
        public ulong[] Clusters { get; }
        public OwnedDirectories(ITestOutputHelper output)
        {
            var scratch = SecondTestRoot();
            var secondBase = string.IsNullOrWhiteSpace(scratch) ? System.IO.Path.GetTempPath() : System.IO.Path.GetFullPath(scratch);
            var identifier = Guid.NewGuid().ToString("N");
            First = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ResourceManager-HostMemoryDiskReservesTests-" + identifier + "-first");
            Second = System.IO.Path.Combine(secondBase, "ResourceManager-HostMemoryDiskReservesTests-" + identifier + "-second");
            Directory.CreateDirectory(First);
            Directory.CreateDirectory(Second);
            Clusters = [Cluster(First), Cluster(Second)];
            var firstVolume = VolumeRoot(First);
            var secondVolume = VolumeRoot(Second);
            output.WriteLine($"First volume: {firstVolume}, allocation unit: {Clusters[0]}; second volume: {secondVolume}, allocation unit: {Clusters[1]}.");
            output.WriteLine(string.Equals(firstVolume, secondVolume, StringComparison.OrdinalIgnoreCase)
                ? "This fact covers two logical reserve entries on the same volume; it does not claim physical cross-volume coverage."
                : "This fact uses reserve entries on two distinct volume roots.");
        }
        public HostMemoryReserveSettings Setting(int index, ulong units) => new(index == 0 ? First : Second, Clusters[index] * units);
        public string ReserveFile(int index) => Assert.Single(Directory.EnumerateFiles(index == 0 ? First : Second, ".rm-host-memory-reserve-*.bin"));
        public void Dispose()
        {
            string[] names = ["ordered.dmp", "seek.dmp", "persist.dmp", "retired.dmp", "second.dmp", "empty-first.dmp", "cross.dmp"];
            foreach (var directory in new[] { First, Second })
            {
                // Only flat files created in these two uniquely owned directories are eligible.
                // Dispose all reserves and leases before this fixture; never recurse or follow links.
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    var name = System.IO.Path.GetFileName(path);
                    Assert.True(name.StartsWith(".rm-host-memory-reserve", StringComparison.Ordinal) ||
                        names.Any(known => name == known || name == known + ".part0002"), name);
                    File.Delete(path);
                }
                Directory.Delete(directory);
            }
        }
    }

    private static string? SecondTestRoot()
    {
        var configured = Environment.GetEnvironmentVariable("RM_MEMORY_RESERVE_TEST_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH") : configured;
    }

    private static string VolumeRoot(string path)
    {
        var root = new StringBuilder(32768);
        Assert.True(GetVolumePathName(path, root, root.Capacity));
        return root.ToString();
    }

    private static ulong Cluster(string path)
    {
        Assert.True(GetDiskFreeSpace(VolumeRoot(path), out var sectors, out var bytes, out _, out _));
        return (ulong)sectors * bytes;
    }

    private static ulong Allocation(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.True(GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()));
        return checked((ulong)info.AllocationSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint clusters);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out StandardInfo info, uint size);
}
