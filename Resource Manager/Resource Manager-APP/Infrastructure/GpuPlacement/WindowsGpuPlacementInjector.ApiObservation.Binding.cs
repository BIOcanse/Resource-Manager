using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    private async Task<RuntimeGpuProviderInjectionResult> EnsureObservationProviderAsync(
        SafeKernelHandle handle, ProviderProcess owner, GpuGraphicsApi? candidates, CancellationToken token)
    {
        var processId = owner.Identity.ProcessId;
        if (CheckRuntimeProviderEligibility(handle, owner.Identity) is { } rejected) return rejected;
        var actualCandidates = candidates ?? ReadApiObservationCandidates(processId);
        if (actualCandidates == 0)
            return Failed(processId, "api-observation-no-candidates", "目标尚未加载可观察的图形运行库。");
        var providers = SelectApiObservationProviders(actualCandidates, AppContext.BaseDirectory,
            name => FindRemoteModule(processId, name));
        var offsets = new ApiObservationEntries[providers.Count];
        // Validate every image before loading any additional provider or starting a window.
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            if (!File.Exists(provider.Path))
                return Failed(processId, "provider-missing", $"运行时 GPU Provider 不存在：{provider.Path}");
            if (provider.LoadedModule is { } existing && !PathsMatch(existing.Path, provider.Path))
                return Failed(processId, "provider-path-mismatch", "目标已加载不同路径的 Provider。");
            if (!PortableExecutableExportReader.TryGetExportRva(provider.Path, StartApiObservationExportName, out var start, out var error)
                || !PortableExecutableExportReader.TryGetExportRva(provider.Path, ReadApiObservationExportName, out var read, out error)
                || !PortableExecutableExportReader.TryGetExportRva(provider.Path, StopApiObservationExportName, out var stop, out error))
                return Failed(processId, "api-observation-export-missing", error);
            offsets[index] = new(new(checked((int)start)), new(checked((int)read)), new(checked((int)stop)), provider.Apis);
        }

        var entries = new ApiObservationEntries[providers.Count];
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            var observed = provider.LoadedModule;
            if (observed is null)
            {
                var loaded = await LoadProviderAsync(owner, provider.Path, GpuRemoteCallKind.LoadObservationProvider, token).ConfigureAwait(false);
                if (!loaded.Success) return loaded;
                observed = FindRemoteModule(processId, Path.GetFileName(provider.Path));
                if (observed is null)
                    return Failed(processId, "provider-not-observed", "远程加载完成后未找到 GPU Provider 模块。");
            }
            var module = observed.Value;
            if (!PathsMatch(module.Path, provider.Path))
                return Failed(processId, "provider-path-mismatch", "目标已加载不同路径的 Provider。");
            var entry = offsets[index];
            entries[index] = new(module.BaseAddress + checked((int)entry.Start),
                module.BaseAddress + checked((int)entry.Read), module.BaseAddress + checked((int)entry.Stop), entry.Apis);
        }
        owner.ApiObservations = entries;
        return new(processId, true, providers.All(provider => provider.LoadedModule is not null),
            "api-observation-prepared", "API 观察入口已准备，尚未开始观察。", providers[0].Path, null, 0, null);
    }

    internal static IReadOnlyList<ApiObservationProvider> SelectApiObservationProviders(
        GpuGraphicsApi candidates, string baseDirectory, Func<string, RemoteModule?> findModule)
    {
        if (candidates == 0 || (candidates & ~GpuApiObservationProtocol.AllApis) != 0)
            throw new ArgumentOutOfRangeException(nameof(candidates));
        List<ApiObservationProvider> providers = [];
        if ((candidates & GpuGraphicsApi.Vulkan) != 0
            && findModule(GpuStartupProviderArtifacts.VulkanLayerFileName) is { } layer)
        {
            providers.Add(new(Path.Combine(baseDirectory, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName),
                GpuGraphicsApi.Vulkan, layer));
            candidates &= ~GpuGraphicsApi.Vulkan;
        }
        if (candidates != 0)
            providers.Add(new(Path.Combine(baseDirectory, "GpuPlacementShim", RuntimeProviderFileName), candidates, findModule(RuntimeProviderFileName)));
        return providers;
    }

    internal sealed record ApiObservationProvider(string Path, GpuGraphicsApi Apis, RemoteModule? LoadedModule);
}
