using ResourceManager.App.Domain.PublicServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

public sealed partial class HostManagerPublicServiceCoordinatorOwner
{
    public Task<LocalPublicServiceDescriptor> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var holder = AcquireCurrent();
        try
        {
            return Task.FromResult(ProjectCatalog(holder));
        }
        finally
        {
            holder.Release();
        }
    }

    public async Task<IReadOnlyList<LocalPublicServiceCapability>> ListCapabilitiesAsync(
        CancellationToken cancellationToken)
        => (await GetCatalogAsync(cancellationToken).ConfigureAwait(false)).Capabilities;

    private static LocalPublicServiceDescriptor ProjectCatalog(WorkspaceHolder holder)
    {
        var hot = holder.Plan.HotPublish;
        var nativeRows = holder.Workspace.ReadCapabilities(
            holder.Plan.Recreate.Capacity.MaximumCapabilityCount);
        if (nativeRows.Length != hot.Capabilities.Length)
        {
            throw new InvalidOperationException(
                "The native public-service capability count does not match the compiled plan.");
        }

        var planByHandle = hot.Capabilities.ToDictionary(
            static item => item.Handle);
        var capabilities = new LocalPublicServiceCapability[nativeRows.Length];
        for (var index = 0; index < nativeRows.Length; index++)
        {
            var row = nativeRows[index];
            if (!planByHandle.TryGetValue(row.CapabilityHandle, out var plan)
                || row.PayloadHandle != plan.PayloadHandle
                || row.CatalogGeneration != hot.ConfigurationGeneration)
            {
                throw new InvalidOperationException(
                    "The native public-service capability projection does not match the compiled plan.");
            }
            var flags = (NativePublicServiceCapabilityFlags)row.Flags;
            if ((flags & ~NativePublicServiceCapabilityFlags.Known) != 0
                || flags.HasFlag(NativePublicServiceCapabilityFlags.Enabled) != plan.Enabled
                || flags.HasFlag(NativePublicServiceCapabilityFlags.Available) != plan.Available)
            {
                throw new InvalidOperationException(
                    "The native public-service capability state is not canonical.");
            }
            capabilities[index] = new LocalPublicServiceCapability(
                plan.Id,
                hot.ApiVersion,
                plan.Enabled,
                plan.Available,
                plan.Description,
                ProjectEndpoints(hot.Routes, plan.Handle));
        }
        return new LocalPublicServiceDescriptor(
            hot.ServiceName,
            hot.ApiVersion,
            hot.BasePath,
            hot.ServiceEnabled,
            capabilities,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<LocalPublicServiceEndpoint> ProjectEndpoints(
        IEnumerable<CompiledHostManagerPublicServiceRoutePlan> routes,
        ulong capabilityHandle)
    {
        var endpoints = new List<LocalPublicServiceEndpoint>();
        foreach (var route in routes
                     .Where(item => item.CapabilityHandle == capabilityHandle)
                     .OrderBy(static item => item.Handle))
        {
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Get,
                "GET",
                route.Path);
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Head,
                "HEAD",
                route.Path);
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Post,
                "POST",
                route.Path);
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Put,
                "PUT",
                route.Path);
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Delete,
                "DELETE",
                route.Path);
            AppendEndpoint(
                endpoints,
                route.MethodMask,
                NativePublicServiceMethodMask.Patch,
                "PATCH",
                route.Path);
        }
        return endpoints;
    }

    private static void AppendEndpoint(
        ICollection<LocalPublicServiceEndpoint> endpoints,
        uint methodMask,
        NativePublicServiceMethodMask flag,
        string method,
        string path)
    {
        if ((methodMask & (uint)flag) != 0)
        {
            endpoints.Add(new LocalPublicServiceEndpoint(method, path));
        }
    }
}
