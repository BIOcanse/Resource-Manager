using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAdapterLeaseGrantCompilerTests
{
    [Fact]
    public async Task UnknownAdapterCanReceiveOnlyRegistrationCapability()
    {
        var caller = CreateCaller();
        var compiler = new TrustedAdapterLeaseGrantCompiler(
            new StaticCatalog(null));

        var grant = await compiler.CompileAsync(
            caller,
            CreateAssertion(TrustedAdapterCapability.Register),
            CancellationToken.None);

        Assert.Equal(TrustedAdapterCapability.Register, grant.GrantedCapabilities);
        Assert.Equal(
            Path.GetDirectoryName(caller.CanonicalExecutablePath),
            Assert.Single(grant.Claims.CanonicalProgramRootPaths));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await compiler.CompileAsync(
                caller,
                CreateAssertion(
                    TrustedAdapterCapability.Register
                    | TrustedAdapterCapability.DispatchPolicy),
                CancellationToken.None));
    }

    [Fact]
    public async Task TrustedRegistrationOwnsClaimsAndCapabilityCeiling()
    {
        var caller = CreateCaller();
        var trustedClaims = new TrustedAdapterInstanceClaims(
            "tests.adapter",
            "tests.application",
            "Trusted Display Name",
            [Path.GetDirectoryName(caller.CanonicalExecutablePath)!]);
        var registration = new TrustedAdapterRegistration(
            trustedClaims,
            caller.CanonicalExecutablePath,
            caller.ExecutableFileIdentity,
            TrustedAdapterCapability.Register
                | TrustedAdapterCapability.DispatchPolicy);
        var compiler = new TrustedAdapterLeaseGrantCompiler(
            new StaticCatalog(registration));

        var grant = await compiler.CompileAsync(
            caller,
            CreateAssertion(
                TrustedAdapterCapability.Register
                | TrustedAdapterCapability.DispatchPolicy),
            CancellationToken.None);

        Assert.Equal(trustedClaims.DisplayName, grant.Claims.DisplayName);
        Assert.Equal(
            TrustedAdapterCapability.Register
                | TrustedAdapterCapability.DispatchPolicy,
            grant.GrantedCapabilities);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await compiler.CompileAsync(
                caller,
                CreateAssertion(
                    TrustedAdapterCapability.Register
                    | (TrustedAdapterCapability)(1UL << 63)),
                CancellationToken.None));

        var disabledCeiling = registration with
        {
            CapabilityCeiling = registration.CapabilityCeiling
                | (TrustedAdapterCapability)(1UL << 63)
        };
        var disabledCompiler = new TrustedAdapterLeaseGrantCompiler(
            new StaticCatalog(disabledCeiling));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await disabledCompiler.CompileAsync(
                caller,
                CreateAssertion(TrustedAdapterCapability.Register),
                CancellationToken.None));
    }

    [Fact]
    public async Task TrustedRegistrationRejectsChangedExecutableIdentity()
    {
        var caller = CreateCaller();
        var registration = new TrustedAdapterRegistration(
            CreateAssertion(TrustedAdapterCapability.Register).Claims,
            caller.CanonicalExecutablePath,
            caller.ExecutableFileIdentity with { FileIdLow = 999 },
            TrustedAdapterCapability.Register);
        var compiler = new TrustedAdapterLeaseGrantCompiler(
            new StaticCatalog(registration));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await compiler.CompileAsync(
                caller,
                CreateAssertion(TrustedAdapterCapability.Register),
                CancellationToken.None));
    }

    private static TrustedAdapterLeaseAssertion CreateAssertion(
        TrustedAdapterCapability requestedCapabilities)
        => new(
            new TrustedAdapterInstanceClaims(
                "tests.adapter",
                "tests.application",
                "Wire Display Name",
                [AppContext.BaseDirectory]),
            requestedCapabilities);

    private static TrustedAdapterCallerIdentity CreateCaller()
    {
        var host = new HostManagerRuntimeIdentity();
        return new TrustedAdapterCallerIdentity(
            host.InstanceId,
            TransportConnectionId: 100,
            host.ProcessId,
            host.ProcessCreatedUtcTicks,
            "S-1-5-21-1000",
            WindowsSessionId: 1,
            AuthenticationIdLuid: 2,
            IntegrityLevelRid: 0x2000,
            IsElevated: false,
            Path.GetFullPath(Environment.ProcessPath!),
            new AdapterExecutableFileIdentity(10, 20, 30),
            NativeAdapterInstanceLeaseSession.MonotonicNow());
    }

    private sealed class StaticCatalog(TrustedAdapterRegistration? registration)
        : ITrustedAdapterRegistrationCatalog
    {
        public ValueTask<TrustedAdapterRegistration?> FindAsync(
            string adapterId,
            string appId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(registration);
        }
    }
}
