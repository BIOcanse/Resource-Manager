using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuStartupPlacementResolver(
    IGpuPlacementPolicyStore policyStore,
    IRuntimePlanProvider runtimePlanProvider,
    D3d11ProxyShimRuntime shimRuntime,
    IGpuPlacementProcessHistoryStore processHistoryStore) : IGpuStartupPlacementResolver
{
    public async Task<GpuStartupPlacementDecision> ResolveAsync(
        GpuStartupPlacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executablePath = NormalizePath(request.ExecutablePath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return PassThrough(
                executablePath ?? string.Empty,
                "executable-missing",
                "目标可执行文件不存在。");
        }

        var document = await policyStore.GetAsync(cancellationToken);
        var processPolicy = document.ProcessPolicies
            .Where(static policy => policy.StartupInterceptionEnabled)
            .Where(policy => string.Equals(
                NormalizePath(policy.ExecutablePath),
                executablePath,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static policy => policy.UpdatedAt)
            .FirstOrDefault();
        if (processPolicy is null)
        {
            return PassThrough(executablePath, "not-marked", "该完整路径没有启用固定启动拦截。");
        }

        var softwarePolicy = document.SoftwarePolicies
            .Where(policy => policy.SoftwareId.Equals(processPolicy.SoftwareId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static policy => policy.UpdatedAt)
            .FirstOrDefault();
        if (softwarePolicy is null)
        {
            return PassThrough(
                executablePath,
                "software-policy-missing",
                "启动拦截存在，但找不到对应的软件级启动策略。",
                processPolicy);
        }

        var compiled = runtimePlanProvider.Current.GpuPlacement;
        if (!compiled.GlobalPreciseProviderEnabled)
        {
            return PassThrough(
                executablePath,
                "global-provider-disabled",
                "全局精确 GPU Provider 当前关闭。",
                processPolicy,
                softwarePolicy.StartupTargetGpu);
        }

        var resolved = compiled.Resolve(
            processPolicy.SoftwareId,
            softwarePolicy.SoftwareName,
            null,
            processPolicy.ProcessKey);
        if (!resolved.AllowsStartupShimExecution())
        {
            return PassThrough(
                executablePath,
                "startup-provider-not-allowed",
                "当前软件策略或 Provider 能力不允许启动期精确选卡。",
                processPolicy,
                resolved.StartupTargetGpu);
        }

        var targetGpu = GpuPlacementTargets.Normalize(resolved.StartupTargetGpu);
        if (targetGpu.Equals(GpuPlacementTargets.SystemDefaultGpu, StringComparison.OrdinalIgnoreCase))
        {
            return PassThrough(
                executablePath,
                "system-default-target",
                "启动目标为系统默认 GPU，不需要注入精确 Provider。",
                processPolicy,
                targetGpu);
        }

        var assignedPositionId = ResolveConfiguredPosition(targetGpu);
        if (targetGpu.Equals(GpuPlacementTargets.AutoIdleGpu, StringComparison.OrdinalIgnoreCase))
        {
            return PassThrough(
                executablePath,
                "auto-placement-native-plan-required",
                "原生 Placement Coordinator 尚未发布精确启动 GPU 身份，保持系统默认启动。",
                processPolicy,
                targetGpu);
        }

        var history = await processHistoryStore.GetSoftwareHistoryAsync(
            processPolicy.SoftwareId, softwarePolicy.SoftwareName, cancellationToken);
        var graphicsApi = history.Processes.FirstOrDefault(process => string.Equals(
            NormalizePath(process.ExecutablePath), executablePath, StringComparison.OrdinalIgnoreCase))?.GraphicsApi;
        var startupProviders = resolved.GetStartupProviders(graphicsApi);
        if (startupProviders.Count == 0)
        {
            return PassThrough(executablePath, graphicsApi is null ? "graphics-api-unidentified" : "graphics-api-unsupported",
                "软件尚无可用的图形 API 路径，保持原有启动方式。", processPolicy, targetGpu, assignedPositionId);
        }
        var missingFiles = GpuStartupProviderArtifacts.MissingFiles(AppContext.BaseDirectory, startupProviders);
        if (missingFiles.Count != 0)
        {
            return PassThrough(executablePath, "startup-provider-missing",
                $"启动 Provider 文件缺失：{string.Join("、", missingFiles)}。",
                processPolicy, targetGpu, assignedPositionId);
        }

        var preparation = shimRuntime.Prepare(new D3d11ProxyShimPreparationRequest(
            $"startup:{processPolicy.ProcessKey}",
            processPolicy.ProcessName,
            assignedPositionId ?? string.Empty,
            targetGpu.Equals(GpuPlacementTargets.IntegratedGpu, StringComparison.OrdinalIgnoreCase)));
        if (!preparation.PolicyPrepared
            || string.IsNullOrWhiteSpace(preparation.PolicyPath))
        {
            return PassThrough(
                executablePath,
                preparation.TargetResolution,
                preparation.ErrorMessage ?? "启动 GPU 策略或 Provider 二进制不可用。",
                processPolicy,
                targetGpu,
                assignedPositionId);
        }

        return new GpuStartupPlacementDecision(
            GpuStartupPlacementDecisionKinds.Inject,
            preparation.TargetResolution,
            "启动器将配置选定 Provider；策略在其后受支持的设备或 instance 创建时使用。",
            executablePath,
            processPolicy.SoftwareId,
            processPolicy.ProcessKey,
            targetGpu,
            assignedPositionId,
            preparation.PolicyPath,
            preparation.TargetAdapterName,
            preparation.TargetLuid,
            startupProviders);
    }

    private static string? ResolveConfiguredPosition(string targetGpu)
    {
        if (!GpuPlacementTargets.IsExactGpuIndexTarget(targetGpu))
        {
            return string.Empty;
        }

        return int.TryParse(
            targetGpu[3..],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var index)
            ? $"gpu:{index}"
            : string.Empty;
    }

    private static GpuStartupPlacementDecision PassThrough(
        string executablePath,
        string status,
        string message,
        GpuPlacementProcessPolicy? processPolicy = null,
        string? startupTargetGpu = null,
        string? assignedPositionId = null)
    {
        return new GpuStartupPlacementDecision(
            GpuStartupPlacementDecisionKinds.PassThrough,
            status,
            message,
            executablePath,
            processPolicy?.SoftwareId,
            processPolicy?.ProcessKey,
            startupTargetGpu,
            assignedPositionId,
            null,
            null,
            null,
            []);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
