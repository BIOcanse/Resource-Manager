using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

internal static class TrustedAccessEndpointTestAuthentication
{
    public static AddedCredential Add(IServiceCollection services, params string[] scopes)
    {
        var fixture = TrustedAccessTestFactory.Create();
        var key = fixture.Owner.CreateAsync(new AccessKeyCreateRequest(
            "endpoint fixture", AccessKeyPurposes.Control, scopes), CancellationToken.None).GetAwaiter().GetResult();
        services.AddSingleton<ITrustedAccessKeyService>(_ => fixture.Owner);
        services.AddSingleton<ILoopbackApiAdministratorReader>(new NonAdministrator());
        services.AddSingleton(new Credential(key.ApiKey));
        return new AddedCredential(fixture.Owner, key);
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        LoopbackApiPipelineConfigurator.Compile(false).ConfigureApplication(app);
        return app;
    }

    public static HttpClient Client(WebApplication app)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        return Apply(client, app.Services);
    }

    public static HttpClient Apply(HttpClient client, IServiceProvider services)
    {
        client.DefaultRequestHeaders.Add(TrustedAccessHeaders.KeyHeader,
            services.GetRequiredService<Credential>().ApiKey);
        return client;
    }

    private sealed record Credential(string ApiKey);

    public sealed record AddedCredential(ITrustedAccessKeyService Owner, AccessKeyCreated Key)
    {
        public string ApiKey => Key.ApiKey;
        public string CredentialId => Key.Credential.Id;
    }

    private sealed class NonAdministrator : ILoopbackApiAdministratorReader
    {
        public bool IsAdministrator(HttpContext context) => false;
    }
}
