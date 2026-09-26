using System.Runtime.InteropServices;
using System.ComponentModel;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    private static RemoteModule? FindRemoteModuleWithRetry(
        int processId,
        string moduleName)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var module = FindRemoteModule(processId, moduleName);
            if (module is not null)
            {
                return module;
            }
            Thread.Sleep(25);
        }

        return null;
    }

    internal static RemoteModule? FindRemoteModule(
        int processId,
        string moduleName)
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32,
            (uint)processId);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var entry = new NativeMethods.ModuleEntry32
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>()
        };
        if (!NativeMethods.Module32First(snapshot, ref entry))
        {
            return EndOfModuleEnumeration(Marshal.GetLastWin32Error());
        }

        do
        {
            if (entry.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry.ExecutablePath).Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteModule(entry.BaseAddress, entry.ExecutablePath);
            }
        }
        while (NativeMethods.Module32Next(snapshot, ref entry));

        return EndOfModuleEnumeration(Marshal.GetLastWin32Error());
    }

    internal static RemoteModule? EndOfModuleEnumeration(int error)
        => error == NativeMethods.ErrorNoMoreFiles ? null : throw new Win32Exception(error);

    private static GpuGraphicsApi ReadApiObservationCandidates(int processId)
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32, (uint)processId);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new NativeMethods.ModuleEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>() };
        var names = new List<string>();
        if (NativeMethods.Module32First(snapshot, ref entry))
        {
            do { names.Add(entry.ModuleName); }
            while (NativeMethods.Module32Next(snapshot, ref entry));
        }
        _ = EndOfModuleEnumeration(Marshal.GetLastWin32Error());
        return WindowsGpuGraphicsApiDetector.CollectCandidates(names);
    }

    internal static ProviderBinding SelectProvider(
        GpuGraphicsApi api, string baseDirectory, Func<string, RemoteModule?> findModule)
    {
        var required = RequiredProviderStatus(api);
        if (api == GpuGraphicsApi.Vulkan && findModule(GpuStartupProviderArtifacts.VulkanLayerFileName) is { } layer)
            return new(Path.Combine(baseDirectory, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName),
                ConfigureExportName, required, layer);

        return new(Path.Combine(baseDirectory, "GpuPlacementShim", RuntimeProviderFileName),
            api switch
            {
                GpuGraphicsApi.Vulkan => ConfigureVulkanExportName,
                GpuGraphicsApi.OpenGL => ConfigureOpenGlExportName,
                _ => ConfigureExportName
            },
            required, findModule(RuntimeProviderFileName));
    }

    internal sealed record ProviderBinding(string Path, string ConfigureExport, uint RequiredStatus, RemoteModule? LoadedModule);
    internal readonly record struct RemoteModule(IntPtr BaseAddress, string Path);
}
