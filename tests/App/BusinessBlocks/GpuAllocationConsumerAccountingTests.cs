using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationConsumerAccountingTests
{
    [Fact]
    public void CollectorFactoryPassesObserverAndSessionToTheGpuConsumerRuntime()
    {
        var observer = new RecordingObserver();
        using var collector = new GpuAllocationCollector(
            NullLogger<GpuAllocationCollector>.Instance,
            new ConfigurationBuilder().Build(),
            observer);

        var factory = Assert.IsType<TraceEventGpuAllocationSessionFactory>(ReadField(collector, "sessionFactory"));
        var session = (TraceEventSession)RuntimeHelpers.GetUninitializedObject(typeof(TraceEventSession));
        GC.SuppressFinalize(session); // This reflection-only fixture owns no native session to release.
        var gpuSession = factory.WrapSession(session);
        var processing = ReadField(gpuSession, "processing");
        var runtimeFields = processing.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Contains(runtimeFields, field => field.FieldType == typeof(TraceEventSession)
            && ReferenceEquals(field.GetValue(processing), session));
        Assert.Contains(runtimeFields, field => field.FieldType == typeof(IEtwConsumerThreadObserver)
            && ReferenceEquals(field.GetValue(processing), observer));
        Assert.Contains(runtimeFields, field => field.FieldType == typeof(string)
            && Equals(field.GetValue(processing), "gpu-allocation-etw"));
    }

    private static object ReadField(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Expected private field '{name}' on {instance.GetType().FullName}.");

    private sealed class RecordingObserver : IEtwConsumerThreadObserver
    {
        public IDisposable RegisterCurrentThread(string blockId, string sessionName) => EmptyDisposable.Instance;

        public void ReportFailure(string blockId, Exception error) { }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
