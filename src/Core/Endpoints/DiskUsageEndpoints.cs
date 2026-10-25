using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Endpoints.Transport;

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
        });

        // 扫描结果的概况。还没扫过就是 null，界面据此显示空态。
        app.MapGet("/api/disk-usage/summary", (
            HttpContext context,
            HttpResponse response,
            IOwnedDiskUsageTreeStore store) =>
        {
            DisableResponseCache(response);
            if (!TryGetResultOwner(context, out var owner)) return Results.Unauthorized();
            var summary = store.SummaryFor(owner);
            return summary is null
                ? Results.Text("null", "application/json")
                : Results.Json(summary);
        });

        // 返回所选子树的有界事实页；读取只取得当前结果，不启动或等待扫描。
        app.MapGet("/api/disk-usage/tree", (
            HttpContext context,
            HttpResponse response,
            IOwnedDiskUsageTreeStore store,
            int rootIndex,
            int maxNodes,
            int maxBytes,
            int? node,
            int? cursor) =>
        {
            DisableResponseCache(response);
            if (!TryGetResultOwner(context, out var owner)) return Results.Unauthorized();
            try
            {
                DiskUsageTreeBinaryWriter.ValidateBounds(maxNodes, maxBytes);
                if (rootIndex < 0)
                    throw new ArgumentException("The requested disk-usage root is unavailable.");
                using var reading = store.TryAcquire(owner);
                var current = reading?.Result;
                if (current is null || current.Tree.Roots.Count == 0)
                    return Results.Bytes(DiskUsageTreeBinaryWriter.WriteEmpty(), "application/octet-stream");

                var tree = current.Tree;
                if (rootIndex >= tree.Roots.Count)
                    throw new ArgumentException("The requested disk-usage root is unavailable.");
                var scanRoot = tree.Roots[rootIndex];
                var queryRoot = node ?? scanRoot;
                if ((uint)queryRoot >= (uint)tree.Count)
                    throw new ArgumentException("The requested disk-usage node is unavailable.");
                var ancestor = queryRoot;
                while (ancestor != scanRoot)
                {
                    var parent = tree.ParentOf(ancestor);
                    if (parent < scanRoot || parent >= ancestor)
                        throw new ArgumentException("The requested node is outside the selected scan root.");
                    ancestor = parent;
                }
                return Results.Bytes(
                    DiskUsageTreeBinaryWriter.Write(tree, queryRoot, maxNodes, maxBytes, cursor),
                    "application/octet-stream");
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new EndpointErrorPayload(error.Message));
            }
        });

        // 单个节点的事实：右键菜单和选中提示要的就是这些。
        app.MapGet("/api/disk-usage/node/{nodeId:int}", (
            HttpContext context,
            HttpResponse response,
            IOwnedDiskUsageTreeStore store,
            int nodeId) =>
        {
            DisableResponseCache(response);
            if (!TryGetResultOwner(context, out var owner)) return Results.Unauthorized();
            using var reading = store.TryAcquire(owner);
            var current = reading?.Result;
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
        });

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            // 扫描走操作协调器：进度、取消和任务中心条目都是它现成给的。
            app.MapPost("/api/disk-usage/scan", async (
                HttpContext context,
                DiskUsageScanRequest request,
                IHostManagerOperationCommandService operations,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    if (!TryGetResultOwner(context, out var owner)) return Results.Unauthorized();
                    var command = HostManagerOperationRequestCodec.DiskUsageScan(request, owner);
                    var operation = await operations
                        .SubmitAsync(command, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(HostManagerOperationWireProjection.Project(operation));
                }
                catch (ArgumentException error)
                {
                    return Results.BadRequest(new EndpointErrorPayload(error.Message));
                }
                catch (InvalidOperationException error)
                {
                    return Results.BadRequest(new EndpointErrorPayload(error.Message));
                }
            });
        }

        return app;
    }

    private static bool TryGetResultOwner(HttpContext context, out DiskUsageResultOwner owner)
    {
        if (context.Items.TryGetValue(typeof(TrustedAdapterCallerIdentity), out var value)
            && value is TrustedAdapterCallerIdentity frontend)
        {
            owner = new DiskUsageResultOwner(frontend.WindowsSid, frontend.WindowsSessionId);
            if (owner.IsValid) return true;
        }

        if (WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var caller)
            && caller.ProcessId != Environment.ProcessId)
        {
            owner = new DiskUsageResultOwner(caller.WindowsSid, caller.WindowsSessionId);
            if (owner.IsValid) return true;
        }

        owner = default;
        return false;
    }
}
