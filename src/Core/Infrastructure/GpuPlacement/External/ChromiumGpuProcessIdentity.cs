using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.ProcessIdentity;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal static class ChromiumGpuProcessIdentity
{
    internal static bool HasCompatibleRenderer(IReadOnlyList<string> arguments, GpuGraphicsApi? confirmedApi)
    {
        if (confirmedApi != GpuGraphicsApi.D3D11) return false;
        var processTypes = arguments.Where(argument => argument.StartsWith("--type=", StringComparison.Ordinal)).ToArray();
        if (processTypes.Length != 1 || processTypes[0] != "--type=gpu-process") return false;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--use-angle=", StringComparison.Ordinal)
                && argument is not "--use-angle=d3d11" and not "--use-angle=default") return false;
            if (argument.StartsWith("--use-gl=", StringComparison.Ordinal)
                && argument is not "--use-gl=angle" and not "--use-gl=default") return false;
            if (argument.StartsWith("--enable-features=", StringComparison.Ordinal)
                && argument[18..].Split(',').Any(feature => feature.Trim().Split(':', '<', '.')[0] == "Vulkan")) return false;
        }
        return true;
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected, GpuGraphicsApi? confirmedApi)
    {
        using var reads = new RendererQualificationReadCycle(WindowsRendererQualificationNativeApi.Instance);
        return Read(expected, confirmedApi, reads);
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected, GpuGraphicsApi? confirmedApi,
        RendererQualificationReadCycle reads)
    {
        if (string.IsNullOrWhiteSpace(expected.ExecutablePath) || expected.ProcessId <= 4 || expected.ProcessStartKey == 0)
            return null;
        try
        {
            var gpu = reads.Get(expected);
            if (gpu is null || !gpu.IsAmd64() || gpu.Arguments().Value is not { } gpuArguments
                || !HasCompatibleRenderer(gpuArguments, confirmedApi))
                return null;
            var browser = gpu.ParentId() is int parentId ? reads.Get(parentId) : null;
            if (browser is null || browser.Identity.StartKey > expected.ProcessStartKey || browser.Identity.SessionId == 0
                || browser.Identity.SessionId != gpu.Identity.SessionId
                || browser.Arguments().Value is not { } browserArguments
                || browserArguments.Any(argument => argument.StartsWith("--type=", StringComparison.Ordinal))
                || browser.IsDebugged() != false) return null;
            return new(new(browser.Identity.ProcessId, browser.Identity.StartKey,
                Path.GetFileNameWithoutExtension(browser.Identity.ImagePath), browser.Identity.ImagePath), expected);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal static bool Matches(Process process, GpuPlacementProcessInstance expected)
        => !process.HasExited && checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()) == expected.ProcessStartKey
            && string.Equals(WindowsProcessImagePathReader.TryRead(process), expected.ExecutablePath,
                StringComparison.OrdinalIgnoreCase);

}
