using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class WindowsGpuPlacementCapabilityReader : IGpuPlacementCapabilityReader
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const string SupportedGraphicsApis = "D3D11/D3D12/Vulkan";

    private readonly string baseDirectory;
    private readonly string runtimeProviderPath;
    private readonly string rendererRuntimePath;
    private readonly ResourceManager.Shared.Packages.PackageRuntimeCatalog imageCatalog;

    public WindowsGpuPlacementCapabilityReader()
        : this(AppContext.BaseDirectory, ResourceManager.Shared.Packages.PackageRuntimeCatalog.Current)
    {
    }

    internal WindowsGpuPlacementCapabilityReader(
        string baseDirectory, ResourceManager.Shared.Packages.PackageRuntimeCatalog? catalog = null)
    {
        var root = Path.GetFullPath(baseDirectory);
        this.baseDirectory = root;
        imageCatalog = catalog ?? ResourceManager.Shared.Packages.PackageRuntimeCatalog.Load(root);
        runtimeProviderPath = Path.Combine(
            root,
            "GpuPlacementShim",
            WindowsExternalGpuPlacementRuntime.FileName);
        rendererRuntimePath = Path.Combine(root, "GpuPlacementShim", WindowsExternalGpuPlacementRuntime.RendererFileName);
    }

    public GpuPlacementProcessCapabilities Evaluate(
        string processKey,
        string? executablePath,
        string? observedArchitecture,
        GpuGraphicsApi? graphicsApi)
    {
        var executable = EvaluateExecutable(executablePath, observedArchitecture);
        if (executable.State != GpuPlacementCapabilityStates.Supported)
        {
            var blocked = Capability(executable.State, executable.Architecture, executable.ReasonCode);
            return new GpuPlacementProcessCapabilities(processKey, blocked, blocked);
        }

        var startupApi = EvaluateGraphicsApi(graphicsApi, forStartup: true);
        var startupMissing = GpuStartupProviderArtifacts.MissingFiles(baseDirectory, [startupApi.ProviderId]);
        var startup = startupApi.State != GpuPlacementCapabilityStates.Supported
            ? Capability(startupApi.State, executable.Architecture, startupApi.ReasonCode,
                startupApi.GraphicsApi, startupApi.ProviderId)
            : startupMissing.Count == 0
            ? Capability(
                GpuPlacementCapabilityStates.Supported,
                executable.Architecture,
                startupApi.ReasonCode,
                startupApi.GraphicsApi, startupApi.ProviderId)
            : Capability(
                GpuPlacementCapabilityStates.Unsupported,
                executable.Architecture,
                "startup-provider-missing",
                startupApi.GraphicsApi, startupApi.ProviderId, startupMissing);

        var (runtimeFile, runtimeProvider) = graphicsApi switch
        {
            GpuGraphicsApi.D3D11 => (runtimeProviderPath, WindowsExternalGpuPlacementRuntime.ProviderId),
            GpuGraphicsApi.D3D12 => (rendererRuntimePath, WindowsExternalGpuPlacementRuntime.QtProviderId),
            GpuGraphicsApi.Vulkan => (rendererRuntimePath, WindowsExternalGpuPlacementRuntime.QtVulkanProviderId),
            _ => (string.Empty, WindowsExternalGpuPlacementRuntime.ProviderId)
        };
        var needsRuntimeObservation = runtimeFile.Length != 0 && File.Exists(runtimeFile);
        var runtime = Capability(needsRuntimeObservation ? GpuPlacementCapabilityStates.Unknown : GpuPlacementCapabilityStates.Unsupported,
            executable.Architecture, needsRuntimeObservation
                ? "runtime-observation-required"
                : "runtime-route-unverified",
            GpuGraphicsApiRoutes.DisplayName(graphicsApi), runtimeProvider);

        return new GpuPlacementProcessCapabilities(processKey, startup, runtime);
    }

    private ExecutableCapability EvaluateExecutable(
        string? executablePath,
        string? observedArchitecture)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "executable-path-missing",
                null);
        }

        string path;
        try
        {
            path = Path.GetFullPath(executablePath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "executable-path-invalid",
                null);
        }

        if (!File.Exists(path))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "executable-missing",
                null);
        }

        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (IsPathUnder(path, windowsRoot))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                NormalizeArchitecture(observedArchitecture),
                "system-path-blocked",
                path);
        }

        if (imageCatalog.IsInfrastructureImage(path)
            || path.Equals(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName)), StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                NormalizeArchitecture(observedArchitecture),
                "self-process-blocked",
                path);
        }

        if (!WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(path, out var machine))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "architecture-unreadable",
                path);
        }

        if (machine != ImageFileMachineAmd64)
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                $"machine-0x{machine:x4}",
                "architecture-unsupported",
                path);
        }

        return new ExecutableCapability(
            GpuPlacementCapabilityStates.Supported,
            "x64",
            string.Empty,
            path);
    }

    private static GraphicsApiCapability EvaluateGraphicsApi(
        GpuGraphicsApi? api,
        bool forStartup)
    {
        if (api is null)
        {
            return new GraphicsApiCapability(
                GpuPlacementCapabilityStates.Unknown,
                SupportedGraphicsApis,
                "graphics-api-unidentified");
        }

        if (forStartup && api == GpuGraphicsApi.Vulkan)
        {
            return new GraphicsApiCapability(GpuPlacementCapabilityStates.Supported, "Vulkan",
                "startup-vulkan-instance-only",
                GpuPlacementProviderIds.VulkanExplicitLayer);
        }

        var supportedApis = new List<string>();
        if (GpuGraphicsApiRoutes.RuntimeProvider(api) is not null && api.Value.HasFlag(GpuGraphicsApi.D3D11))
        {
            supportedApis.Add("D3D11");
        }

        if (GpuGraphicsApiRoutes.RuntimeProvider(api) is not null && api.Value.HasFlag(GpuGraphicsApi.D3D12))
        {
            supportedApis.Add("D3D12");
        }

        if (supportedApis.Count != 0)
        {
            return new GraphicsApiCapability(
                GpuPlacementCapabilityStates.Supported,
                string.Join("/", supportedApis),
                "startup-d3d-creation-only");
        }

        return new GraphicsApiCapability(
            GpuPlacementCapabilityStates.Unsupported,
            GpuGraphicsApiRoutes.DisplayName(api),
            "graphics-api-unsupported");
    }

    private static GpuPlacementProviderCapability Capability(
        string state,
        string? architecture,
        string reasonCode,
        string graphicsApi = SupportedGraphicsApis,
        string providerId = D3d11ProxyShimRuntime.ProviderId,
        IReadOnlyList<string>? missingFiles = null) => new(
            state,
            providerId,
            graphicsApi,
            architecture,
            reasonCode,
            missingFiles ?? []);

    private static string? NormalizeArchitecture(string? architecture) =>
        string.IsNullOrWhiteSpace(architecture) ? null : architecture.Trim();

    private static bool IsPathUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ExecutableCapability(
        string State,
        string? Architecture,
        string ReasonCode,
        string? ExecutablePath);

    private sealed record GraphicsApiCapability(
        string State,
        string GraphicsApi,
        string ReasonCode,
        string ProviderId = D3d11ProxyShimRuntime.ProviderId);
}
