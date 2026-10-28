using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessInvocationPipeIsolationTests
{
    [Fact]
    public void OmittedNameRetainsTheProductionContractDefault()
    {
        var parameter = typeof(TrustedAccessInvocationHostedService).GetConstructors().Single()
            .GetParameters().Single(value => value.Name == "pipeName");
        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(TrustedAccessInvocationContract.PipeName, parameter.DefaultValue);
    }

    [Fact]
    public async Task ExplicitPrivateNamesCanListenConcurrentlyWithoutTheProductionPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var firstName = $"ResourceManager.ControlDecisionAcceptance.{Guid.NewGuid():N}";
        var secondName = $"ResourceManager.ControlDecisionAcceptance.{Guid.NewGuid():N}";
        Assert.NotEqual(TrustedAccessInvocationContract.PipeName, firstName);
        Assert.NotEqual(TrustedAccessInvocationContract.PipeName, secondName);
        using var first = Create(firstName);
        using var second = Create(secondName);
        await first.StartAsync(default);
        await second.StartAsync(default);
        try
        {
            using var firstClient = new NamedPipeClientStream(".", firstName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var secondClient = new NamedPipeClientStream(".", secondName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(firstClient.ConnectAsync(deadline.Token), secondClient.ConnectAsync(deadline.Token));
            Assert.True(firstClient.IsConnected);
            Assert.True(secondClient.IsConnected);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(first.StopAsync(deadline.Token), second.StopAsync(deadline.Token));
        }
    }

    private static TrustedAccessInvocationHostedService Create(string name) =>
        new(null!, null!, null!, null!, new HostManagerRuntimeIdentity(), NullLogger<TrustedAccessInvocationHostedService>.Instance, name);
}



