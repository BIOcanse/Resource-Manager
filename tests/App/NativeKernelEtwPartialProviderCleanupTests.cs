using System.ComponentModel;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwPartialProviderCleanupTests
{
    [Fact]
    public void BindingCancellationAfterOwnedProviderStopDoesNotFlushTheEndedSession()
    {
        // Real-machine restart: StopProvider ended the owned session, then
        // CancelBindings flushed it by name and failed with 0x80071069.
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(1000, 1));
        using var hold = resource.Acquire(new("held", KernelTraceEventParser.Keywords.Process,
            static (source, _) => source.ConsumerCounter("ProcessStartGroup")) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        var session = resource.Session;
        var token = session.GetConsumerCounterToken("canceled");
        Assert.NotNull(session.CreateConsumerCounterReader(token, "canceled", KernelEtwNativeOutputs.Residency,
            resource.Generation, "ProcessStartGroup", null));
        var name = ResourceManagerEtwSessionNames.KernelSessionName; // Keyword control accepts only this incarnation's name; never enabled here.
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
        using var provider = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = false };
        var native = new NativeControl(name) { QueryFailure = new Win32Exception(4201, "fixture provider already ended") };
        Field("provider").SetValue(session, provider);
        Field("nativeControl").SetValue(session, native);
        try
        {
            ((IKernelEtwNativeRuntimeOperations)session).StopProvider();
            Assert.True((bool)Field("providerStopped").GetValue(session)!);
            session.ConfigureConsumerAdmission(token, resource.Generation, KernelEtwNativeOutputs.Residency, active: false);
            session.RetireConsumerCounter(token);
            session.UpdateOutputs(KernelEtwNativeOutputs.Residency);
            Assert.Equal(0, native.Stops);
        }
        finally { Field("provider").SetValue(session, null); } // The controlled release never stops a provider.
    }

    [Fact]
    public void PartialEnableCleanupRetriesDeniedNativeStopBeforeMarkingProviderReleased()
    {
        var name = ResourceManagerEtwSessionNames.KernelSessionName; // Unique current process incarnation.
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
        using var provider = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = false };
        var denied = new Win32Exception(5, "fixture partial provider stop denied");
        var native = new NativeControl(name) { StopFailure = denied };
        var session = new NativeKernelEtwSession(new EmptyRegion(), nativeControl: native);
        Field("provider").SetValue(session, provider); // Enable failed before keywordControl could be constructed.
        var operations = (IKernelEtwNativeRuntimeOperations)session;
        try
        {
            Assert.Same(denied, Record.Exception(operations.StopProvider));
            Assert.Equal(1, native.Stops);
            Assert.False((bool)Field("providerStopped").GetValue(session)!);
            native.StopFailure = null;
            operations.StopProvider();
            Assert.Equal(2, native.Stops);
            Assert.True((bool)Field("providerStopped").GetValue(session)!);
            operations.StopProvider();
            Assert.Equal(2, native.Stops);
        }
        finally { ReleaseNeverStartedFixture(session, operations); }
    }

    [Fact]
    public void PartialEnableCleanupAcceptsQueriedAbsenceWithoutCallingNativeStop()
    {
        var name = ResourceManagerEtwSessionNames.KernelSessionName;
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
        using var provider = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = false };
        var native = new NativeControl(name) { QueryFailure = new Win32Exception(4201, "fixture already absent") };
        var session = new NativeKernelEtwSession(new EmptyRegion(), nativeControl: native);
        Field("provider").SetValue(session, provider);
        var operations = (IKernelEtwNativeRuntimeOperations)session;
        try
        {
            operations.StopProvider();
            Assert.Equal(1, native.Queries);
            Assert.Equal(0, native.Stops);
            Assert.True((bool)Field("providerStopped").GetValue(session)!);
            operations.StopProvider();
            Assert.Equal(1, native.Queries);
        }
        finally { ReleaseNeverStartedFixture(session, operations); }
    }

    private static void ReleaseNeverStartedFixture(NativeKernelEtwSession session, IKernelEtwNativeRuntimeOperations operations)
    {
        // This test never enables a provider, opens a consumer, or creates a
        // native owner. Detach the lazy managed object so RED cleanup performs
        // no real session-control call, then release only the empty fixture.
        Field("provider").SetValue(session, null);
        Field("providerStopped").SetValue(session, true);
        operations.CloseTrace();
        session.Dispose();
    }
    private static FieldInfo Field(string name) => typeof(NativeKernelEtwSession)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class EmptyRegion : IKernelEtwRegionLease
    {
        public nint Pointer => 0;
        public nuint Length => 0;
        public void Complete() => throw new InvalidOperationException("No native allocation belongs to this test.");
        public void Dispose() { }
    }
    private sealed class NativeControl(string name) : IKernelEtwNativeSessionControl
    {
        private readonly KernelEtwNativeSessionIdentity identity = new(name, 17, Guid.NewGuid(),
            KernelEtwSessionKeywordControl.RealTimeMode | KernelEtwSessionKeywordControl.SystemLoggerMode);
        internal Exception? StopFailure, QueryFailure;
        internal int Stops, Queries;
        public KernelEtwNativeSessionIdentity Query(string expectedName)
        {
            Assert.Equal(identity.Name, expectedName); Queries++;
            if (QueryFailure is not null) throw QueryFailure;
            return identity;
        }
        public void SetSystemTraceFlags(ulong handle, ulong flags) => throw new InvalidOperationException("No provider enable is permitted.");
        public void StopSession(ulong handle, string expectedName)
        {
            Assert.Equal(identity.Handle, handle); Assert.Equal(identity.Name, expectedName); Stops++;
            if (StopFailure is not null) throw StopFailure;
        }
    }
}
