using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSessionKeywordControlTests
{
    [Fact]
    public void UpdateUsesTheFullQueriedHandleAndTheCurrentKeywordUnion()
    {
        var native = new FakeNativeControl();
        var control = new KernelEtwSessionKeywordControl(native.Identity.Name, native);
        var union = KernelTraceEventParser.Keywords.ContextSwitch
            | KernelTraceEventParser.Keywords.NetworkTCPIP;

        control.UpdateKeywords(union);
        control.UpdateKeywords(KernelTraceEventParser.Keywords.NetworkTCPIP);

        Assert.Equal(3, native.Queries.Count);
        Assert.All(native.Queries, name => Assert.Equal(native.Identity.Name, name));
        Assert.Equal(
            new[]
            {
                (native.Identity.Handle, WithMetadata(union)),
                (native.Identity.Handle, WithMetadata(KernelTraceEventParser.Keywords.NetworkTCPIP))
            },
            native.Updates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacedSessionIsRejectedBeforeAnyUpdate(bool replaceHandle)
    {
        var native = new FakeNativeControl();
        var control = new KernelEtwSessionKeywordControl(native.Identity.Name, native);
        native.Identity = replaceHandle
            ? native.Identity with { Handle = native.Identity.Handle + 1 }
            : native.Identity with { Guid = Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() => control.UpdateKeywords(KernelTraceEventParser.Keywords.Process));

        Assert.Empty(native.Updates);
        Assert.Equal(2, native.Queries.Count);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("null-name")]
    [InlineData("zero-handle")]
    [InlineData("invalid-handle")]
    [InlineData("empty-guid")]
    [InlineData("not-real-time")]
    [InlineData("not-system-logger")]
    public void InvalidQueriedIdentityIsRejectedOnCaptureAndBeforeUpdate(string defect)
    {
        var native = new FakeNativeControl();
        var expectedName = native.Identity.Name;
        var validIdentity = native.Identity;
        var invalidIdentity = defect switch
        {
            "name" => validIdentity with { Name = "another-session" },
            "null-name" => validIdentity with { Name = null! },
            "zero-handle" => validIdentity with { Handle = 0 },
            "invalid-handle" => validIdentity with { Handle = ulong.MaxValue },
            "empty-guid" => validIdentity with { Guid = Guid.Empty },
            "not-real-time" => validIdentity with { LogFileMode = KernelEtwSessionKeywordControl.SystemLoggerMode },
            "not-system-logger" => validIdentity with { LogFileMode = KernelEtwSessionKeywordControl.RealTimeMode },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        native.Identity = invalidIdentity;
        Assert.Throws<InvalidOperationException>(() => new KernelEtwSessionKeywordControl(expectedName, native));
        native.Identity = validIdentity;
        var control = new KernelEtwSessionKeywordControl(expectedName, native);
        native.Identity = invalidIdentity;

        Assert.Throws<InvalidOperationException>(() => control.UpdateKeywords(KernelTraceEventParser.Keywords.Process));
        Assert.Empty(native.Updates);
    }

    [Fact]
    public void ForeignProcessIncarnationIsRejectedBeforeNativeQuery()
    {
        var native = new FakeNativeControl();
        var owner = ResourceManagerEtwSessionNames.CurrentOwner;
        var foreignName = ResourceManagerEtwSessionNames.Create(
            ResourceManagerEtwSessionNames.KernelPrefix,
            owner with { ProcessStartUtcTicks = owner.ProcessStartUtcTicks!.Value + 1 });

        Assert.Throws<ArgumentException>(() => new KernelEtwSessionKeywordControl(foreignName, native));
        Assert.Empty(native.Queries);
        Assert.Empty(native.Updates);
    }

    [Theory]
    [InlineData(KernelTraceEventParser.Keywords.None)]
    [InlineData(KernelTraceEventParser.Keywords.ReferenceSet)]
    [InlineData(KernelTraceEventParser.Keywords.PMCProfile)]
    public void UnsupportedUpdatesDoNotReachTheNativeController(KernelTraceEventParser.Keywords keywords)
    {
        var native = new FakeNativeControl();
        var control = new KernelEtwSessionKeywordControl(native.Identity.Name, native);

        if (keywords == KernelTraceEventParser.Keywords.None)
            Assert.Throws<ArgumentOutOfRangeException>(() => control.UpdateKeywords(keywords));
        else
            Assert.Throws<NotSupportedException>(() => control.UpdateKeywords(keywords));

        Assert.Single(native.Queries);
        Assert.Empty(native.Updates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeFailureIsPreservedWithoutAnImplicitRetry(bool failQuery)
    {
        var native = new FakeNativeControl();
        var control = new KernelEtwSessionKeywordControl(native.Identity.Name, native);
        var failure = new InvalidOperationException("fixture native failure");
        if (failQuery) native.QueryFailure = failure;
        else native.UpdateFailure = failure;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => control.UpdateKeywords(KernelTraceEventParser.Keywords.Process)));
        Assert.Equal(2, native.Queries.Count);
        Assert.Equal(failQuery ? 0 : 1, native.Updates.Count);
    }

    private static ulong WithMetadata(KernelTraceEventParser.Keywords keywords)
        => (uint)(keywords | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);

    private sealed class FakeNativeControl : IKernelEtwNativeSessionControl
    {
        public KernelEtwNativeSessionIdentity Identity { get; set; } = new(
            ResourceManagerEtwSessionNames.KernelSessionName,
            0x12345678abcdef01UL,
            Guid.NewGuid(),
            KernelEtwSessionKeywordControl.RealTimeMode | KernelEtwSessionKeywordControl.SystemLoggerMode);
        public List<string> Queries { get; } = [];
        public List<(ulong Handle, ulong Flags)> Updates { get; } = [];
        public Exception? QueryFailure { get; set; }
        public Exception? UpdateFailure { get; set; }

        public KernelEtwNativeSessionIdentity Query(string sessionName)
        {
            Queries.Add(sessionName);
            if (QueryFailure is not null) throw QueryFailure;
            return Identity;
        }

        public void SetSystemTraceFlags(ulong sessionHandle, ulong flags)
        {
            Updates.Add((sessionHandle, flags));
            if (UpdateFailure is not null) throw UpdateFailure;
        }
    }
}
