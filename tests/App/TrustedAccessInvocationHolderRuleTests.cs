using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessInvocationHolderRuleTests
{
    [Fact]
    public async Task KeyCarryingAnAdministratorHolderScopeIsRefusedFromAnUnverifiedCallerAndNamesTheKey()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var resolver = new Resolver();
        using var host = new TrustedAccessInvocationHostedService(fixture.Owner, resolver, resolver, null!,
            new HostManagerRuntimeIdentity(), NullLogger<TrustedAccessInvocationHostedService>.Instance,
            "ResourceManager.Tests.HolderRule." + Guid.NewGuid().ToString("N"));
        var holderScopes = AccessKeyScopes.Definitions
            .Where(static scope => scope.RequiresAdministratorHolder)
            .Select(static scope => scope.Name)
            .ToArray();
        Assert.NotEmpty(holderScopes);

        foreach (var holderScope in holderScopes)
        {
            var key = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
                "resolve with " + holderScope, AccessKeyPurposes.Control,
                [AccessKeyScopes.GpuResolve, holderScope]), CancellationToken.None);
            string? acceptedId = null;

            var refused = await host.InvokeAsync(new(TrustedAccessInvocationContract.GpuResolveOperation,
                    key.ApiKey, @"D:\holder.exe"), UnverifiedCaller(), verifiedAdministrator: false,
                CancellationToken.None, id => acceptedId = id);

            Assert.Equal(TrustedAccessInvocationContract.Forbidden, refused.Status);
            Assert.Null(refused.Payload);
            Assert.Equal(0, resolver.ResolveCalls);
            // The completion log line names the key behind the holder refusal.
            Assert.Equal(key.Credential.Id, acceptedId);
        }

        var resolveOnly = await fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "resolve only", AccessKeyPurposes.Control, [AccessKeyScopes.GpuResolve]), CancellationToken.None);
        var resolved = await host.InvokeAsync(new(TrustedAccessInvocationContract.GpuResolveOperation,
                resolveOnly.ApiKey, @"D:\resolve-only.exe"), UnverifiedCaller(), verifiedAdministrator: false,
            CancellationToken.None, _ => { });
        Assert.Equal(TrustedAccessInvocationContract.Success, resolved.Status);
        Assert.Equal(1, resolver.ResolveCalls);
    }

    // The attested identity does not match the running process (creation time and SID differ), so the
    // administrator re-read for keyed calls cannot verify it, whether or not the test host is elevated.
    private static TrustedAdapterCallerIdentity UnverifiedCaller()
    {
        var host = new HostManagerRuntimeIdentity();
        return new TrustedAdapterCallerIdentity(
            host.InstanceId,
            TransportConnectionId: 1,
            Environment.ProcessId,
            ProcessCreatedUtcTicks: 1,
            "S-1-5-21-1000",
            WindowsSessionId: 1,
            AuthenticationIdLuid: 2,
            IntegrityLevelRid: 0x2000,
            IsElevated: false,
            Path.GetFullPath(Environment.ProcessPath!),
            new AdapterExecutableFileIdentity(10, 20, 30),
            AttestedAtMonotonicTimestamp: 0);
    }

    private sealed class Resolver : IGpuStartupPlacementResolver, IGpuLaunchExecutionReportStore
    {
        internal int ResolveCalls;

        public Task<GpuStartupPlacementDecision> ResolveAsync(
            GpuStartupPlacementRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCalls);
            return Task.FromResult(new GpuStartupPlacementDecision(GpuStartupPlacementDecisionKinds.PassThrough,
                "probe-resolved", "holder rule resolver", request.ExecutablePath, "fixture-software", "fixture-process",
                GpuPlacementTargets.IntegratedGpu, null, null, null, null, []));
        }

        public Task<GpuLaunchExecutionReport> RecordAsync(GpuLaunchExecutionReport report, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<GpuLaunchExecutionReport>> GetLatestAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
