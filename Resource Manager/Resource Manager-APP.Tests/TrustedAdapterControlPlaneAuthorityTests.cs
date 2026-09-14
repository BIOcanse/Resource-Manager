using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAdapterControlPlaneAuthorityTests
{
    [Fact]
    public void GenerationExhaustionFailsClosedWithoutWrappingToAnOldAuthority()
    {
        Assert.False(TrustedAdapterControlPlaneAuthority.TryNextGeneration(0, out var zero));
        Assert.Equal(0UL, zero);
        Assert.False(TrustedAdapterControlPlaneAuthority.TryNextGeneration(
            ulong.MaxValue,
            out var exhausted));
        Assert.Equal(0UL, exhausted);
        Assert.True(TrustedAdapterControlPlaneAuthority.TryNextGeneration(41, out var next));
        Assert.Equal(42UL, next);
    }

    [Fact]
    public void LeaseRenewalRequiresExactTransportAttestedIdentity()
    {
        var host = new HostManagerRuntimeIdentity();
        using var concrete = CreateAuthority(host);
        ITrustedAdapterInstanceLeaseAuthority authority = concrete;
        var caller = CreateCaller(host, connectionId: 41);
        var lease = authority.Issue(
            caller,
            new TrustedAdapterLeaseGrant(
                CreateClaims(),
                TrustedAdapterCapability.Register | TrustedAdapterCapability.DispatchPolicy));

        Assert.False(authority.TryRenew(
            lease.LeaseId,
            lease.HeartbeatGeneration,
            caller with
            {
                TransportConnectionId = 42,
                AttestedAtMonotonicTimestamp = NativeAdapterInstanceLeaseSession.MonotonicNow()
            },
            out _));
        Assert.True(authority.TryRenew(
            lease.LeaseId,
            lease.HeartbeatGeneration,
            caller with
            {
                AttestedAtMonotonicTimestamp = NativeAdapterInstanceLeaseSession.MonotonicNow()
            },
            out var renewed));
        Assert.Equal(2UL, renewed!.HeartbeatGeneration);
        Assert.True(authority.TryResolve(
            lease.LeaseId,
            renewed.HeartbeatGeneration,
            TrustedAdapterCapability.DispatchPolicy,
            out _));
        Assert.False(authority.TryResolve(
            lease.LeaseId,
            lease.HeartbeatGeneration,
            TrustedAdapterCapability.Register,
            out _));
    }

    private static TrustedAdapterCallerIdentity CreateCaller(
        HostManagerRuntimeIdentity host,
        ulong connectionId)
        => new(
            host.InstanceId,
            connectionId,
            host.ProcessId,
            host.ProcessCreatedUtcTicks,
            "S-1-5-21-1000",
            WindowsSessionId: 1,
            AuthenticationIdLuid: 2,
            IntegrityLevelRid: 0x2000,
            IsElevated: false,
            Path.GetFullPath(Environment.ProcessPath!),
            new AdapterExecutableFileIdentity(1, 2, 3),
            NativeAdapterInstanceLeaseSession.MonotonicNow());

    private static TrustedAdapterControlPlaneAuthority CreateAuthority(
        HostManagerRuntimeIdentity host)
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            buffer.ToArray(),
            "test/default.json");
        var hostManager = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(),
            1,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var provider = new TestRuntimePlanProvider(
            CompiledRuntimePlan.Default with { HostManager = hostManager });
        return new TrustedAdapterControlPlaneAuthority(host, provider);
    }

    private static TrustedAdapterInstanceClaims CreateClaims()
        => new(
            "tests.adapter",
            "tests.application",
            "Tests Adapter",
            [AppContext.BaseDirectory]);

    private sealed class TestRuntimePlanProvider(CompiledRuntimePlan current)
        : ResourceManager.App.Application.RuntimeSpecialization.IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = current;

        public ResourceManager.App.Application.RuntimeSpecialization.RuntimePlanPublicationLease
            AcquirePublicationLease()
            => ResourceManager.App.Application.RuntimeSpecialization
                .RuntimePlanPublicationLease.CreateUntracked(Current, 1);

        public ResourceManager.App.Application.RuntimeSpecialization
            .RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            Current = plan;
            return new ResourceManager.App.Application.RuntimeSpecialization
                .RuntimePlanPublicationResult(plan, 1, []);
        }
    }
}
