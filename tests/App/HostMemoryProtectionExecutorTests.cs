using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

// Every destructive action below is confined to the ping.exe child created inside the test.
// No live inventory, subscription source, protection host or user process is used.
public sealed class HostMemoryProtectionExecutorTests : IDisposable
{
    private const nuint AllocationSize = 16 * 1024 * 1024;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rm-host-memory-executor-" + Guid.NewGuid().ToString("N"));

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
            var result = target.Execute(directory);
            Assert.Equal("excluded", result.Outcome);
            Assert.Null(result.DumpPath);
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
            var executor = new WindowsHostMemoryProtectionExecutor(_ => unchecked((int)0xc0000022));
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(directory);
            Assert.Equal("suspend-failed", result.Outcome);
            Assert.True(result.SuspendFailed);
            Assert.Null(result.DumpPath);
            Assert.Equal(0UL, result.BytesWritten);
            Assert.False(child.HasExited);
            Assert.False(Directory.Exists(directory));
        }
        finally { StopOwnedChild(child); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateCommitFallsWhileStandardDumpGrowsThenOwnedChildTerminates(bool wow64)
    {
        using var child = StartOwnedChild(wow64);
        try
        {
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
            var result = target.Execute(directory);
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
            Assert.NotNull(result.DumpPath);
            Assert.Equal((ulong)new FileInfo(result.DumpPath).Length, result.BytesWritten);
            Assert.True(samples.Count > 1);
            Assert.Contains(samples, sample => initialCommit - sample.Commit >= (long)AllocationSize / 2);
            Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second.FileBytes > pair.First.FileBytes));
            lock (HostMemoryMinidumpWriter.DbgHelpGate)
                AssertStandardDumpAndPrivateMarker(result.DumpPath, allocation, marker);
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
            var executor = new WindowsHostMemoryProtectionExecutor(HostMemoryProtectionNative.NtSuspendProcess,
                (_, _, bytes) => { bytesBeforeFailure = bytes; throw new IOException("injected-disk-full"); });
            using var target = executor.Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(directory);
            Assert.Equal("slice-incomplete", result.Outcome);
            Assert.Contains("injected-disk-full", result.Reason);
            Assert.False(result.SuspendFailed);
            Assert.True(child.WaitForExit(5000));
            Assert.NotNull(result.DumpPath);
            Assert.True(result.BytesWritten > 0);
            Assert.Equal(bytesBeforeFailure, result.BytesWritten);
            Assert.Equal((ulong)new FileInfo(result.DumpPath).Length, result.BytesWritten);
        }
        finally { StopOwnedChild(child); }
    }

    [Fact]
    public void FileCreationFailureStillTerminatesOwnedChildWithoutInventingAPath()
    {
        using var child = StartOwnedChild();
        try
        {
            Directory.CreateDirectory(directory);
            var fileInsteadOfDirectory = Path.Combine(directory, "file");
            File.WriteAllText(fileInsteadOfDirectory, "owned fixture");
            using var target = new WindowsHostMemoryProtectionExecutor().Open(Identity(child));
            Assert.Null(target.ExclusionReason);
            var result = target.Execute(fileInsteadOfDirectory);
            Assert.Equal("slice-incomplete", result.Outcome);
            Assert.NotNull(result.Reason);
            Assert.True(child.WaitForExit(5000));
            Assert.Null(result.DumpPath);
            Assert.Equal(0UL, result.BytesWritten);
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
                actions.Add(Task.Run(() => firstTarget.Execute(directory)));
                Assert.True(firstWriting.Wait(TimeSpan.FromSeconds(5)), actions[0].IsCompletedSuccessfully
                    ? $"First action ended before decommit observation: {actions[0].Result.Outcome}: {actions[0].Result.Reason}"
                    : "The first action did not reach a decommitted chunk.");
                actions.Add(Task.Run(() =>
                {
                    secondAttempted.Set();
                    return secondTarget.Execute(directory);
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

    private static unsafe void AssertStandardDumpAndPrivateMarker(string path, nuint allocation, byte[] marker)
    {
        // A mapped view rounds capacity to page boundaries; only the file's actual length is payload.
        var fileLength = checked((ulong)new FileInfo(path).Length);
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
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
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
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

    private static Process StartOwnedChild(bool wow64 = false)
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

    private static HostMemoryProcess Identity(Process child)
    {
        using var handle = HostMemoryProtectionNative.OpenProcess(0x1000, false, child.Id);
        Assert.False(handle.IsInvalid);
        Assert.True(HostMemoryProtectionNative.GetProcessTimes(handle, out var creation, out _, out _, out _));
        return new(child.Id, creation.ToString(CultureInfo.InvariantCulture), "ping", 0);
    }

    private static SafeProcessHandle OpenAllocationHandle(Process child)
    {
        var handle = HostMemoryProtectionNative.OpenProcess(0x0008 | 0x0020, false, child.Id);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    private static void StopOwnedChild(Process child)
    {
        if (!child.HasExited) { child.Kill(); Assert.True(child.WaitForExit(5000)); }
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        // This test creates only known flat fixture/dump files; never traverse or delete other roots.
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualAllocEx(SafeProcessHandle process, nuint address, nuint size, uint type, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint bytesWritten);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpReadDumpStream(nint baseOfDump, uint stream, out nint directory, out nint streamPointer, out uint streamBytes);
}
