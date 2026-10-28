using Microsoft.AspNetCore.Routing;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceInformationRouteCompositionTests
{
    [Theory]
    [InlineData("/api/device-topology/state")]
    [InlineData("/api/device-topology/fields")]
    [InlineData("/api/device-topology/acpi/fields")]
    [InlineData("/api/device-topology/network-endpoints/fields")]
    public async Task HttpRoutesResolveTheRegisteredOwnerContract(string path)
    {
        var errors = new Errors();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(errors);
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read");
        builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(new Owner());
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapDeviceTopologyEndpoints",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        try
        {
            using var client = TrustedAccessEndpointTestAuthentication.Client(app);
            using var response = await client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode,
                $"GET {path}: {(int)response.StatusCode}\n{string.Join("\n", errors.Exceptions)}");
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task MetricFieldRouteTableBindsDependenciesAsServicesWithoutRequiringUnrelatedOwners()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<HardwareMetricCatalogState>();
        builder.Services.AddSingleton<IMetricSampler>(_ => throw new InvalidOperationException("Route discovery must not sample."));
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapMetricFieldEndpoints",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        Assert.NotEmpty(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints));
    }
    private sealed class Owner : IDeviceTopologySnapshotProvider
    {
        public DeviceTopologySnapshotState ReadState() => DeviceTopologySnapshotState.Warming;
        public async IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return DeviceTopologySnapshotState.Warming with { LastAttemptAt = DateTimeOffset.UtcNow };
        }
        public IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationChangesAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("This fixture does not subscribe to all information fields.");
        public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeNetworkEndpointsAsync(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token, bool includeCurrent = true)
        {
            Assert.False(includeCurrent);
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return DeviceInformationSnapshot.Empty;
        }
        public IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeChangesAsync(string id, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("A single fetch must use sample completion.");
        public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return DeviceInformationSnapshot.Empty;
        }
    }

    private sealed class Errors : ILoggerProvider
    {
        public ConcurrentQueue<string> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(Errors owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is not null) owner.Exceptions.Enqueue(exception.ToString());
            }
        }
    }
}