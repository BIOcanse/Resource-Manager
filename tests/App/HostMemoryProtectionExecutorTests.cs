using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

// Every destructive action below is confined to the ping.exe child created inside the test.
// No live inventory, subscription source, protection host or user process is used.
public sealed class HostMemoryProtectionExecutorTests : IDisposable
{
    private const nuint AllocationSize = 16 * 1024 * 1024;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rm-host-memory-executor-" + Guid.NewGuid().ToString("N"));
    private string SecondDirectory => directory + "-second";

    [Theory]
    [InlineData("dumpThenTerminate", true, "software-no-dump")]
    [InlineData("terminateOnly", true, "terminate-only")]
    [InlineData("terminateOnly", false, "terminate-only")]
    public void DirectActionModesTerminateOwnedChildWithoutSuspendSliceOrReserveConsumption(string mode, bool noDump, string reason)
    {
        using var child = StartOwnedChild();
        try
        {
            using var reserve = CreateReserve();
            var remaining = reserve.ReadStatus().RemainingBytes;
            var suspendCalls = 0;
            var executor = new WindowsHostMemoryProtectionExecutor(_ => { suspendCalls++; return -1; });
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(reserve, new(mode, noDump));
            Assert.Equal("terminated-without-dump", result.Outcome);
            Assert.Equal(reason, result.Reason);
            Assert.Equal(0, suspendCalls);
            Assert.True(child.WaitForExit(5000));
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.Equal(remaining, reserve.ReadStatus().RemainingBytes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void MismatchedIdentityDoesNotSuspendDumpOrKillTheOwnedChild()
    {
        using var child = StartOwnedChild();
        try
        {
            var identity = Identity(child);
            identity = identity with { ProcessStartKey = (ulong.Parse(identity.ProcessStartKey, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) };
            using var target = new WindowsHostMemoryProtectionExecutor().Open(identity);
            Assert.Equal("identity-changed", target.ExclusionReason);
            using var reserve = new HostMemoryDiskReserves();
            var result = target.Execute(reserve);
            Assert.Equal("excluded", result.Outcome);
            Assert.Empty(result.DumpPaths);
            Assert.False(child.HasExited);
            Assert.False(Directory.Exists(directory));
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void SuspendFailureDoesNotCreateSliceOrTerminateTheOwnedChild()
    {
        using var child = StartOwnedChild();
        try
        {
            using var reserve = CreateReserve();
            var remaining = reserve.ReadStatus().RemainingBytes;
            var executor = new WindowsHostMemoryProtectionExecutor(_ => unchecked((int)0xc0000022));
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(reserve);
            Assert.Equal("suspend-failed", result.Outcome);
            Assert.True(result.SuspendFailed);
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.False(child.HasExited);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
            Assert.Equal(remaining, reserve.ReadStatus().RemainingBytes);
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void MissingReserveDoesNotSuspendDumpOrTerminateOwnedChild()
    {
        using var child = StartOwnedChild();
        try
        {
            using var reserve = new HostMemoryDiskReserves();
            var suspendCalls = 0;
            var executor = new WindowsHostMemoryProtectionExecutor(handle =>
            {
                suspendCalls++;
                return HostMemoryProtectionNative.NtSuspendProcess(handle);
            });
            using var target = executor.Open(Identity(child));
            var result = target.Execute(reserve);
            Assert.Equal("reserve-unavailable", result.Outcome);
            Assert.Equal(0, suspendCalls);
            Assert.False(child.HasExited);
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.False(Directory.Exists(directory));
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void PrivateCommitLargerThanRemainingReserveTerminatesWithoutSuspendOrDump()
    {
        using var child = StartOwnedChild();
        try
        {
            using var reserve = CreateReserve(64 * 1024);
            var remaining = reserve.ReadStatus().RemainingBytes;
            var identity = Identity(child);
            Assert.True(identity.PrivateCommitBytes > remaining);
            var suspendCalls = 0;
            var executor = new WindowsHostMemoryProtectionExecutor(handle =>
            {
                suspendCalls++;
                return HostMemoryProtectionNative.NtSuspendProcess(handle);
            });
            using var target = executor.Open(identity);
            var result = target.Execute(reserve);
            Assert.Equal("terminated-without-dump", result.Outcome);
            Assert.Equal("private-commit-exceeds-reserve", result.Reason);
            Assert.Equal(0, suspendCalls);
            Assert.True(child.WaitForExit(5000));
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.Equal(remaining, reserve.ReadStatus().RemainingBytes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void FullMinidumpExceedsReserveEvenWhenReportedPrivateCommitFitsTerminatesWithoutSuspendOrDump()
    {
        using var child = StartOwnedChild();
        try
        {
            using var reserve = CreateReserve();
            var cluster = Assert.Single(reserve.ReadStatus().Entries).ClusterBytes;
            Assert.True(cluster > 0);
            // Control only the published metric input. Native sizing must still independently measure
            // this owned child's complete image instead of trusting that deliberately small metric.
            var identity = Identity(child) with { PrivateCommitBytes = 1 };
            var privateBytes = identity.PrivateCommitBytes!.Value;
            var capacity = cluster;
            var status = reserve.Prepare([new HostMemoryReserveSettings(directory, capacity)]);
            Assert.True(status.Built, status.Reason);
            Assert.True(privateBytes <= status.RemainingBytes);
            using (var handle = HostMemoryProtectionNative.OpenProcess(HostMemoryProtectionNative.RequiredAccess, false, child.Id))
            {
                Assert.False(handle.IsInvalid);
                var layout = HostMemoryMinidumpWriter.ReadLayout(handle);
                var fullBytes = HostMemoryMinidumpWriter.Measure(handle, child.Id, layout);
                using var lease = reserve.TryAcquire();
                Assert.NotNull(lease);
                Assert.True(lease.CanFit(privateBytes));
                Assert.False(lease.CanFit(fullBytes));
            }
            var suspendCalls = 0;
            var executor = new WindowsHostMemoryProtectionExecutor(handle =>
            {
                suspendCalls++;
                return HostMemoryProtectionNative.NtSuspendProcess(handle);
            });
            using var target = executor.Open(identity);
            var result = target.Execute(reserve);
            Assert.Equal("terminated-without-dump", result.Outcome);
            Assert.Equal("minidump-exceeds-reserve", result.Reason);
            Assert.Equal(0, suspendCalls);
            Assert.True(child.WaitForExit(5000));
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.Equal(status.RemainingBytes, reserve.ReadStatus().RemainingBytes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
        }
        finally { StopOwnedChild(child); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PrivateCommitFallsWhileStandardDumpGrowsThenOwnedChildTerminates(bool wow64, bool multipleEntries)
    {
        using var child = StartOwnedChild(wow64);
        try
        {
            using var reserve = multipleEntries ? CreateSegmentedReserve() : CreateReserve();
            var remaining = reserve.ReadStatus().RemainingBytes;
            using var allocationHandle = OpenAllocationHandle(child);
            var marker = "host-memory-private-slice-marker"u8.ToArray();
            var allocation = VirtualAllocEx(allocationHandle, 0, AllocationSize, 0x3000, 0x04);
            Assert.NotEqual((nuint)0, allocation);
            Assert.True(WriteProcessMemory(allocationHandle, allocation, marker, (nuint)marker.Length, out _));
            // A committed no-access page must still be included rather than silently skipped.
            Assert.True(HostMemoryProtectionNative.VirtualProtectEx(allocationHandle, allocation + AllocationSize - 4096, 4096, 0x01, out _));
            child.Refresh();
            var initialCommit = child.PrivateMemorySize64;
            List<(long Commit, ulong FileBytes, ulong Released)> samples = [];
            List<(nuint Address, nuint Size, int Error)> failedReleases = [];
            var executor = new WindowsHostMemoryProtectionExecutor(HostMemoryProtectionNative.NtSuspendProcess,
                (_, released, bytes) =>
                {
                    Assert.False(child.HasExited);
                    child.Refresh();
                    samples.Add((child.PrivateMemorySize64, bytes, released));
                }, (address, size, error) => failedReleases.Add((address, size, error)));
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(reserve);
            AssertCompleteSlice(result);
            Assert.DoesNotContain(failedReleases, failed => failed.Address < allocation + AllocationSize && failed.Address + failed.Size > allocation);
            if (result.Outcome == "completed") Assert.Empty(failedReleases);
            else
            {
                Assert.NotEmpty(failedReleases);
                Assert.All(failedReleases, failed => Assert.True(failed.Error != 0));
                var failedBytes = failedReleases.Aggregate(0UL, (total, failed) => total + (ulong)failed.Size);
                Assert.StartsWith($"private-memory-decommit-limited chunks={failedReleases.Count} bytes={failedBytes};", result.Reason);
            }
            Assert.False(result.SuspendFailed);
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(multipleEntries ? 2 : 1, result.DumpPaths.Count);
            if (multipleEntries) Assert.Equal(64 * 1024, new FileInfo(result.DumpPaths[0]).Length);
            Assert.Equal(result.DumpPaths.Aggregate(0UL, (total, path) => total + (ulong)new FileInfo(path).Length), result.BytesWritten);
            Assert.Equal(result.DumpPaths.Aggregate(0UL, (total, path) => total + ReadAllocatedBytes(path)), remaining - reserve.ReadStatus().RemainingBytes);
            Assert.True(samples.Count > 1);
            Assert.Contains(samples, sample => initialCommit - sample.Commit >= (long)AllocationSize / 2);
            Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second.FileBytes > pair.First.FileBytes));
            lock (HostMemoryMinidumpWriter.DbgHelpGate)
                AssertStandardDumpAndPrivateMarker(result.DumpPaths, allocation, marker);
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void MidWriteFailureStillTerminatesOwnedChildAndReportsActualPartialBytes()
    {
        using var child = StartOwnedChild();
        ulong bytesBeforeFailure = 0;
        try
        {
            using var reserve = CreateReserve();
            var remaining = reserve.ReadStatus().RemainingBytes;
            var executor = new WindowsHostMemoryProtectionExecutor(HostMemoryProtectionNative.NtSuspendProcess,
                (_, _, bytes) => { bytesBeforeFailure = bytes; throw new IOException("injected-disk-full"); });
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(reserve);
            Assert.Equal("slice-incomplete", result.Outcome);
            Assert.Contains("injected-disk-full", result.Reason);
            Assert.False(result.SuspendFailed);
            Assert.True(child.WaitForExit(5000));
            var dumpPath = Assert.Single(result.DumpPaths);
            Assert.True(result.BytesWritten > 0);
            Assert.Equal(bytesBeforeFailure, result.BytesWritten);
            Assert.Equal((ulong)new FileInfo(dumpPath).Length, result.BytesWritten);
            var after = reserve.ReadStatus().RemainingBytes;
            Assert.True(after < remaining);
            Assert.Equal(ReadAllocatedBytes(dumpPath), remaining - after);
            Assert.Equal(after, reserve.ReadStatus().RemainingBytes); // No implicit reserve refill.
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void FileCreationFailureStillTerminatesOwnedChildWithoutInventingAPath()
    {
        using var child = StartOwnedChild();
        try
        {
            using var funded = CreateReserve();
            var remaining = funded.ReadStatus().RemainingBytes;
            using var reserve = new FailingReserve(funded, failCreate: true);
            using var target = new WindowsHostMemoryProtectionExecutor().Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(reserve);
            Assert.Equal("slice-incomplete", result.Outcome);
            Assert.NotNull(result.Reason);
            Assert.True(child.WaitForExit(5000));
            Assert.Empty(result.DumpPaths);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.Equal(remaining, funded.ReadStatus().RemainingBytes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void MetadataCallbackWriteFailureReturnsToManagedCodeAndTerminatesOwnedChild()
    {
        using var child = StartOwnedChild();
        try
        {
            using var funded = CreateReserve();
            var remaining = funded.ReadStatus().RemainingBytes;
            using var reserve = new FailingReserve(funded, failCreate: false);
            using var target = new WindowsHostMemoryProtectionExecutor().Open(Identity(child));
            var result = target.Execute(reserve);
            Assert.Equal("slice-incomplete", result.Outcome);
            Assert.Contains("injected-metadata-write-failure", result.Reason);
            Assert.True(reserve.WriteAttempts > 0);
            Assert.True(child.WaitForExit(5000));
            Assert.Empty(result.DumpPaths); // Composite parts are created lazily on their first actual write.
            Assert.Equal(0UL, result.BytesWritten);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.dmp"));
            Assert.Equal(remaining, funded.ReadStatus().RemainingBytes);
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void SeparateExecutorInstancesSerializeDestructiveActionsForTheirOwnedChildren()
    {
        using var firstChild = StartOwnedChild();
        Process? secondChild = null;
        using var firstWriting = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        using var secondSuspended = new ManualResetEventSlim();
        try
        {
            using var reserve = CreateReserve();
            secondChild = StartOwnedChild();
            var held = 0;
            var firstExecutor = new WindowsHostMemoryProtectionExecutor(HostMemoryProtectionNative.NtSuspendProcess,
                (_, _, _) =>
                {
                    if (Interlocked.Exchange(ref held, 1) != 0) return;
                    firstWriting.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
                });
            var secondExecutor = new WindowsHostMemoryProtectionExecutor(handle =>
            {
                secondSuspended.Set();
                return HostMemoryProtectionNative.NtSuspendProcess(handle);
            });
            using var firstTarget = firstExecutor.Open(Identity(firstChild));
            using var secondTarget = secondExecutor.Open(Identity(secondChild));
            Assert.Null(firstTarget.ExclusionReason);
            Assert.Null(secondTarget.ExclusionReason);
            List<Task<HostMemoryActionResult>> actions = [];
            try
            {
                actions.Add(Task.Run(() => firstTarget.Execute(reserve)));
                Assert.True(firstWriting.Wait(TimeSpan.FromSeconds(5)), actions[0].IsCompletedSuccessfully
                    ? $"First action ended before decommit observation: {actions[0].Result.Outcome}: {actions[0].Result.Reason}"
                    : "The first action did not reach a decommitted chunk.");
                actions.Add(Task.Run(() =>
                {
                    secondAttempted.Set();
                    return secondTarget.Execute(reserve);
                }));
                Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(secondSuspended.Wait(TimeSpan.FromMilliseconds(250)));
                Assert.False(firstChild.HasExited);
                Assert.False(secondChild.HasExited);
            }
            finally
            {
                releaseFirst.Set();
                // Complete both calls before disposing their retained effect handles or test events.
                Task.WhenAll(actions).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
            Assert.All(actions, action => AssertCompleteSlice(action.Result));
            Assert.True(firstChild.WaitForExit(5000));
            Assert.True(secondChild.WaitForExit(5000));
        }
        finally
        {
            releaseFirst.Set();
            StopOwnedChild(firstChild);
            if (secondChild is not null) { StopOwnedChild(secondChild); secondChild.Dispose(); }
        }
    }

    internal static unsafe void AssertStandardDumpAndPrivateMarker(IReadOnlyList<string> paths, nuint allocation, byte[] marker)
    {
        if (paths.Count > 1)
        {
            // Reassemble only in test-owned memory; production never writes an unfunded combined file.
            var bytes = new byte[checked((int)paths.Sum(path => new FileInfo(path).Length))];
            var offset = 0;
            foreach (var part in paths)
            {
                using var input = File.OpenRead(part);
                var length = checked((int)input.Length);
                input.ReadExactly(bytes.AsSpan(offset, length));
                offset += length;
            }
            fixed (byte* baseOfDump = bytes) AssertDumpMemory(baseOfDump, (ulong)bytes.Length, allocation, marker);
            return;
        }
        var path = Assert.Single(paths);
        // A mapped view rounds capacity to page boundaries; only the file's actual length is payload.
        var fileLength = checked((ulong)new FileInfo(path).Length);
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try { AssertDumpMemory(pointer, fileLength, allocation, marker); }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    private static unsafe void AssertDumpMemory(byte* pointer, ulong fileLength, nuint allocation, byte[] marker)
    {
            // Use Windows' independent minidump reader, not the production writer's layout code.
            Assert.True(MiniDumpReadDumpStream((nint)pointer, 3, out _, out var threads, out var threadBytes));
            Assert.True(threadBytes >= 4);
            var threadCount = *(uint*)threads;
            Assert.True(threadCount > 0);
            // MINIDUMP_THREAD: ThreadContext location descriptor is at offsets 40 and 44.
            var contextBytes = *(uint*)((byte*)threads + 4 + 40);
            var contextRva = *(uint*)((byte*)threads + 4 + 44);
            Assert.True(contextBytes > 0);
            Assert.InRange((ulong)contextRva + contextBytes, 1UL, fileLength);
            Assert.True(MiniDumpReadDumpStream((nint)pointer, 4, out _, out var modules, out var moduleBytes));
            Assert.True(moduleBytes > 4);
            Assert.True(*(uint*)modules > 0);
            Assert.True(MiniDumpReadDumpStream((nint)pointer, 9, out _, out var memory, out var memoryBytes));
            Assert.True(memoryBytes >= 16);
            var count = *(ulong*)memory;
            var payload = *((ulong*)memory + 1);
            Assert.True(count > 0);
            Assert.Equal(16UL + count * 16, (ulong)memoryBytes);
            var found = false;
            var sharedUserDataFound = false;
            for (ulong index = 0; index < count; index++)
            {
                var address = *((ulong*)memory + 2 + index * 2);
                var size = *((ulong*)memory + 3 + index * 2);
                Assert.InRange(payload + size, 1UL, fileLength);
                if ((ulong)allocation >= address && (ulong)allocation + (ulong)marker.Length <= address + size)
                {
                    Assert.Equal(marker, new ReadOnlySpan<byte>(pointer + checked((long)(payload + (ulong)allocation - address)), marker.Length).ToArray());
                    found = true;
                }
                if (address == (ulong)HostMemoryMinidumpWriter.SharedUserDataAddress && size == 4096)
                    sharedUserDataFound = true;
                payload += size;
            }
            Assert.True(found, "The owned child's private allocation must be in the standard memory list.");
            Assert.True(sharedUserDataFound, "The OS-owned shared page remains in the dump without an invalid process decommit.");
            Assert.Equal(fileLength, payload);
    }

    private static void AssertCompleteSlice(HostMemoryActionResult result)
    {
        Assert.True(result.Outcome is "completed" or "completed-with-release-limit", $"{result.Outcome}: {result.Reason}");
        if (result.Outcome == "completed") Assert.Null(result.Reason);
        else
        {
            Assert.StartsWith("private-memory-decommit-limited chunks=", result.Reason);
            Assert.Contains("address=0x", result.Reason);
            Assert.Contains("native-error=", result.Reason);
        }
    }

    private HostMemoryDiskReserves CreateReserve(ulong sizeBytes = 32 * 1024 * 1024)
    {
        var reserve = new HostMemoryDiskReserves();
        try
        {
            var status = reserve.Prepare([new HostMemoryReserveSettings(directory, sizeBytes)]);
            Assert.True(status.Built, status.Reason);
            return reserve;
        }
        catch { reserve.Dispose(); throw; }
    }

    private HostMemoryDiskReserves CreateSegmentedReserve()
    {
        var reserve = new HostMemoryDiskReserves();
        try
        {
            var status = reserve.Prepare([new(directory, 64 * 1024), new(SecondDirectory, 32 * 1024 * 1024)]);
            Assert.True(status.Built, status.Reason);
            return reserve;
        }
        catch { reserve.Dispose(); throw; }
    }

    private static ulong ReadAllocatedBytes(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()),
            $"GetFileInformationByHandleEx failed: {Marshal.GetLastWin32Error()}");
        Assert.True(info.AllocationSize >= info.EndOfFile);
        return checked((ulong)info.AllocationSize);
    }

    private sealed class FailingReserve(IHostMemoryProtectionReserve inner, bool failCreate) : IHostMemoryProtectionReserve
    {
        internal int WriteAttempts;
        public HostMemoryReserveStatus ReadStatus() => inner.ReadStatus();
        public HostMemoryReserveStatus Prepare(IReadOnlyList<HostMemoryReserveSettings> entries) => inner.Prepare(entries);
        public HostMemoryReserveStatus Rebuild() => inner.Rebuild();
        public IHostMemoryProtectionReserveLease? TryAcquire()
            => inner.TryAcquire() is { } lease ? new FailingLease(lease, this, failCreate) : null;
        public void Dispose() { }

        private sealed class FailingLease(IHostMemoryProtectionReserveLease lease, FailingReserve owner, bool failCreate)
            : IHostMemoryProtectionReserveLease
        {
            public string Location => lease.Location;
            public ulong RemainingBytes => lease.RemainingBytes;
            public IReadOnlyList<string> SlicePaths => lease.SlicePaths;
            public bool CanFit(ulong bytes) => lease.CanFit(bytes);
            public Stream CreateSlice(string fileName)
                => failCreate ? throw new IOException("injected-slice-create-failure") : new FailingWriteStream(lease.CreateSlice(fileName), owner);
            public void Dispose() => lease.Dispose();
        }

        private sealed class FailingWriteStream(Stream stream, FailingReserve owner) : Stream
        {
            public override bool CanRead => stream.CanRead;
            public override bool CanSeek => stream.CanSeek;
            public override bool CanWrite => stream.CanWrite;
            public override long Length => stream.Length;
            public override long Position { get => stream.Position; set => stream.Position = value; }
            public override void Flush() => stream.Flush();
            public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
            public override void SetLength(long value) => stream.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                owner.WriteAttempts++;
                throw new IOException("injected-metadata-write-failure");
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) stream.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    internal static Process StartOwnedChild(bool wow64 = false)
    {
        var systemDirectory = wow64
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        var child = Process.Start(new ProcessStartInfo(Path.Combine(systemDirectory, "ping.exe"))
        {
            ArgumentList = { "-t", "127.0.0.1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the owned test child.");
        try
        {
            // Its first output proves the loader and entry point ran before taking native metadata.
            Assert.NotNull(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            return child;
        }
        catch
        {
            StopOwnedChild(child);
            child.Dispose();
            throw;
        }
    }

    internal static HostMemoryProcess Identity(Process child)
    {
        using var handle = HostMemoryProtectionNative.OpenProcess(0x1000, false, child.Id);
        Assert.False(handle.IsInvalid);
        Assert.True(HostMemoryProtectionNative.GetProcessTimes(handle, out var creation, out _, out _, out _));
        child.Refresh();
        return new(child.Id, creation.ToString(CultureInfo.InvariantCulture), "ping", checked((ulong)child.PrivateMemorySize64));
    }

    private static SafeProcessHandle OpenAllocationHandle(Process child)
    {
        var handle = HostMemoryProtectionNative.OpenProcess(0x0008 | 0x0020, false, child.Id);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    internal static void StopOwnedChild(Process child)
    {
        if (!child.HasExited) { child.Kill(); Assert.True(child.WaitForExit(5000)); }
    }

    public void Dispose()
    {
        foreach (var ownedDirectory in new[] { directory, SecondDirectory })
        {
            if (!Directory.Exists(ownedDirectory)) continue;
            // This test creates only known flat fixture/dump files; never traverse or delete other roots.
            foreach (var file in Directory.EnumerateFiles(ownedDirectory)) File.Delete(file);
            Directory.Delete(ownedDirectory);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualAllocEx(SafeProcessHandle process, nuint address, nuint size, uint type, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint bytesWritten);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileStandardInfo information, uint bufferSize);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpReadDumpStream(nint baseOfDump, uint stream, out nint directory, out nint streamPointer, out uint streamBytes);
}
