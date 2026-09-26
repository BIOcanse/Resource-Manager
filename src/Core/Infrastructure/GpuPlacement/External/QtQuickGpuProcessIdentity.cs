using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
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

    private static ExternalGpuRenderer? ReadBackend(Process process, ProcessModule quick)
    {
        var state = new byte[8];
        return quick.ModuleMemorySize >= AppliedBackendStateRva + state.Length
            && ReadProcessMemory(process.SafeHandle, quick.BaseAddress + AppliedBackendStateRva, state,
                (nuint)state.Length, out var read) && read == (nuint)state.Length ? ReadBackend(state) : null;
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected)
    {
        if (expected.ProcessId <= 4 || expected.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(expected.ExecutablePath)) return null;
        try
        {
            if (!WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(expected.ExecutablePath, out var machine)
                || machine != 0x8664) return null;
            using var process = Process.GetProcessById(expected.ProcessId);
            if (!ChromiumGpuProcessIdentity.Matches(process, expected) || process.SessionId == 0
                || !CheckRemoteDebuggerPresent(process.Handle, out var debugged) || debugged) return null;
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            var versions = modules.Where(module => IsQtModule(module.ModuleName)
                    || module.ModuleName.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(module => module.ModuleName, module => new Version(module.FileVersionInfo.FileMajorPart,
                    module.FileVersionInfo.FileMinorPart, module.FileVersionInfo.FileBuildPart), StringComparer.OrdinalIgnoreCase);
            if (!HasQualifiedModules(versions)) return null;
            foreach (var (name, hash) in QualifiedImages)
            {
                var module = modules.SingleOrDefault(value => value.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (module is null) return null;
                using var file = File.OpenRead(module.FileName);
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.Ordinal)) return null;
            }
            var directories = modules.Where(module => IsQtModule(module.ModuleName))
                .Select(module => Path.GetDirectoryName(module.FileName)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var quick = modules.Single(module => module.ModuleName.Equals("Qt6Quick.dll", StringComparison.OrdinalIgnoreCase));
            var renderer = ReadBackend(process, quick);
            if (directories != 1 || renderer is null) return null;
            if (renderer == ExternalGpuRenderer.QtQuickD3D12 && !versions.ContainsKey("D3D12Core.dll")) return null;
            if (renderer == ExternalGpuRenderer.QtQuickVulkan)
            {
                var loader = modules.SingleOrDefault(module => module.ModuleName.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase));
                if (loader is null) return null;
                using var file = File.OpenRead(loader.FileName);
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(
                    "CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB", StringComparison.Ordinal)) return null;
            }
            return new(expected, expected) { Renderer = renderer.Value };
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or OverflowException
            or IOException or UnauthorizedAccessException)
        { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool present);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer,
        nuint size, out nuint bytesRead);
}
