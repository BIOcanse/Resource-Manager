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

    public WindowsGpuPlacementCapabilityReader()
        : this(AppContext.BaseDirectory)
    {
    }

    internal WindowsGpuPlacementCapabilityReader(
        string baseDirectory)
    {
        var root = Path.GetFullPath(baseDirectory);
        this.baseDirectory = root;
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
            var blocked = Capability(executable.State, executable.Architecture, executable.Reason);
            return new GpuPlacementProcessCapabilities(processKey, blocked, blocked);
        }

        var startupApi = EvaluateGraphicsApi(graphicsApi, forStartup: true);
        var startupMissing = GpuStartupProviderArtifacts.MissingFiles(baseDirectory, [startupApi.ProviderId]);
        var startup = startupApi.State != GpuPlacementCapabilityStates.Supported
            ? Capability(startupApi.State, executable.Architecture, startupApi.Reason,
                startupApi.GraphicsApi, startupApi.ProviderId)
            : startupMissing.Count == 0
            ? Capability(
                GpuPlacementCapabilityStates.Supported,
                executable.Architecture,
                $"{startupApi.Reason}启动链只影响其后创建的 instance/设备，仍须允许 GPU shim。",
                startupApi.GraphicsApi, startupApi.ProviderId)
            : Capability(
                GpuPlacementCapabilityStates.Unsupported,
                executable.Architecture,
                $"启动期精确 Provider 文件缺失：{string.Join("、", startupMissing)}。已保存设置不会执行。",
                startupApi.GraphicsApi, startupApi.ProviderId);

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
                ? "等待当前 GPU 进程和渲染器的精确确认；仅确认过的外部重建路径可执行。"
                : "此软件尚无已验证的无注入运行期换卡路径。",
            GpuGraphicsApiRoutes.DisplayName(graphicsApi), runtimeProvider);

        return new GpuPlacementProcessCapabilities(processKey, startup, runtime);
    }

    private static ExecutableCapability EvaluateExecutable(
        string? executablePath,
        string? observedArchitecture)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "尚未取得可执行文件完整路径，无法验证精确 GPU Provider 能力。",
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
                "可执行文件路径无效，无法验证精确 GPU Provider 能力。",
                null);
        }

        if (!File.Exists(path))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "可执行文件当前不存在，无法验证精确 GPU Provider 能力。",
                null);
        }

        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (IsPathUnder(path, windowsRoot))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                NormalizeArchitecture(observedArchitecture),
                "Windows 系统目录下的进程不接受精确 GPU Provider。",
                path);
        }

        var imageName = Path.GetFileName(path);
        if (imageName.Equals("ResourceManager.exe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals("ResourceManager.NativeUi.exe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals(WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName, StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                NormalizeArchitecture(observedArchitecture),
                "Resource Manager 自身进程不接受精确 GPU Provider。",
                path);
        }

        if (!WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(path, out var machine))
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unknown,
                NormalizeArchitecture(observedArchitecture),
                "无法读取可执行文件架构，精确 GPU Provider 能力未知。",
                path);
        }

        if (machine != ImageFileMachineAmd64)
        {
            return new ExecutableCapability(
                GpuPlacementCapabilityStates.Unsupported,
                $"machine-0x{machine:x4}",
                "当前精确 GPU Provider 只支持原生 x64 可执行文件。",
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
                "软件信息尚未记录图形 API，等待后台首次识别。");
        }

        if (forStartup && api == GpuGraphicsApi.Vulkan)
        {
            return new GraphicsApiCapability(GpuPlacementCapabilityStates.Supported, "Vulkan",
                "已观察到 Vulkan。启动路径使用普通权限新进程的 explicit layer 与 API 1.1+ instance 选卡，不迁移既有设备。",
                GpuPlacementProviderIds.VulkanExplicitLayer);
        }

        var supportedApis = new List<string>();
        var entryPoints = new List<string>();
        if (GpuGraphicsApiRoutes.RuntimeProvider(api) is not null && api.Value.HasFlag(GpuGraphicsApi.D3D11))
        {
            supportedApis.Add("D3D11");
            entryPoints.Add("D3D11CreateDevice/D3D11CreateDeviceAndSwapChain");
        }

        if (GpuGraphicsApiRoutes.RuntimeProvider(api) is not null && api.Value.HasFlag(GpuGraphicsApi.D3D12))
        {
            supportedApis.Add("D3D12");
            entryPoints.Add("D3D12CreateDevice / ID3D12DeviceFactory::CreateDevice（通过 D3D12GetInterface 在加载后获取的工厂；不保证预先持有的工厂）");
        }

        if (supportedApis.Count != 0)
        {
            return new GraphicsApiCapability(
                GpuPlacementCapabilityStates.Supported,
                string.Join("/", supportedApis),
                $"软件信息已记录 {string.Join("/", supportedApis)}；仅覆盖 {string.Join("、", entryPoints)}。");
        }

        return new GraphicsApiCapability(
            GpuPlacementCapabilityStates.Unsupported,
            GpuGraphicsApiRoutes.DisplayName(api),
            $"已记录 {GpuGraphicsApiRoutes.DisplayName(api)}；当前没有适用的精确路径。已保存设置不会执行。");
    }

    private static GpuPlacementProviderCapability Capability(
        string state,
        string? architecture,
        string reason,
        string graphicsApi = SupportedGraphicsApis,
        string providerId = D3d11ProxyShimRuntime.ProviderId) => new(
            state,
            providerId,
            graphicsApi,
            architecture,
            reason);

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
        string Reason,
        string? ExecutablePath);

    private sealed record GraphicsApiCapability(
        string State,
        string GraphicsApi,
        string Reason,
        string ProviderId = D3d11ProxyShimRuntime.ProviderId);
}
