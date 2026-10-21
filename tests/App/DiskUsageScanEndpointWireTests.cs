using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageScanEndpointWireTests
{
    [Fact]
    public async Task ScanSubmissionUsesTheOperationWireProjectionForUInt64Fields()
    {
        var timestamp = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        var snapshot = new HostManagerOperationSnapshot(
            "00000000000000000000000000000001",
            "disk-usage.scan",
            "disk-usage:scan",
            "Scan disk usage",
            HostManagerOperationStates.Running,
            ulong.MaxValue,
            ulong.MaxValue - 1,
            1,
            1,
            timestamp,
            timestamp,
            null,
            false,
            new HostManagerOperationProgress(
                ulong.MaxValue,
                50,
                ulong.MaxValue - 1,
                ulong.MaxValue,
                ulong.MaxValue,
                "scan",
                "Working"),
            null,
            null);
        var commands = new RecordingCommandService(snapshot);
        var store = new DiskUsageTreeStore();
        var owner = new DiskUsageResultOwner("S-1-5-21-1001", 4);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IHostManagerOperationCommandService>(commands);
        builder.Services.AddSingleton<IDiskUsageVolumeCatalog>(new EmptyVolumeCatalog());
        builder.Services.AddSingleton<IDiskUsageTreeStore>(store);
        builder.Services.AddSingleton<IOwnedDiskUsageTreeStore>(store);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Items[typeof(TrustedAdapterCallerIdentity)] = Caller(owner);
            await next(context);
        });
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapDiskUsageEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, StartupCapabilitySet.Full]);
        var diskRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/disk-usage/", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, diskRoutes.Length);
        Assert.All(diskRoutes, endpoint =>
            Assert.Null(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>()));

        await app.StartAsync();
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await client.PostAsJsonAsync(
                "/api/disk-usage/scan",
                new DiskUsageScanRequest(
                    DiskUsageScanScopes.AllVolumes,
                    DiskUsageScanModes.Fast,
                    string.Empty));

            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal(HostManagerOperationKinds.DiskUsageScan, commands.Submitted?.Kind);
            var decoded = HostManagerOperationRequestCodec.DecodeDiskUsageScan(
                commands.Submitted!.CanonicalRequest);
            Assert.Equal(owner, decoded.Owner);
            Assert.Equal(DiskUsageScanScopes.AllVolumes, decoded.Request.Scope);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal(ulong.MaxValue.ToString(), root.GetProperty("configurationGeneration").GetString());
            Assert.Equal((ulong.MaxValue - 1).ToString(), root.GetProperty("stateRevision").GetString());
            var progress = root.GetProperty("progress");
            Assert.Equal(ulong.MaxValue.ToString(), progress.GetProperty("sequence").GetString());
            Assert.Equal((ulong.MaxValue - 1).ToString(), progress.GetProperty("bytesDone").GetString());
            Assert.Equal(ulong.MaxValue.ToString(), progress.GetProperty("bytesTotal").GetString());
            Assert.Equal(ulong.MaxValue.ToString(), progress.GetProperty("speedBytesPerSecond").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("configurationGeneration").ValueKind);
        }
        finally
        {
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class EmptyVolumeCatalog : IDiskUsageVolumeCatalog
    {
        public IReadOnlyList<DiskUsageVolume> ReadVolumes() => [];
    }

    private sealed class RecordingCommandService(HostManagerOperationSnapshot snapshot)
        : IHostManagerOperationCommandService
    {
        public HostManagerOperationSubmitCommand? Submitted { get; private set; }

        public Task<HostManagerOperationSnapshot> SubmitAsync(
            HostManagerOperationSubmitCommand command,
            CancellationToken cancellationToken)
        {
            Submitted = command;
            return Task.FromResult(snapshot);
        }

        public Task<HostManagerOperationSnapshot?> CancelAsync(
            string operationId,
            CancellationToken cancellationToken)
            => Task.FromResult<HostManagerOperationSnapshot?>(null);
    }

    private static TrustedAdapterCallerIdentity Caller(DiskUsageResultOwner owner)
        => new(11, 12, 13, 14, owner.WindowsSid, owner.WindowsSessionId, 15, 0, false,
            @"C:\Program Files\ResourceManager\ResourceManager.exe",
            new AdapterExecutableFileIdentity(1, 2, 3), 16);
}
