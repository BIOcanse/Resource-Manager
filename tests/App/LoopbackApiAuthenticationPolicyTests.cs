using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class LoopbackApiAuthenticationPolicyTests
{
    private const string ExpectedToken = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void AuthenticationEnabled_AcceptsOnlyTheCurrentInstanceToken()
    {
        Assert.True(LoopbackApiAuthenticationPolicy.IsAuthorized(
            ExpectedToken,
            ExpectedToken));
        Assert.False(LoopbackApiAuthenticationPolicy.IsAuthorized(
            ExpectedToken,
            "0123456789ABCDEF0123456789ABCDE0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AuthenticationEnabled_RejectsMissingTokens(string? suppliedToken)
    {
        Assert.False(LoopbackApiAuthenticationPolicy.IsAuthorized(
            ExpectedToken,
            suppliedToken));
    }

    [Fact]
    public void StandardUserPipeline_RegistersFixedTokenAuthenticationServices()
    {
        var pipeline = LoopbackApiPipelineConfigurator.Compile(processIsAdministrator: false);
        var services = new ServiceCollection();

        pipeline.ConfigureServices(services);

        Assert.Equal(LoopbackApiPipelineMode.StandardUser, pipeline.Mode);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LoopbackApiAccessToken)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AdministratorPipeline_RegistersFixedTokenAuthenticationServices()
    {
        var pipeline = LoopbackApiPipelineConfigurator.Compile(processIsAdministrator: true);
        var services = new ServiceCollection();

        pipeline.ConfigureServices(services);

        Assert.Equal(LoopbackApiPipelineMode.Administrator, pipeline.Mode);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LoopbackApiAccessToken)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AuthenticationMiddleware_BindsAuthenticationServicesForBothModes()
    {
        var constructor = Assert.Single(
            typeof(LoopbackApiAuthenticationMiddleware).GetConstructors());
        Assert.Equal(
            [
                typeof(RequestDelegate),
                typeof(LoopbackApiAccessToken),
                typeof(IRuntimePlanProvider),
                typeof(ILoopbackApiAdministratorReader)
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var invoke = typeof(LoopbackApiAuthenticationMiddleware).GetMethod("InvokeAsync");
        Assert.NotNull(invoke);
        Assert.Equal(
            [typeof(HttpContext)],
            invoke!.GetParameters().Select(parameter => parameter.ParameterType));

        foreach (var processIsAdministrator in new[] { false, true })
        {
            var services = new ServiceCollection();
            LoopbackApiPipelineConfigurator
                .Compile(processIsAdministrator)
                .ConfigureServices(services);
            Assert.Contains(services, descriptor =>
                descriptor.ServiceType == typeof(LoopbackApiAccessToken));
        }
    }

    [Fact]
    public void SessionProof_ProvesTokenPossessionWithoutExposingTheToken()
    {
        var challenge = new string('A', LoopbackSessionProofContract.ChallengeHexLength);
        var proof = LoopbackSessionProofContract.ComputeProof(ExpectedToken, challenge);

        Assert.Equal(LoopbackSessionProofContract.ProofHexLength, proof.Length);
        Assert.DoesNotContain(ExpectedToken, proof, StringComparison.Ordinal);
        Assert.True(LoopbackSessionProofContract.VerifyProof(
            ExpectedToken,
            challenge,
            proof));
        Assert.False(LoopbackSessionProofContract.VerifyProof(
            ExpectedToken,
            new string('B', LoopbackSessionProofContract.ChallengeHexLength),
            proof));
        Assert.False(LoopbackSessionProofContract.VerifyProof(
            ExpectedToken + "0",
            challenge,
            proof));
    }
}
