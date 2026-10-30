using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal interface IOracleKernelEtwSessionBroker
{
    IOracleKernelEtwSubscription Acquire(OracleKernelEtwSubscriptionRequest request);
    KernelEtwSessionSnapshot GetSnapshot();
}

internal interface IOracleKernelEtwSubscription : IDisposable
{
    KernelEtwSessionSnapshot GetSnapshot();
}

internal sealed record OracleKernelEtwSubscriptionRequest(string Id, KernelTraceEventParser.Keywords Keywords,
    Action<OracleKernelEtwEventSource, KernelEtwSessionContext> Bind);

internal sealed record OracleKernelEtwSubscriptionRegistration(long LeaseId, string Id, KernelTraceEventParser.Keywords Keywords,
    Action<OracleKernelEtwEventSource, KernelEtwSessionContext> Bind);
