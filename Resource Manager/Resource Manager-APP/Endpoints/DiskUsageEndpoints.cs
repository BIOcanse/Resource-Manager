using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Operations;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDiskUsageEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        // 卷会插拔，所以每次都现读，不缓存也不缓存响应。
        app.MapGet("/api/disk-usage/volumes", (
            HttpResponse response,
            IDiskUsageVolumeCatalog volumes) =>
        {
            DisableResponseCache(response);
            return Results.Ok(volumes.ReadVolumes());
        }).AllowAnonymous();

        // 扫描结果的概况。还没扫过就是 null，界面据此显示空态。
        app.MapGet("/api/disk-usage/summary", (
            HttpResponse response,
            IDiskUsageTreeStore store) =>
        {
            DisableResponseCache(response);
            return Results.Ok(store.Current?.Summary);
        }).AllowAnonymous();

        // 方格布局。默认从根开始；钻进子树时带上 node。
        //
        // 其余参数描述客户端此刻看到的东西：画布的物理像素尺寸、滚轮倍数、
        // 以及看得见的那块单位空间矩形。发哪些方格由它们决定 ——
        // 看不见的不发，在屏幕上小于一个像素门槛的也不发。
        // 一个都不传就按整张图、不缩放算，所以这些参数都是可选的。
        app.MapGet("/api/disk-usage/layout", (
            HttpResponse response,
            IDiskUsageTreeStore store,
            int? node,
            float? pixelWidth,
            float? pixelHeight,
            float? scale,
            float? minX,
            float? minY,
            float? maxX,
            float? maxY) =>
        {
            DisableResponseCache(response);
            var current = store.Current;
            if (current is null || current.Tree.Roots.Count == 0)
            {
                return Results.Ok(null as object);
            }

            var tree = current.Tree;
            var rootNode = node is { } requested && requested >= 0 && requested < tree.Count
                ? requested
                : tree.Roots[0];
            var view = DiskUsageViewWindow.Normalize(
                pixelWidth, pixelHeight, scale, minX, minY, maxX, maxY);
            var layout = DiskUsageTreemapLayout.Create(tree, rootNode, view);
            return Results.Ok(new
            {
                rootNodeId = layout.RootNodeId,
                rootPath = tree.PathOf(rootNode),
                rootSizeBytes = tree.SizeOf(rootNode),
                omittedCount = layout.OmittedCount,
                // 方格是热路径上量最大的东西，发成并列数组而不是一堆对象。
                nodeIds = layout.Tiles.Select(static tile => tile.NodeId).ToArray(),
                parentIds = layout.Tiles.Select(static tile => tile.ParentNodeId).ToArray(),
                depths = layout.Tiles.Select(static tile => tile.Depth).ToArray(),
                x = layout.Tiles.Select(static tile => tile.X).ToArray(),
                y = layout.Tiles.Select(static tile => tile.Y).ToArray(),
                width = layout.Tiles.Select(static tile => tile.Width).ToArray(),
                height = layout.Tiles.Select(static tile => tile.Height).ToArray(),
                directoryFlags = layout.Tiles.Select(static tile => tile.IsDirectory).ToArray(),
                sizes = layout.Tiles.Select(static tile => tile.SizeBytes).ToArray(),
                // 名字跟方格一起发。它只是每个方格多几十字节，
                // 而分开按需取意味着一次布局要发几万个单节点请求 —— 那才是真的重。
                names = layout.Tiles
                    .Select(tile => new string(tree.NameOf(tile.NodeId)))
                    .ToArray(),
                // 悬停提示要对目录说"里面有多少个文件"，同样跟着方格一起发。
                fileCounts = layout.Tiles
                    .Select(tile => tree.FileCountOf(tile.NodeId))
                    .ToArray()
            });
        }).AllowAnonymous();

        // 单个节点的事实：右键菜单和选中提示要的就是这些。
        app.MapGet("/api/disk-usage/node/{nodeId:int}", (
            HttpResponse response,
            IDiskUsageTreeStore store,
            int nodeId) =>
        {
            DisableResponseCache(response);
            var current = store.Current;
            if (current is null || nodeId < 0 || nodeId >= current.Tree.Count)
            {
                return Results.NotFound();
            }

            var tree = current.Tree;
            return Results.Ok(new DiskUsageNode(
                checked((uint)nodeId),
                tree.ParentOf(nodeId) >= 0 ? checked((uint)tree.ParentOf(nodeId)) : 0,
                new string(tree.NameOf(nodeId)),
                tree.PathOf(nodeId),
                tree.IsDirectory(nodeId),
                tree.SizeOf(nodeId) > 0 ? (ulong)tree.SizeOf(nodeId) : 0,
                tree.AllocatedOf(nodeId) > 0 ? (ulong)tree.AllocatedOf(nodeId) : 0,
                tree.FileCountOf(nodeId),
                null));
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            // 扫描走操作协调器：进度、取消和任务中心条目都是它现成给的。
            app.MapPost("/api/disk-usage/scan", async (
                DiskUsageScanRequest request,
                IHostManagerOperationCommandService operations,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var command = HostManagerOperationRequestCodec.DiskUsageScan(request);
                    return Results.Ok(await operations
                        .SubmitAsync(command, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (ArgumentException error)
                {
                    return Results.BadRequest(new { error = error.Message });
                }
                catch (InvalidOperationException error)
                {
                    return Results.BadRequest(new { error = error.Message });
                }
            });
        }

        return app;
    }
}
