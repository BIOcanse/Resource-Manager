using System.ComponentModel;
using System.Buffers.Binary;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal static class QtQuickGpuProcessIdentity
{
    private static readonly IReadOnlyDictionary<string, string> QualifiedImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Qt6Quick.dll"] = "2454BAE3EE29FA76B3ABE0F9D6CE6FC55630A01B400892F79511ADD3EBE78601",
        ["Qt6Gui.dll"] = "428E11074A3764184F06BD437779F483F5C754C8C8DE5A6CDA94D9ED108A4789",
        ["Qt6Core.dll"] = "6965720757C87788C1701B236B8157270F3EA9B7D5EAF2D1297B21803FC3B000",
        ["ucrtbase.dll"] = "5C52E3A303BAAAC0E0AF8BD9B96134993DA34BC9D834A31EF37E1D2CDC7FE192"
    };
    internal static bool IsQtModule(string name) => name.Equals("Qt6Gui.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Qt6Quick.dll", StringComparison.OrdinalIgnoreCase);
    internal static bool HasQualifiedModules(IReadOnlyDictionary<string, Version> versions)
        => versions.TryGetValue("Qt6Gui.dll", out var gui) && gui == new Version(6, 8, 3)
            && versions.TryGetValue("Qt6Quick.dll", out var quick) && quick == gui
            && (versions.ContainsKey("D3D12Core.dll") || versions.ContainsKey("vulkan-1.dll"));

    // Layout of the hash-qualified Qt 6.8.3 QSGRhiSupport singleton, not a Qt ABI.
    private const int AppliedBackendStateRva = 0x5ba4c8;
    internal static bool HasD3D12Backend(ReadOnlySpan<byte> state)
        => ReadBackend(state) == ExternalGpuRenderer.QtQuickD3D12;

    internal static ExternalGpuRenderer? ReadBackend(ReadOnlySpan<byte> state)
        => state.Length != 8 || state[0] != 1 ? null : BinaryPrimitives.ReadInt32LittleEndian(state[4..]) switch
        {
            5 => ExternalGpuRenderer.QtQuickD3D12,
            1 => ExternalGpuRenderer.QtQuickVulkan,
            _ => null
        };

    private static ExternalGpuRenderer? ReadBackend(RendererProcessRead process, RendererModuleName quick)
    {
        return process.ModuleSize(quick) >= AppliedBackendStateRva + 8
            && process.ReadMemory(quick.Address + AppliedBackendStateRva, 8) is { } state ? ReadBackend(state) : null;
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected)
    {
        using var reads = new RendererQualificationReadCycle(WindowsRendererQualificationNativeApi.Instance);
        return Read(expected, reads);
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected, RendererQualificationReadCycle reads)
    {
        if (expected.ProcessId <= 4 || expected.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(expected.ExecutablePath)) return null;
        try
        {
            var process = reads.Get(expected);
            var modules = process?.Modules();
            if (process is null || modules is null || !modules.Any(module => module.Name.Equals("Qt6Quick.dll", StringComparison.OrdinalIgnoreCase))) return null;
            if (!process.IsAmd64() || process.Identity.SessionId == 0 || process.IsDebugged() != false) return null;
            var versions = modules.Where(module => IsQtModule(module.Name))
                .ToDictionary(module => module.Name, process.Version, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "D3D12Core.dll", "vulkan-1.dll" })
            {
                var count = modules.Count(module => module.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (count > 1) return null;
                if (count == 1) versions.Add(name, new Version(0, 0)); // Only presence is consumed for backend libraries.
            }
            if (!HasQualifiedModules(versions)) return null;
            foreach (var (name, hash) in QualifiedImages)
            {
                var module = modules.SingleOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (module is null) return null;
                if (!process.Hash(module).Equals(hash, StringComparison.Ordinal)) return null;
            }
            var directories = modules.Where(module => IsQtModule(module.Name))
                .Select(module => Path.GetDirectoryName(process.ModulePath(module))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var quick = modules.Single(module => module.Name.Equals("Qt6Quick.dll", StringComparison.OrdinalIgnoreCase));
            var renderer = ReadBackend(process, quick);
            if (directories != 1 || renderer is null) return null;
            if (renderer == ExternalGpuRenderer.QtQuickD3D12 && !versions.ContainsKey("D3D12Core.dll")) return null;
            if (renderer == ExternalGpuRenderer.QtQuickVulkan)
            {
                var loader = modules.SingleOrDefault(module => module.Name.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase));
                if (loader is null) return null;
                if (!process.Hash(loader).Equals(
                    "CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB", StringComparison.Ordinal)) return null;
            }
            return new(expected, expected) { Renderer = renderer.Value };
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or OverflowException
            or IOException or UnauthorizedAccessException)
        { return null; }
    }

}
