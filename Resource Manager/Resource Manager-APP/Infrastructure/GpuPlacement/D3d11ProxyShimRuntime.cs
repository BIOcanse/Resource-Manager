using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class D3d11ProxyShimRuntime(IHostEnvironment environment)
{
    public const string PolicyEnvironmentVariableName = "RM_GPU_SHIM_POLICY_FILE";
    public const string ProviderId = "d3d-device-create-shim";

    private const string LowPowerMode = "lowPower";
    private const string HighPerformanceMode = "highPerformance";
    private const string TargetLuidMode = "targetLuid";

    private readonly WindowsGpuAdapterOrderReader adapterReader = new();
    private readonly string shimBinaryPath = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", "d3d11.dll");
    private readonly string runtimeProviderBinaryPath = Path.Combine(
        AppContext.BaseDirectory,
        "GpuPlacementShim",
        WindowsGpuPlacementInjector.RuntimeProviderFileName);
    private readonly string policyRoot = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData",
        "GpuPlacement",
        "D3D11ProxyShim");

    public D3d11ProxyShimPreparation Prepare(D3d11ProxyShimPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var adapters = adapterReader.ReadInventory().Adapters;
            var target = ResolveTarget(request, adapters);
            return PublishPreparation(request.TargetId, request.AssignedPositionId, target);
        }
        catch (ExactGpuTargetUnavailableException ex)
        {
            return new D3d11ProxyShimPreparation(
                PolicyPrepared: false,
                ShimBinaryAvailable: File.Exists(shimBinaryPath),
                ShimBinaryPath: shimBinaryPath,
                RuntimeProviderBinaryAvailable: File.Exists(runtimeProviderBinaryPath),
                RuntimeProviderBinaryPath: runtimeProviderBinaryPath,
                PolicyPath: null,
                PolicyEnvironmentVariableName: PolicyEnvironmentVariableName,
                PolicyMode: TargetLuidMode,
                TargetLuid: null,
                TargetAdapterIndex: ex.AdapterIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TargetAdapterName: null,
                TargetResolution: ex.Resolution,
                ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            return new D3d11ProxyShimPreparation(
                PolicyPrepared: false,
                ShimBinaryAvailable: File.Exists(shimBinaryPath),
                ShimBinaryPath: shimBinaryPath,
                RuntimeProviderBinaryAvailable: File.Exists(runtimeProviderBinaryPath),
                RuntimeProviderBinaryPath: runtimeProviderBinaryPath,
                PolicyPath: null,
                PolicyEnvironmentVariableName: PolicyEnvironmentVariableName,
                PolicyMode: request.PreferIntegratedGpu ? LowPowerMode : HighPerformanceMode,
                TargetLuid: null,
                TargetAdapterIndex: null,
                TargetAdapterName: null,
                TargetResolution: "policy-write-failed",
                ErrorMessage: ex.Message);
        }
    }

    public D3d11ProxyShimPreparation PrepareExact(string targetId, ulong adapterKey)
    {
        var target = new D3d11ProxyShimTarget(TargetLuidMode,
            $"0x{(uint)(adapterKey >> 32):x8}_0x{(uint)adapterKey:x8}", null, null, "exact-planned-luid");
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
            ArgumentOutOfRangeException.ThrowIfZero(adapterKey);
            return PublishPreparation(targetId, $"gpu-luid:{adapterKey:x16}", target);
        }
        catch (Exception ex)
        {
            return new(false, File.Exists(shimBinaryPath), shimBinaryPath,
                File.Exists(runtimeProviderBinaryPath), runtimeProviderBinaryPath, null,
                PolicyEnvironmentVariableName, TargetLuidMode, target.TargetLuid, null, null,
                "exact-policy-not-prepared", ex.Message);
        }
    }

    internal bool RuntimeProviderAvailable => File.Exists(runtimeProviderBinaryPath);

    internal static byte[] CreateExactPolicyValue(ulong adapterKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(adapterKey);
        var target = new D3d11ProxyShimTarget(TargetLuidMode,
            $"0x{(uint)(adapterKey >> 32):x8}_0x{(uint)adapterKey:x8}", null, null, "exact-planned-luid");
        return Encoding.ASCII.GetBytes(BuildPolicyText(target, $"gpu-luid:{adapterKey:x16}"));
    }

    private D3d11ProxyShimPreparation PublishPreparation(
        string targetId, string assignedPositionId, D3d11ProxyShimTarget target)
    {
        var policyPath = GetPolicyPath(targetId);
        var policyText = BuildPolicyText(target, assignedPositionId);
        lock (policySync)
        {
            WritePolicyCore(policyPath, Encoding.ASCII.GetBytes(policyText));
        }
        return new(true, File.Exists(shimBinaryPath), shimBinaryPath,
            File.Exists(runtimeProviderBinaryPath), runtimeProviderBinaryPath, policyPath,
            PolicyEnvironmentVariableName, target.Mode, target.TargetLuid,
            target.AdapterIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.AdapterName, target.Resolution, null);
    }

    public static string BuildPolicyText(D3d11ProxyShimTarget target, string assignedPositionId)
    {
        ArgumentNullException.ThrowIfNull(target);

        var lines = new List<string>
        {
            "version=1",
            "provider=d3d-device-create-shim",
            $"mode={target.Mode}",
            $"assignedPositionId={SanitizePolicyValue(assignedPositionId)}"
        };

        if (!string.IsNullOrWhiteSpace(target.TargetLuid))
        {
            lines.Add($"targetLuid={SanitizePolicyValue(target.TargetLuid)}");
        }

        if (target.AdapterIndex is not null)
        {
            lines.Add($"targetAdapterIndex={target.AdapterIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(target.AdapterName))
        {
            lines.Add($"targetAdapterName={SanitizePolicyValue(target.AdapterName)}");
        }

        lines.Add($"targetResolution={SanitizePolicyValue(target.Resolution)}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static bool TryParseGpuPositionIndex(string? positionId, out int index)
    {
        index = -1;
        var clean = positionId?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return false;
        }

        if (clean.StartsWith("gpu:", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                clean[4..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index);
        }

        return clean.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                clean[3..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index);
    }

    internal static D3d11ProxyShimTarget ResolveTarget(
        D3d11ProxyShimPreparationRequest request,
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var assignedPositionId = request.AssignedPositionId?.Trim();
        var hasExactTargetSyntax = !string.IsNullOrWhiteSpace(assignedPositionId)
            && (assignedPositionId.StartsWith("gpu:", StringComparison.OrdinalIgnoreCase)
                || assignedPositionId.StartsWith("GPU", StringComparison.OrdinalIgnoreCase));
        if (hasExactTargetSyntax)
        {
            if (!TryParseGpuPositionIndex(assignedPositionId, out var gpuIndex))
            {
                throw new ExactGpuTargetUnavailableException(
                    "exact-target-invalid",
                    null,
                    $"配置的精确 GPU 目标 {assignedPositionId} 无效。");
            }

            var adapter = adapters.FirstOrDefault(adapter => adapter.Index == gpuIndex && !adapter.IsSoftware);
            if (adapter is null || IsZeroLuid(adapter.Luid))
            {
                throw new ExactGpuTargetUnavailableException(
                    "exact-target-unavailable",
                    gpuIndex,
                    $"配置的精确 GPU 目标 GPU{gpuIndex} 当前不可用。");
            }

            return new D3d11ProxyShimTarget(
                TargetLuidMode,
                FormatLuid(adapter.Luid),
                adapter.Index,
                adapter.Name,
                "exact-dxgi-luid");
        }

        if (request.PreferIntegratedGpu)
        {
            var adapter = adapters
                .Where(static adapter => !adapter.IsSoftware)
                .OrderBy(static adapter => adapter.DedicatedVideoMemoryBytes)
                .ThenBy(static adapter => adapter.Index)
                .FirstOrDefault();
            return new D3d11ProxyShimTarget(
                LowPowerMode,
                adapter is not null && !IsZeroLuid(adapter.Luid) ? FormatLuid(adapter.Luid) : null,
                adapter?.Index,
                adapter?.Name,
                adapter is null ? "low-power-class-fallback" : "low-power-adapter-fallback");
        }

        var highPerformance = adapters
            .Where(static adapter => !adapter.IsSoftware)
            .OrderByDescending(static adapter => adapter.DedicatedVideoMemoryBytes)
            .ThenBy(static adapter => adapter.Index)
            .FirstOrDefault();
        return new D3d11ProxyShimTarget(
            HighPerformanceMode,
            highPerformance is not null && !IsZeroLuid(highPerformance.Luid) ? FormatLuid(highPerformance.Luid) : null,
            highPerformance?.Index,
            highPerformance?.Name,
            highPerformance is null ? "high-performance-class-fallback" : "high-performance-adapter-fallback");
    }

    private static string FormatLuid(AdapterLuid luid)
    {
        return $"0x{(uint)luid.HighPart:x8}_0x{luid.LowPart:x8}";
    }

    private static bool IsZeroLuid(AdapterLuid luid)
    {
        return luid.LowPart == 0 && luid.HighPart == 0;
    }

    private static string CreateStableDirectoryName(string targetId)
    {
        var clean = string.IsNullOrWhiteSpace(targetId) ? "unknown-target" : targetId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizePolicyValue(string? value)
    {
        return string.Join(
            ' ',
            (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

public sealed record D3d11ProxyShimPreparationRequest(
    string TargetId,
    string DisplayName,
    string AssignedPositionId,
    bool PreferIntegratedGpu);

public sealed record D3d11ProxyShimTarget(
    string Mode,
    string? TargetLuid,
    int? AdapterIndex,
    string? AdapterName,
    string Resolution);

internal sealed class ExactGpuTargetUnavailableException(
    string resolution,
    int? adapterIndex,
    string message) : InvalidOperationException(message)
{
    public string Resolution { get; } = resolution;

    public int? AdapterIndex { get; } = adapterIndex;
}

public sealed record D3d11ProxyShimPreparation(
    bool PolicyPrepared,
    bool ShimBinaryAvailable,
    string? ShimBinaryPath,
    bool RuntimeProviderBinaryAvailable,
    string? RuntimeProviderBinaryPath,
    string? PolicyPath,
    string PolicyEnvironmentVariableName,
    string PolicyMode,
    string? TargetLuid,
    string? TargetAdapterIndex,
    string? TargetAdapterName,
    string TargetResolution,
    string? ErrorMessage)
{
    public IReadOnlyDictionary<string, string> ToMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["d3d11ProxyShimProvider"] = D3d11ProxyShimRuntime.ProviderId,
            ["d3d11ProxyShimPolicyPrepared"] = PolicyPrepared ? "true" : "false",
            ["d3d11ProxyShimBinaryAvailable"] = ShimBinaryAvailable ? "true" : "false",
            ["d3d11RuntimeProviderBinaryAvailable"] = RuntimeProviderBinaryAvailable ? "true" : "false",
            ["d3d11ProxyShimPolicyMode"] = PolicyMode,
            ["d3d11ProxyShimPolicyEnvironmentVariable"] = PolicyEnvironmentVariableName,
            ["d3d11ProxyShimTargetResolution"] = TargetResolution,
            ["d3d11ProxyShimLoadRequirement"] = "managed startup import-injects ResourceManager.GpuPlacementShim.dll before entry; runtime scheduling injects the same provider and configures the same process policy"
        };

        AddIfPresent(metadata, "d3d11ProxyShimBinaryPath", ShimBinaryPath);
        AddIfPresent(metadata, "d3d11RuntimeProviderBinaryPath", RuntimeProviderBinaryPath);
        AddIfPresent(metadata, "d3d11ProxyShimPolicyPath", PolicyPath);
        AddIfPresent(metadata, "d3d11ProxyShimTargetLuid", TargetLuid);
        AddIfPresent(metadata, "d3d11ProxyShimTargetAdapterIndex", TargetAdapterIndex);
        AddIfPresent(metadata, "d3d11ProxyShimTargetAdapterName", TargetAdapterName);
        AddIfPresent(metadata, "d3d11ProxyShimError", ErrorMessage);
        return metadata;
    }

    private static void AddIfPresent(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }
}
