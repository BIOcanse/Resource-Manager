using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryDiskReserveTests
{
    [Fact]
    public void UnconfiguredReserveCreatesNothing()
    {
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var status = reserve.Prepare(directory.Path, 0);
        Assert.False(status.Built);
        Assert.Equal("reserve-not-configured", status.Reason);
        Assert.Equal(0UL, status.RemainingBytes);
        Assert.Null(reserve.TryAcquire());
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public void PreparationReallyAllocatesUncompressedNonSparseWholeClusters()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var cluster = Cluster(directory.Path);
        var configured = cluster * 128;
        var status = reserve.Prepare(directory.Path, configured);
        Assert.True(status.Built, status.Reason);
        Assert.Null(status.Reason);
        Assert.Equal(configured, status.ConfiguredSizeBytes);
        Assert.Equal(cluster, status.ClusterBytes);
        Assert.Equal(configured, status.RemainingBytes);
        var path = directory.ReserveFile;
        Assert.Equal(status.RemainingBytes, Allocation(path));
        Assert.Equal(status.RemainingBytes, (ulong)new FileInfo(path).Length);
        Assert.Equal(0, (int)(File.GetAttributes(path) & (FileAttributes.SparseFile | FileAttributes.Compressed)));
        Assert.Equal(status.RemainingBytes, DiskSize(path));
        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
    }

    [Fact]
    public void PartialAllocationUnitIsRejectedWithoutSilentlyRoundingTheSetting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var cluster = Cluster(directory.Path);
        var configured = cluster * 2 + 1;
        var status = reserve.Prepare(directory.Path, configured);
        Assert.False(status.Built);
        Assert.Equal(configured, status.ConfiguredSizeBytes);
        Assert.Equal(cluster, status.ClusterBytes);
        Assert.Contains($"cluster-bytes={cluster}", status.Reason!);
        Assert.Equal(0UL, status.RemainingBytes);
        Assert.Null(reserve.TryAcquire());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.bin"));
    }

    [Fact]
    public void ImpossibleCapacityFailsPreflightWithoutAllocatingAnOversizedFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var cluster = Cluster(directory.Path);
        var status = reserve.Prepare(directory.Path, ulong.MaxValue - ulong.MaxValue % cluster);
        Assert.False(status.Built);
        Assert.Equal("insufficient-disk-space-for-reserve", status.Reason);
        Assert.Equal(0UL, status.RemainingBytes);
        Assert.Null(reserve.TryAcquire());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.bin"));
    }

    [Fact]
    public void HeadersSeekSetLengthAndPayloadAreChargedByClustersBeforeGrowthAndOverwritesOnlyOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var prepared = reserve.Prepare(directory.Path, 1024 * 1024);
        Assert.True(prepared.Built, prepared.Reason);
        var cluster = Cluster(directory.Path);
        using var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        Assert.Null(reserve.TryAcquire());
        using var slice = lease.CreateSlice("dump.dmp");
        Assert.Equal(System.IO.Path.Combine(directory.Path, "dump.dmp"), Assert.Single(lease.SlicePaths));
        Assert.True(lease.CanFit(prepared.RemainingBytes));
        Assert.False(lease.CanFit(prepared.RemainingBytes + 1));
        Assert.True(slice.CanSeek && slice.CanRead && slice.CanWrite);
        slice.Write("MDMP"u8); // Header data must consume reserve too.
        Assert.Equal(prepared.RemainingBytes - cluster, lease.RemainingBytes);
        slice.Position = checked((long)(cluster * 2 + 17));
        slice.WriteByte(42);
        Assert.Equal(prepared.RemainingBytes - cluster * 3, lease.RemainingBytes);
        Assert.Equal(lease.RemainingBytes, Allocation(directory.ReserveFile));
        Assert.Equal(cluster * 3, Allocation(System.IO.Path.Combine(directory.Path, "dump.dmp")));
        slice.Position = 0;
        slice.Write("MDMP"u8);
        Assert.Equal(prepared.RemainingBytes - cluster * 3, lease.RemainingBytes);
        slice.SetLength(checked((long)(cluster * 4 + 1)));
        Assert.Equal(prepared.RemainingBytes - cluster * 5, lease.RemainingBytes);
        slice.SetLength(4);
        slice.Position = checked((long)(cluster * 4));
        slice.WriteByte(7);
        Assert.Equal(prepared.RemainingBytes - cluster * 5, lease.RemainingBytes);
        slice.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        Assert.Equal(4, slice.Read(signature));
        Assert.True(signature.SequenceEqual("MDMP"u8));
        Assert.Equal(lease.RemainingBytes, reserve.ReadStatus().RemainingBytes);
        Assert.Equal(prepared.RemainingBytes, lease.RemainingBytes + Allocation(System.IO.Path.Combine(directory.Path, "dump.dmp")));
    }

    [Fact]
    public void ExhaustionRejectsExtensionAndKeepsEarlierSliceAndRemainingAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var status = reserve.Prepare(directory.Path, 64 * 1024);
        Assert.True(status.Built, status.Reason);
        using var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        using var slice = lease.CreateSlice("partial.dmp");
        slice.Write("MDMP"u8);
        var remaining = lease.RemainingBytes;
        Assert.Throws<IOException>(() => slice.SetLength((long)status.RemainingBytes + 1));
        Assert.Equal(4, slice.Length);
        Assert.Equal(remaining, lease.RemainingBytes);
        Assert.Equal(remaining, Allocation(directory.ReserveFile));
        slice.SetLength((long)status.RemainingBytes);
        Assert.Equal(0UL, lease.RemainingBytes);
        Assert.Equal(0UL, Allocation(directory.ReserveFile));
        slice.Position = slice.Length;
        Assert.Throws<IOException>(() => slice.WriteByte(1));
    }

    [Fact]
    public void UnchangedPrepareReopenAndSliceDeletionNeverRefillButExplicitRebuildDoes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        ulong remaining;
        ulong original;
        using (var reserve = new HostMemoryDiskReserve())
        {
            var status = reserve.Prepare(directory.Path, 256 * 1024);
            Assert.True(status.Built, status.Reason);
            original = status.RemainingBytes;
            using (var lease = reserve.TryAcquire())
            {
                Assert.NotNull(lease);
                using var slice = lease.CreateSlice("old.dmp");
                slice.Write(new byte[8193]);
            }
            remaining = reserve.ReadStatus().RemainingBytes;
            Assert.True(remaining < original);
            Assert.Equal(remaining, reserve.Prepare(directory.Path, 256 * 1024).RemainingBytes);
        }
        File.Delete(System.IO.Path.Combine(directory.Path, "old.dmp"));
        using (var reserve = new HostMemoryDiskReserve())
        {
            Assert.Equal(remaining, reserve.Prepare(directory.Path, 256 * 1024).RemainingBytes);
            Assert.Equal(remaining, Allocation(directory.ReserveFile));
            Assert.Equal(original, reserve.Rebuild().RemainingBytes);
            Assert.Equal(original, Allocation(directory.ReserveFile));
        }
    }

    [Fact]
    public void ChangedSizeRetiresOnlyOwnedControlFileAndLeavesSlicesAndUnrelatedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        Assert.True(reserve.Prepare(directory.Path, 128 * 1024).Built);
        var old = directory.ReserveFile;
        using (var lease = reserve.TryAcquire())
        {
            Assert.NotNull(lease);
            using var slice = lease.CreateSlice("kept.dmp");
            slice.WriteByte(17);
        }
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "unrelated.txt"), "owned by test");
        var next = reserve.Prepare(directory.Path, 256 * 1024);
        Assert.True(next.Built, next.Reason);
        Assert.False(File.Exists(old));
        Assert.Equal((byte)17, File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "kept.dmp"))[0]);
        Assert.Equal("owned by test", File.ReadAllText(System.IO.Path.Combine(directory.Path, "unrelated.txt")));
        Assert.Equal(next.RemainingBytes, Allocation(directory.ReserveFile));
    }

    [Fact]
    public void MissingCompletionProofDoesNotAcceptTheReserveAndNamesCannotEscapeTheLocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using (var reserve = new HostMemoryDiskReserve())
        {
            Assert.True(reserve.Prepare(directory.Path, 64 * 1024).Built);
            using var lease = reserve.TryAcquire();
            Assert.NotNull(lease);
            foreach (var name in new[] { "../escape.dmp", "a/b.dmp", "a\\b.dmp", "dump.dmp:alternate", ".rm-host-memory-reserve.json", "..", "dump. " })
                Assert.Throws<ArgumentException>(() => lease.CreateSlice(name));
        }
        var marker = System.IO.Path.Combine(directory.Path, ".rm-host-memory-reserve.json");
        var content = File.ReadAllText(marker);
        Assert.Contains("\"Complete\":true", content);
        File.WriteAllText(marker, content.Replace("\"Complete\":true", "\"Complete\":false", StringComparison.Ordinal));
        using (var reserve = new HostMemoryDiskReserve())
        {
            var status = reserve.Prepare(directory.Path, 64 * 1024);
            Assert.False(status.Built);
            Assert.Contains("incomplete", status.Reason!);
            Assert.Equal(Allocation(directory.ReserveFile), status.RemainingBytes);
            Assert.True(status.RemainingBytes > 0);
            Assert.Null(reserve.TryAcquire());
            Assert.True(reserve.Rebuild().Built);
        }
    }

    [Fact]
    public void CompletedProofWithInvalidFileLengthRemainsUnusableButReportsItsActualAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        var cluster = Cluster(directory.Path);
        var configured = cluster * 16;
        using (var reserve = new HostMemoryDiskReserve())
            Assert.True(reserve.Prepare(directory.Path, configured).Built);
        var path = directory.ReserveFile;
        using (var altered = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
            altered.SetLength((long)cluster + 1);
        var actual = Allocation(path);
        Assert.True(actual > 0);
        using (var reserve = new HostMemoryDiskReserve())
        {
            var status = reserve.Prepare(directory.Path, configured);
            Assert.False(status.Built);
            Assert.Contains("allocation-does-not-match", status.Reason!);
            Assert.Equal(actual, status.RemainingBytes);
            Assert.Null(reserve.TryAcquire());
            Assert.Equal(actual, reserve.Prepare(directory.Path, configured).RemainingBytes);
        }
    }

    [Fact]
    public void FailedRetirementKeepsOwnedAllocationVisibleAndCanBeRetriedWithoutDeletingSlices()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        var initial = reserve.Prepare(directory.Path, Cluster(directory.Path) * 16);
        Assert.True(initial.Built, initial.Reason);
        using (var lease = reserve.TryAcquire())
        {
            Assert.NotNull(lease);
            using var slice = lease.CreateSlice("kept.dmp");
            slice.Write("MDMP"u8);
        }
        var reservePath = directory.ReserveFile;
        var before = Allocation(reservePath);
        var originalAttributes = File.GetAttributes(reservePath);
        File.SetAttributes(reservePath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            var failed = reserve.Prepare(directory.Path, 0);
            Assert.False(failed.Built);
            Assert.NotNull(failed.Reason);
            Assert.Equal(before, failed.RemainingBytes);
            Assert.Equal(before, Allocation(reservePath));
            Assert.Null(reserve.TryAcquire());
        }
        finally { File.SetAttributes(reservePath, originalAttributes); }
        var retired = reserve.Prepare(directory.Path, 0);
        Assert.False(retired.Built);
        Assert.Equal("reserve-not-configured", retired.Reason);
        Assert.Equal(0UL, retired.RemainingBytes);
        Assert.False(File.Exists(reservePath));
        Assert.Equal("MDMP"u8.ToArray(), File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "kept.dmp")));
    }

    [Fact]
    public void InvalidExistingControlFileNeverBecomesOwnedOrTruncatedByPrepareOrRebuild()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        var marker = System.IO.Path.Combine(directory.Path, ".rm-host-memory-reserve.json");
        const string existing = "existing unrelated control-name collision";
        File.WriteAllText(marker, existing);
        using var reserve = new HostMemoryDiskReserve();
        Assert.False(reserve.Prepare(directory.Path, Cluster(directory.Path) * 16).Built);
        Assert.False(reserve.Rebuild().Built);
        Assert.False(reserve.Rebuild().Built);
        Assert.Equal(0UL, reserve.ReadStatus().RemainingBytes);
        Assert.Equal(existing, File.ReadAllText(marker));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.bin"));
    }

    [Fact]
    public void ExistingDirectoryReparsePointIsRejectedBeforeCreatingAnythingInItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var target = new OwnedDirectory();
        var link = System.IO.Path.Combine(directory.Path, "test-owned-link");
        try { Directory.CreateSymbolicLink(link, target.Path); }
        catch (UnauthorizedAccessException) { return; } // Standard accounts may lack symlink privilege.
        catch (IOException error) when ((error.HResult & 0xFFFF) == 1314) { return; }
        try
        {
            using var reserve = new HostMemoryDiskReserve();
            var status = reserve.Prepare(link, Cluster(target.Path) * 16);
            Assert.False(status.Built);
            Assert.Contains("reparse", status.Reason!);
            Assert.Empty(Directory.EnumerateFiles(target.Path));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task PrepareAndRebuildWaitForTheWholeLeaseWhileStatusRemainsReadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new OwnedDirectory();
        using var reserve = new HostMemoryDiskReserve();
        Assert.True(reserve.Prepare(directory.Path, 64 * 1024).Built);
        var lease = reserve.TryAcquire();
        Assert.NotNull(lease);
        using var started = new ManualResetEventSlim();
        var rebuilding = Task.Run(() => { started.Set(); return reserve.Rebuild(); });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            using var slice = lease.CreateSlice("lease.dmp");
            slice.WriteByte(1);
            Assert.False(rebuilding.IsCompleted);
            Assert.Equal(lease.RemainingBytes, reserve.ReadStatus().RemainingBytes);
        }
        finally { lease.Dispose(); }
        Assert.True((await rebuilding.WaitAsync(TimeSpan.FromSeconds(10))).Built);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "lease.dmp")));
    }

    private sealed class OwnedDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ResourceManager-HostMemoryDiskReserveTests-" + Guid.NewGuid().ToString("N"));
        public string ReserveFile => Assert.Single(Directory.EnumerateFiles(Path, ".rm-host-memory-reserve-*.bin"));
        public OwnedDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // Flat, test-owned directory only. Never recursively delete, follow links or touch a
            // configured product reserve. Every file created by these tests is in this list.
            string[] slices = ["dump.dmp", "partial.dmp", "old.dmp", "kept.dmp", "lease.dmp", "unrelated.txt"];
            foreach (var file in Directory.EnumerateFiles(Path))
            {
                var name = System.IO.Path.GetFileName(file);
                Assert.True(name.StartsWith(".rm-host-memory-reserve", StringComparison.Ordinal) || slices.Contains(name), name);
                File.Delete(file);
            }
            Directory.Delete(Path);
        }
    }

    private static ulong Cluster(string path)
    {
        Assert.True(GetDiskFreeSpace(System.IO.Path.GetPathRoot(path)!, out var sectors, out var bytes, out _, out _));
        return (ulong)sectors * bytes;
    }
    private static ulong Allocation(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.True(GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()));
        return checked((ulong)info.AllocationSize);
    }
    private static ulong DiskSize(string path)
    {
        Marshal.SetLastPInvokeError(0);
        var low = GetCompressedFileSize(path, out var high);
        Assert.True(low != uint.MaxValue || Marshal.GetLastWin32Error() == 0);
        return ((ulong)high << 32) | low;
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
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint clusters);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out StandardInfo info, uint size);
}
