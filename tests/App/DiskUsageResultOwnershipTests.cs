using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageResultOwnershipTests
{
    [Fact]
    public void ResultOwnerIsReplacedWithTheOriginalTreeAndSessionMustMatch()
    {
        var store = new DiskUsageTreeStore();
        var firstOwner = new DiskUsageResultOwner("S-1-5-21-1001", 4);
        var nextOwner = new DiskUsageResultOwner("S-1-5-21-2002", 6);
        var first = CreateResult();
        var second = CreateResult();

        store.Replace(first, firstOwner);
        using var firstRead = store.TryAcquire(firstOwner);
        Assert.NotNull(firstRead);
        Assert.Same(first.Tree, firstRead.Result.Tree);
        Assert.Null(store.TryAcquire(new DiskUsageResultOwner(firstOwner.WindowsSid, 5)));
        Assert.Null(store.TryAcquire(new DiskUsageResultOwner("S-1-5-21-3003", 4)));

        store.Replace(second, nextOwner);
        Assert.Same(second.Summary, store.CurrentSummary);
        Assert.Null(store.TryAcquire(firstOwner));
        using var secondRead = store.TryAcquire(nextOwner);
        Assert.NotNull(secondRead);
        Assert.Same(second.Tree, secondRead.Result.Tree);
        Assert.Equal(first.Tree.Count, firstRead.Result.Tree.Count);

        store.Clear();
        Assert.Null(store.TryAcquire(nextOwner));
    }

    [Fact]
    public async Task LayoutCanSelectEveryRootAndASecondSessionCannotReadTheResult()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(ledger);
        var owner = new DiskUsageResultOwner("S-1-5-21-1001", 4);
        var otherSession = new DiskUsageResultOwner(owner.WindowsSid, 5);
        store.Replace(CreateResult(), owner);

        await using var ownerApp = await StartAsync(store, owner);
        await using var otherApp = await StartAsync(store, otherSession);
        using var ownerClient = Client(ownerApp);
        using var otherClient = Client(otherApp);

        using var summary = JsonDocument.Parse(await ownerClient.GetStringAsync("/api/disk-usage/summary"));
        Assert.Equal(new[] { "C:\\", "D:\\" }, summary.RootElement.GetProperty("roots")
            .EnumerateArray().Select(static root => root.GetString()).ToArray());

        using var secondRoot = await ownerClient.GetAsync("/api/disk-usage/layout?rootIndex=1");
        Assert.Equal(System.Net.HttpStatusCode.OK, secondRoot.StatusCode);
        var layout = await secondRoot.Content.ReadAsByteArrayAsync();
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(12)));
        Assert.Equal(73d, BinaryPrimitives.ReadDoubleLittleEndian(layout.AsSpan(24)));
        var rootPathLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(60)));
        Assert.Equal("D:\\", Encoding.UTF8.GetString(layout, layout.Length - rootPathLength, rootPathLength));

        using var invalidRoot = await ownerClient.GetAsync("/api/disk-usage/layout?rootIndex=2");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidRoot.StatusCode);

        using var otherSummary = JsonDocument.Parse(await otherClient.GetStringAsync("/api/disk-usage/summary"));
        Assert.Equal(JsonValueKind.Null, otherSummary.RootElement.ValueKind);
        using var otherLayout = await otherClient.GetAsync("/api/disk-usage/layout");
        var hiddenLayout = await otherLayout.Content.ReadAsByteArrayAsync();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hiddenLayout.AsSpan(8)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(hiddenLayout.AsSpan(12)));
        using var hiddenNode = await otherClient.GetAsync("/api/disk-usage/node/1");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, hiddenNode.StatusCode);
    }

    [Fact]
    public void PublicReadWriteCapabilitiesOmitPathBearingMftReferences()
    {
        var mft = new FieldCapability(
            new FieldReference("ntfs-object|encoded-path", "directory_all_file_count_aggregated"),
            new FieldDescriptor("directory_all_file_count_aggregated", "count", "unsigned-integer", null),
            new OperationSupport(true, null, []),
            OperationSupport.Unsupported("field-write-not-bound"),
            DeviceInformationFieldRegistry.MftSourceId);
        var ordinary = mft with
        {
            Field = new FieldReference("volume-identity", "storage_all_volume_size"),
            ReadSource = "windows.volume-space"
        };

        var visible = ResourceManagerEndpointRouteBuilderExtensions.PublicCapabilityFields([mft, ordinary]);

        Assert.Collection(visible, item => Assert.Same(ordinary, item));
    }

    private static DiskUsageScanResult CreateResult()
    {
        var builder = new DiskUsageTreeBuilder();
        var firstRoot = builder.Add(-1, "C:\\", isDirectory: true, 0, 0);
        _ = builder.Add(firstRoot, "private-one.txt", isDirectory: false, 41, 41);
        var secondRoot = builder.Add(-1, "D:\\", isDirectory: true, 0, 0);
        _ = builder.Add(secondRoot, "private-two.txt", isDirectory: false, 73, 73);
        var tree = builder.Build();
        return new DiskUsageScanResult(tree, new DiskUsageScanSummary(
            DiskUsageScanScopes.AllVolumes,
            DiskUsageScanModes.Fast,
            string.Empty,
            ["C:\\", "D:\\"],
            DiskUsageScanKinds.MasterFileTable,
            [],
            DateTimeOffset.Parse("2026-10-09T12:00:00Z"),
            0.2,
            114,
            114,
            2,
            2,
            0));
    }

    private static async Task<WebApplication> StartAsync(
        DiskUsageTreeStore store,
        DiskUsageResultOwner owner)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDiskUsageTreeStore>(store);
        builder.Services.AddSingleton<IOwnedDiskUsageTreeStore>(store);
        builder.Services.AddSingleton<IDiskUsageVolumeCatalog>(new EmptyDiskUsageVolumeCatalog());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Items[typeof(TrustedAdapterCallerIdentity)] = Caller(owner);
            await next(context);
        });
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapDiskUsageEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, StartupCapabilitySet.NormalReadOnly]);
        await app.StartAsync();
        return app;
    }

    private sealed class EmptyDiskUsageVolumeCatalog : IDiskUsageVolumeCatalog
    {
        public IReadOnlyList<DiskUsageVolume> ReadVolumes() => Array.Empty<DiskUsageVolume>();
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static TrustedAdapterCallerIdentity Caller(DiskUsageResultOwner owner)
        => new(11, 12, 13, 14, owner.WindowsSid, owner.WindowsSessionId, 15, 0, false,
            @"C:\Program Files\ResourceManager\ResourceManager.exe",
            new AdapterExecutableFileIdentity(1, 2, 3), 16);
}
