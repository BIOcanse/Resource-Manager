using ResourceManager.NativeUi.SystemIntegration;

namespace Resource_Manager_APP.Tests;

public sealed class NativeProcessCreationTimeReaderTests
{
    [Fact]
    public void SuccessfulQueryClosesHandleOnceAndReturnsCreationTime()
    {
        var calls = new FakeNativeCalls
        {
            Handle = new IntPtr(41),
            ReadResult = true,
            CreationTime = 123456789
        };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Success, result.Status);
        Assert.Equal<long?>(123456789L, result.CreationFileTimeUtc);
        Assert.Equal(0x1000U, calls.DesiredAccess);
        Assert.Equal(77U, calls.ProcessId);
        Assert.Equal(1, calls.ReadCount);
        Assert.Equal(1, calls.CloseCount);
        Assert.False(result.HandleCloseFailed);
    }

    [Fact]
    public void AccessDeniedUsesUnavailableResultWithoutClosingInvalidHandle()
    {
        var calls = new FakeNativeCalls { LastError = 5 };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Unavailable, result.Status);
        Assert.Equal(5, result.Win32Error.GetValueOrDefault());
        Assert.Equal(0, calls.ReadCount);
        Assert.Equal(0, calls.CloseCount);
    }

    [Fact]
    public void MissingProcessIsSkippedWithoutClosingInvalidHandle()
    {
        var calls = new FakeNativeCalls { LastError = 87 };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Exited, result.Status);
        Assert.Equal(87, result.Win32Error.GetValueOrDefault());
        Assert.Equal(0, calls.CloseCount);
    }

    [Fact]
    public void QueryFailureClosesOpenedHandleExactlyOnce()
    {
        var calls = new FakeNativeCalls
        {
            Handle = new IntPtr(41),
            ReadResult = false,
            LastError = 5
        };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Unavailable, result.Status);
        Assert.Equal(1, calls.CloseCount);
        Assert.False(result.HandleCloseFailed);
    }

    [Fact]
    public void QueryExceptionStillClosesOpenedHandleExactlyOnce()
    {
        var expected = new InvalidOperationException("simulated query failure");
        var calls = new FakeNativeCalls
        {
            Handle = new IntPtr(41),
            ReadException = expected
        };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Unavailable, result.Status);
        Assert.Same(expected, result.Error);
        Assert.Equal(1, calls.CloseCount);
    }

    [Fact]
    public void CloseFailureMakesCreationTimeUnavailableAndKeepsDiagnostic()
    {
        var calls = new FakeNativeCalls
        {
            Handle = new IntPtr(41),
            ReadResult = true,
            CreationTime = 123456789,
            CloseResult = false,
            LastError = 6
        };

        var result = NativeProcessCreationTimeReader.Read(77, calls);

        Assert.Equal(ProcessCreationTimeReadStatus.Unavailable, result.Status);
        Assert.Null(result.CreationFileTimeUtc);
        Assert.Equal(6, result.Win32Error.GetValueOrDefault());
        Assert.True(result.HandleCloseFailed);
        Assert.Equal(1, calls.CloseCount);
    }

    private sealed class FakeNativeCalls : IProcessCreationTimeNativeCalls
    {
        public IntPtr Handle { get; init; }
        public bool ReadResult { get; init; }
        public long CreationTime { get; init; }
        public int LastError { get; init; }
        public bool CloseResult { get; init; } = true;
        public Exception? ReadException { get; init; }
        public uint DesiredAccess { get; private set; }
        public uint ProcessId { get; private set; }
        public int ReadCount { get; private set; }
        public int CloseCount { get; private set; }

        public IntPtr OpenProcess(uint desiredAccess, uint processId)
        {
            DesiredAccess = desiredAccess;
            ProcessId = processId;
            return Handle;
        }

        public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
        {
            ReadCount++;
            if (ReadException is not null)
                throw ReadException;
            creationFileTimeUtc = CreationTime;
            return ReadResult;
        }

        public bool CloseHandle(IntPtr handle)
        {
            CloseCount++;
            return CloseResult;
        }

        public int GetLastError() => LastError;
    }
}
