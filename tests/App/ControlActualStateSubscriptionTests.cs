using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class ControlActualStateSubscriptionTests
{
    [Fact]
    public async Task ChannelWorkersOwnDemandOnlyWhileRunningAndPublishTheCurrentValue()
    {
        var owner = new CountingOwner();
        using var channel = await CompileAsync(owner);
        var subscriptions = channel.Subscriptions;
        Assert.Equal(0, owner.Active);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        using var writer = new ResourceManagerEndpointRouteBuilderExtensions.FrontendSubscriptionChannelWriter(
            context.Response, new(JsonSerializerDefaults.Web));
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = subscriptions[0].RunAsync(writer, firstCancellation.Token);
        var second = subscriptions[1].RunAsync(writer, secondCancellation.Token);
        Assert.Equal(2, owner.Active);
        Assert.True(owner.Reads >= 2);
        var frames = System.Text.Encoding.UTF8.GetString(body.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(frames.Length >= 2);
        foreach (var frame in frames)
        {
            using var json = JsonDocument.Parse(frame);
            Assert.Equal(42, json.RootElement.GetProperty("value").GetProperty("values")[0].GetProperty("number").GetDouble());
        }

        await firstCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, owner.Active);
        await secondCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, owner.Active);
        Assert.Equal(2, owner.Acquired);
        Assert.Equal(2, owner.Released);
    }

    [Fact]
    public async Task ChannelTransportFailureReleasesTheActualStateDemand()
    {
        var owner = new CountingOwner();
        using var channel = await CompileAsync(owner);
        var subscription = channel.Subscriptions[0];
        var context = new DefaultHttpContext();
        using var body = new FailingStream();
        context.Response.Body = body;
        using var writer = new ResourceManagerEndpointRouteBuilderExtensions.FrontendSubscriptionChannelWriter(
            context.Response, new(JsonSerializerDefaults.Web));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => subscription.RunAsync(writer, CancellationToken.None));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(1, owner.Acquired);
        Assert.Equal(1, owner.Released);
        Assert.Equal(0, owner.Active);
    }

    [Fact]
    public async Task AlreadyCanceledChannelDoesNotAcquireDemandAndReadOnlyOwnerStaysEmpty()
    {
        var owner = new CountingOwner();
        using var channel = await CompileAsync(owner);
        var subscription = channel.Subscriptions[0];
        var context = new DefaultHttpContext();
        using var writer = new ResourceManagerEndpointRouteBuilderExtensions.FrontendSubscriptionChannelWriter(
            context.Response, new(JsonSerializerDefaults.Web));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.RunAsync(writer, new(true)));
        Assert.Equal(0, owner.Acquired);
        Assert.Equal(0, owner.Reads);

        var readOnly = new EmptyControlActualStateOwner();
        using var demand = readOnly.AcquireSubscription();
        Assert.Same(ControlActualState.Empty, readOnly.Current);
    }

    private static async Task<CompiledChannel> CompileAsync(
        IControlActualStateOwner owner)
    {
        var registrations = new ServiceCollection().AddSingleton(owner).AddLogging();
        TrustedAccessEndpointTestAuthentication.Add(registrations, AccessKeyScopes.Read);
        var services = registrations.BuildServiceProvider();
        using var client = TrustedAccessEndpointTestAuthentication.Apply(new HttpClient(), services);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers[TrustedAccessHeaders.KeyHeader] = client.DefaultRequestHeaders
            .GetValues(TrustedAccessHeaders.KeyHeader).Single();
        var request = new FrontendSubscriptionChannelRequest(1,
            [new("first", "/api/control/actual/subscribe?intervalMs=60000"),
             new("second", "/api/control/actual/subscribe?intervalMs=60000")]);
        try
        {
            var compilation = await ResourceManagerEndpointRouteBuilderExtensions
                .CompileSubscriptionChannelRequestAsync(request, context);
            Assert.True(compilation.Succeeded, compilation.Error);
            return new CompiledChannel(services, compilation.Subscriptions);
        }
        catch { services.Dispose(); throw; }
    }

    private sealed record CompiledChannel(ServiceProvider Services,
        IReadOnlyList<ResourceManagerEndpointRouteBuilderExtensions.CompiledFrontendSubscription> Subscriptions) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private sealed class CountingOwner : IControlActualStateOwner
    {
        public int Active;
        public int Acquired;
        public int Released;
        public int Reads;
        public ControlActualState Current
        {
            get
            {
                Interlocked.Increment(ref Reads);
                return new([new("device", "power", 42)], DateTimeOffset.UnixEpoch);
            }
        }
        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref Acquired);
            Interlocked.Increment(ref Active);
            return new Lease(this);
        }
        private sealed class Lease(CountingOwner owner) : IDisposable
        {
            private CountingOwner? current = owner;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref current, null) is not { } active) return;
                Interlocked.Increment(ref active.Released);
                Interlocked.Decrement(ref active.Active);
            }
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Transport closed."));
    }
}
