using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNativeAtomicFileCommitterRetryTests
{
    private const int AccessDenied = unchecked((int)0xC0000022);
    private const int NameNotFound = unchecked((int)0xC0000034);

    [Fact]
    public void RenameRecoversOnSecondCallUsingSameFlushedHandleAndBuffer()
    {
        using var fixture = new Store();
        var calls = 0;
        SafeFileHandle? firstHandle = null;
        IntPtr firstBuffer = IntPtr.Zero;
        byte[]? firstInformation = null;
        WindowsNativeAtomicFileCommitter.CommitReplace(fixture.Source, fixture.Target, (handle, buffer, size) =>
        {
            calls++;
            var information = new byte[size];
            Marshal.Copy(buffer, information, 0, information.Length);
            if (calls == 1)
            {
                firstHandle = handle;
                firstBuffer = buffer;
                firstInformation = information;
                return AccessDenied;
            }
            Assert.Same(firstHandle, handle);
            Assert.Equal(firstBuffer, buffer);
            Assert.Equal(firstInformation, information);
            return WindowsNativeAtomicFileCommitter.RenameNative(handle, buffer, size);
        });
        Assert.Equal(2, calls);
        Assert.Equal("new", File.ReadAllText(fixture.Target));
        Assert.False(File.Exists(fixture.Source));
        fixture.AssertLog("rename", "0xC0000022", 1, "retry");
        fixture.AssertLog("rename", "0x00000000", 2, "recovered");
        Assert.Equal(2, fixture.Log.Lines.Count);
    }

    [Fact]
    public void RenameExhaustsExactlyThreeCallsAndPreservesBothFiles()
    {
        using var fixture = new Store();
        var calls = 0;
        var exception = Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitReplace(
            fixture.Source, fixture.Target, (_, _, _) => { calls++; return AccessDenied; }));
        Assert.Equal(3, calls);
        Assert.Contains("NTSTATUS=0xC0000022", exception.Message);
        Assert.Equal(5, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        Assert.Equal("old", File.ReadAllText(fixture.Target));
        Assert.Equal("new", File.ReadAllText(fixture.Source));
        fixture.AssertLog("rename", "0xC0000022", 1, "retry");
        fixture.AssertLog("rename", "0xC0000022", 2, "retry");
        fixture.AssertLog("rename", "0xC0000022", 3, "exhausted");
        Assert.Equal(3, fixture.Log.Lines.Count);
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000043))] // sharing violation
    [InlineData(unchecked((int)0xC0000121))] // cannot delete (also maps to Win32 5)
    [InlineData(unchecked((int)0xC0000056))] // delete pending
    [InlineData(NameNotFound)]
    public void OtherRenameStatusesAreNotRetried(int status)
    {
        using var fixture = new Store();
        var calls = 0;
        var exception = Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitReplace(
            fixture.Source, fixture.Target, (_, _, _) => { calls++; return status; }));
        Assert.Equal(1, calls);
        Assert.Contains($"NTSTATUS=0x{status:X8}", exception.Message);
        Assert.Equal("old", File.ReadAllText(fixture.Target));
        Assert.Equal("new", File.ReadAllText(fixture.Source));
        fixture.AssertLog("rename", $"0x{status:X8}", 1, "failed");
        Assert.Single(fixture.Log.Lines);
    }

    [Fact]
    public void ReadonlyTargetIsNotRetriedEvenForAccessDenied()
    {
        using var fixture = new Store();
        File.SetAttributes(fixture.Target, FileAttributes.ReadOnly);
        var calls = 0;
        Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitReplace(
            fixture.Source, fixture.Target, (_, _, _) => { calls++; return AccessDenied; }));
        Assert.Equal(1, calls);
        fixture.AssertLog("rename", "0xC0000022", 1, "readonly");
        Assert.Single(fixture.Log.Lines);
    }

    [Fact]
    public void SourceOpenFailureNeverCallsRename()
    {
        using var fixture = new Store();
        File.Delete(fixture.Source);
        var calls = 0;
        Assert.Throws<IOException>(() => WindowsNativeAtomicFileCommitter.CommitReplace(
            fixture.Source, fixture.Target, (_, _, _) => { calls++; return AccessDenied; }));
        Assert.Equal(0, calls);
        Assert.Equal("old", File.ReadAllText(fixture.Target));
    }

    [Theory]
    [InlineData(FileOptions.None)]
    [InlineData(FileOptions.Asynchronous)]
    public async Task OpenRecoversOnSecondCallAndKeepsAcquiredVersion(FileOptions options)
    {
        using var fixture = new Store();
        var calls = 0;
        int Open(string path, FileOptions flags, out SafeFileHandle file)
        {
            calls++;
            if (calls == 1) { file = new SafeFileHandle(IntPtr.Zero, true); return NameNotFound; }
            return WindowsNativeAtomicFileCommitter.OpenReadNative(path, flags, out file);
        }
        await using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(fixture.Target, options, Open);
        Assert.Equal(2, calls);
        Assert.Equal(options == FileOptions.Asynchronous, stream.IsAsync);
        Assert.Equal(Path.GetFullPath(fixture.Target), stream.Name);
        WindowsNativeAtomicFileCommitter.CommitReplace(fixture.Source, fixture.Target);
        using var reader = new StreamReader(stream);
        Assert.Equal("old", await reader.ReadToEndAsync());
        Assert.Equal("new", File.ReadAllText(fixture.Target));
        fixture.AssertLog("open", "0xC0000034", 1, "retry");
        fixture.AssertLog("open", "0x00000000", 2, "recovered");
        Assert.Equal(2, fixture.Log.Lines.Count(line => line.Contains("operation=open")));
    }

    [Fact]
    public void OpenExhaustsExactlyThreeCallsWithFinalNativeStatus()
    {
        using var fixture = new Store();
        var calls = 0;
        int Open(string path, FileOptions flags, out SafeFileHandle file)
        {
            calls++;
            file = new SafeFileHandle(IntPtr.Zero, true);
            return NameNotFound;
        }
        var exception = Assert.Throws<FileNotFoundException>(() =>
            WindowsNativeAtomicFileCommitter.OpenCommittedRead(fixture.Target, FileOptions.None, Open));
        Assert.Equal(3, calls);
        Assert.Contains("NTSTATUS=0xC0000034", exception.Message);
        Assert.Equal(fixture.Target, exception.FileName);
        fixture.AssertLog("open", "0xC0000034", 1, "retry");
        fixture.AssertLog("open", "0xC0000034", 2, "retry");
        fixture.AssertLog("open", "0xC0000034", 3, "exhausted");
        Assert.Equal(3, fixture.Log.Lines.Count);
    }

    [Theory]
    [InlineData(unchecked((int)0xC000003A), typeof(DirectoryNotFoundException))]
    [InlineData(AccessDenied, typeof(UnauthorizedAccessException))]
    [InlineData(unchecked((int)0xC0000043), typeof(IOException))]
    public void OtherOpenStatusesAreNotRetried(int status, Type exceptionType)
    {
        using var fixture = new Store();
        var calls = 0;
        int Open(string path, FileOptions flags, out SafeFileHandle file)
        {
            calls++;
            file = new SafeFileHandle(IntPtr.Zero, true);
            return status;
        }
        var exception = Record.Exception(() =>
            WindowsNativeAtomicFileCommitter.OpenCommittedRead(fixture.Target, FileOptions.None, Open));
        Assert.IsType(exceptionType, exception);
        Assert.Equal(1, calls);
        Assert.Contains($"NTSTATUS=0x{status:X8}", exception!.Message);
        fixture.AssertLog("open", $"0x{status:X8}", 1, "failed");
        Assert.Single(fixture.Log.Lines);
    }

    [Fact]
    public void GenuinelyMissingFileStillFailsAfterBound()
    {
        using var fixture = new Store();
        File.Delete(fixture.Target);
        Assert.Throws<FileNotFoundException>(() => WindowsNativeAtomicFileCommitter.OpenCommittedRead(fixture.Target));
        fixture.AssertLog("open", "0xC0000034", 3, "exhausted");
        Assert.Equal(3, fixture.Log.Lines.Count);
    }

    [Fact]
    public async Task NativeOpenKeepsLongUnicodePathsAsyncReadAndDeleteSharing()
    {
        using var fixture = new Store();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, new string('x', 200))).FullName;
        var path = Path.Combine(directory, "设置-🚀-" + new string('y', 70) + ".bin");
        try
        {
            var expected = Enumerable.Repeat((byte)42, 8192).ToArray();
            File.WriteAllBytes(path, expected);
            Assert.Equal(expected, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path));
            Assert.Equal(expected, await WindowsNativeAtomicFileCommitter.ReadCommittedBytesAsync(path, CancellationToken.None));
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(path, FileOptions.SequentialScan);
            File.Delete(path);
            var content = new byte[expected.Length];
            stream.ReadExactly(content);
            Assert.Equal(expected, content);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public void NativeMissingParentAndDirectoryOpenFailImmediately()
    {
        using var fixture = new Store();
        Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsNativeAtomicFileCommitter.OpenCommittedRead(Path.Combine(fixture.Root, "absent", "store.bin")));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeAtomicFileCommitter.OpenCommittedRead(fixture.Root));
        Assert.Equal(2, fixture.Log.Lines.Count);
        Assert.All(fixture.Log.Lines, line => Assert.Contains("attempt=1/3 outcome=failed", line));
    }
    private sealed class Store : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("rm-atomic-retry-").FullName;
        public string Source => Path.Combine(Root, "prepared.tmp");
        public string Target => Path.Combine(Root, "store.bin");
        public Capture Log { get; }
        public Store()
        {
            File.WriteAllText(Target, "old");
            File.WriteAllText(Source, "new");
            Log = new Capture(Root);
            Trace.Listeners.Add(Log);
        }
        public void AssertLog(string operation, string status, int attempt, string outcome)
            => Assert.Contains(Log.Lines, line => line.Contains($"operation={operation} ") &&
                line.Contains($"status={status} ") && line.Contains($"path=\"{Target}\"") &&
                line.Contains($"attempt={attempt}/3 outcome={outcome}") &&
                (operation != "rename" || line.Contains($"source=\"{Source}\"")));
        public void Dispose()
        {
            Trace.Listeners.Remove(Log);
            Log.Dispose();
            if (File.Exists(Target)) File.SetAttributes(Target, FileAttributes.Normal);
            File.Delete(Source);
            File.Delete(Target);
            Directory.Delete(Root);
        }
    }

    private sealed class Capture(string root) : TraceListener
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.Contains(root, StringComparison.Ordinal) == true) Lines.Enqueue(message);
        }
    }
}
