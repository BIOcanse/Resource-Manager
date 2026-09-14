using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector(
    ILogger<WindowsGpuPlacementInjector> logger)
{
    public const string RuntimeProviderFileName = "ResourceManager.GpuPlacementShim.dll";
    public const string StartupBootstrapFileName = "ResourceManager.GpuPlacementBootstrap.dll";
    public const string ConfigureExportName = "ResourceManagerGpuPlacementConfigure";
    internal const string ConfigureVulkanExportName = "ResourceManagerGpuPlacementConfigureVulkan";
    internal const string ConfigureOpenGlExportName = "ResourceManagerGpuPlacementConfigureOpenGl";
    internal const string ReadObservationsExportName = "ResourceManagerGpuPlacementReadDeviceObservations";

    private const uint ProviderStatusConfigured = 1;
    private const uint ProviderStatusInitialized = 2;
    private const uint ProviderStatusHooksEnabled = 4;
    private const uint ProviderStatusD3D9HooksEnabled = 8;
    private const uint ProviderStatusVulkanHooksEnabled = 16;
    private const uint ProviderStatusOpenGlHooksEnabled = 32;

    internal static uint RequiredProviderStatus(GpuGraphicsApi api) => api switch
    {
        GpuGraphicsApi.D3D9 => ProviderStatusConfigured | ProviderStatusInitialized | ProviderStatusHooksEnabled | ProviderStatusD3D9HooksEnabled,
        GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)
            => ProviderStatusConfigured | ProviderStatusInitialized | ProviderStatusHooksEnabled,
        GpuGraphicsApi.Vulkan => ProviderStatusConfigured | ProviderStatusInitialized | ProviderStatusVulkanHooksEnabled,
        GpuGraphicsApi.OpenGL => ProviderStatusConfigured | ProviderStatusInitialized | ProviderStatusOpenGlHooksEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(api))
    };

    private readonly ConcurrentDictionary<int, CachedInjectionFailure> failuresByProcessId = new();
    private readonly string runtimeProviderPath = Path.Combine(
        AppContext.BaseDirectory,
        "GpuPlacementShim",
        RuntimeProviderFileName);

    public string RuntimeProviderPath => runtimeProviderPath;

    internal Task<ProviderProcess> OpenAndConfigureAsync(
        GpuPlacementProcessInstance target,
        GpuGraphicsApi api,
        string policyPath,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        CancellationToken cancellationToken)
    {
        if (api == GpuGraphicsApi.OpenGL)
            throw new ArgumentException("OpenGL requires explicitly prepared callbacks.", nameof(api));
        return OpenAndConfigureCoreAsync(target, api, policyPath, null, execute, cancellationToken);
    }

    internal Task<ProviderProcess> OpenAndConfigureOpenGlAsync(
        GpuPlacementProcessInstance target,
        string policyPath,
        PreparedOpenGlCallbacks source,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        CancellationToken cancellationToken)
        => OpenAndConfigureCoreAsync(target, GpuGraphicsApi.OpenGL, policyPath,
            OpenGlConfigureArguments.Encode(policyPath, source), execute, cancellationToken);

    private Task<ProviderProcess> OpenAndConfigureCoreAsync(
        GpuPlacementProcessInstance target,
        GpuGraphicsApi api,
        string policyPath,
        byte[]? configureArguments,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        CancellationToken cancellationToken)
    {
        _ = RequiredProviderStatus(api);
        return OpenProviderProcessAsync(target, execute,
            (handle, owner, token) => EnsureLoadedAndConfiguredCoreAsync(handle, owner, api, policyPath, configureArguments, token),
            cancellationToken);
    }

    private async Task<ProviderProcess> OpenProviderProcessAsync(
        GpuPlacementProcessInstance target,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        Func<SafeKernelHandle, ProviderProcess, CancellationToken, Task<RuntimeGpuProviderInjectionResult>> prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(execute);
        cancellationToken.ThrowIfCancellationRequested();
        var processId = target.ProcessId;
        if (processId <= 4 || processId == Environment.ProcessId)
        {
            return new(target, Failed(processId, "invalid-target", "目标不是可注入的普通用户态进程。"), execute);
        }
        if (target.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(target.ExecutablePath)
            || !Path.IsPathFullyQualified(target.ExecutablePath))
        {
            return new(target, Failed(processId, "process-identity-invalid", "目标缺少完整的进程创建身份或绝对路径。"), execute);
        }

        var processHandle = NativeMethods.OpenProcess(NativeMethods.RequiredProcessAccess, false, processId);
        if (processHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            processHandle.Dispose();
            return new(target, Failed(processId, "open-process-failed", new Win32Exception(error).Message, error), execute);
        }
        try
        {
            var owner = new ProviderProcess(target, Failed(processId, "not-prepared", "尚未准备。"), execute, processHandle);
            owner.Result = CheckProcessIdentity(processHandle, target)
                ?? await PrepareVerifiedProcessAsync(processHandle, owner, prepare, cancellationToken).ConfigureAwait(false);
            return owner;
        }
        catch
        {
            processHandle.Dispose();
            throw;
        }
    }

    private async Task<RuntimeGpuProviderInjectionResult> PrepareVerifiedProcessAsync(
        SafeKernelHandle processHandle, ProviderProcess owner,
        Func<SafeKernelHandle, ProviderProcess, CancellationToken, Task<RuntimeGpuProviderInjectionResult>> prepare,
        CancellationToken cancellationToken)
    {
        var target = owner.Identity;
        var processId = target.ProcessId;
        if (failuresByProcessId.TryGetValue(processId, out var cachedFailure)
            && cachedFailure.ProcessStartKey == target.ProcessStartKey)
        {
            return cachedFailure.Result with
            {
                Status = "retry-suppressed-until-process-restart",
                Message = $"同一进程实例此前注入失败，本轮不重复尝试：{cachedFailure.Result.Status} ({cachedFailure.Result.Message})"
            };
        }

        failuresByProcessId.TryRemove(processId, out _);
        RuntimeGpuProviderInjectionResult result;
        try
        {
            result = await prepare(processHandle, owner, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            result = Failed(processId, "native-process-query-failed", ex.Message, ex.NativeErrorCode);
        }
        if (result.Success)
        {
            failuresByProcessId.TryRemove(processId, out _);
        }
        else if (!owner.CallsStopped && !cancellationToken.IsCancellationRequested)
        {
            failuresByProcessId[processId] = new CachedInjectionFailure(
                target.ProcessStartKey,
                result);
        }

        return result;
    }

    private async Task<RuntimeGpuProviderInjectionResult> EnsureLoadedAndConfiguredCoreAsync(
        SafeKernelHandle processHandle,
        ProviderProcess owner,
        GpuGraphicsApi api,
        string policyPath,
        byte[]? configureArguments,
        CancellationToken cancellationToken)
    {
        var processId = owner.Identity.ProcessId;
        if (string.IsNullOrWhiteSpace(policyPath) || !File.Exists(policyPath))
        {
            return Failed(processId, "policy-missing", "进程专属 GPU 策略文件不存在。");
        }

        if (CheckRuntimeProviderEligibility(processHandle, owner.Identity) is { } rejected) return rejected;

        var binding = SelectProvider(api, AppContext.BaseDirectory, name => FindRemoteModule(processId, name));
        if (!File.Exists(binding.Path))
            return Failed(processId, "provider-missing", $"运行时 GPU Provider 不存在：{binding.Path}");
        var alreadyLoaded = binding.LoadedModule is not null;
        var observedModule = binding.LoadedModule;
        if (!alreadyLoaded)
        {
            var loadResult = await LoadProviderAsync(owner, binding.Path, GpuRemoteCallKind.LoadProvider, cancellationToken).ConfigureAwait(false);
            if (!loadResult.Success)
            {
                return loadResult;
            }
            observedModule = FindRemoteModuleWithRetry(processId, Path.GetFileName(binding.Path));
            if (observedModule is null)
            {
                return Failed(processId, "provider-not-observed", "远程加载返回后仍未观察到 GPU Provider 模块。");
            }
        }
        var providerModule = observedModule!.Value;

        if (!PathsMatch(providerModule.Path, binding.Path))
        {
            return Failed(processId, "provider-path-mismatch", "目标已加载同名但不同路径的 Provider，不调用其导出地址。");
        }

        if (!PortableExecutableExportReader.TryGetExportRva(
                binding.Path,
                binding.ConfigureExport,
                out var configureRva,
                out var exportError))
        {
            return Failed(processId, "configure-export-missing", exportError);
        }

        var configureAddress = providerModule.BaseAddress + checked((int)configureRva);
        if (PortableExecutableExportReader.TryGetExportRva(binding.Path, ReadObservationsExportName, out var readRva, out _))
            owner.ReadObservationsAddress = providerModule.BaseAddress + checked((int)readRva);
        var configureResult = await owner.InvokeAsync(
            GpuRemoteCallKind.ConfigureProvider,
            configureAddress,
            configureArguments ?? Encoding.Unicode.GetBytes(Path.GetFullPath(policyPath) + '\0'), false,
            cancellationToken).ConfigureAwait(false);
        if (!configureResult.Completed)
        {
            return Failed(
                processId,
                configureResult.Status,
                configureResult.Status,
                configureResult.NativeError);
        }

        var providerStatus = configureResult.ExitCode!.Value;
        var requiredStatus = binding.RequiredStatus;
        if ((providerStatus & requiredStatus) != requiredStatus)
        {
            return new RuntimeGpuProviderInjectionResult(
                processId,
                false,
                alreadyLoaded,
                "provider-not-ready",
                $"GPU Provider 已加载但 hook 未就绪，状态位 0x{providerStatus:x}。",
                binding.Path,
                policyPath,
                providerStatus,
                null);
        }

        return new RuntimeGpuProviderInjectionResult(
            processId,
            true,
            alreadyLoaded,
            alreadyLoaded ? "configured" : "injected-and-configured",
            alreadyLoaded
                ? "运行时 GPU Provider 已存在，已更新进程策略。"
                : "运行时 GPU Provider 已注入并完成 hook 配置。",
            binding.Path,
            policyPath,
            providerStatus,
            null);
    }

    private async Task<RuntimeGpuProviderInjectionResult> LoadProviderAsync(
        ProviderProcess owner, string providerPath, GpuRemoteCallKind kind, CancellationToken cancellationToken)
    {
        var processId = owner.Identity.ProcessId;
        if (FindRemoteModule(processId, "kernel32.dll") is not { } remoteKernel32)
        {
            return Failed(processId, "kernel32-not-found", "无法解析目标进程 kernel32.dll。" );
        }

        var localKernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
        var localLoadLibrary = NativeMethods.GetProcAddress(localKernel32, "LoadLibraryW");
        if (localKernel32 == IntPtr.Zero || localLoadLibrary == IntPtr.Zero)
        {
            return Failed(processId, "load-library-not-found", "无法解析本机 LoadLibraryW。" );
        }

        var loadLibraryOffset = localLoadLibrary.ToInt64() - localKernel32.ToInt64();
        var remoteLoadLibrary = new IntPtr(remoteKernel32.BaseAddress.ToInt64() + loadLibraryOffset);
        var call = await owner.InvokeAsync(
            kind,
            remoteLoadLibrary,
            Encoding.Unicode.GetBytes(providerPath + '\0'), false,
            cancellationToken).ConfigureAwait(false);
        if (!call.Completed)
        {
            return Failed(processId, call.Status, call.Status, call.NativeError);
        }

        logger.LogInformation(
            "Loaded runtime GPU provider into PID {ProcessId}; remote LoadLibrary result 0x{ExitCode:x}.",
            processId,
            call.ExitCode);
        return new RuntimeGpuProviderInjectionResult(
            processId,
            true,
            false,
            "provider-loaded",
            "运行时 GPU Provider 已加载。",
            providerPath,
            null,
            call.ExitCode!.Value,
            null);
    }

    private static bool IsCompatibleNativeX64Process(
        SafeKernelHandle processHandle,
        out string reason)
    {
        if (!Environment.Is64BitProcess)
        {
            reason = "当前 Resource Manager 不是 x64 进程。";
            return false;
        }
        if (!NativeMethods.IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
        {
            reason = "无法读取目标进程架构。";
            return false;
        }

        var nativeX64 = nativeMachine == NativeMethods.ImageFileMachineAmd64;
        var targetNative = processMachine == NativeMethods.ImageFileMachineUnknown;
        reason = nativeX64 && targetNative
            ? string.Empty
            : $"当前 Provider 只支持原生 x64 目标（processMachine=0x{processMachine:x4}, nativeMachine=0x{nativeMachine:x4}）。";
        return nativeX64 && targetNative;
    }

    private static bool TryReadExecutablePath(SafeKernelHandle processHandle, out string path)
    {
        var capacity = 32768;
        var buffer = new StringBuilder(capacity);
        if (!NativeMethods.QueryFullProcessImageName(processHandle, 0, buffer, ref capacity))
        {
            path = string.Empty;
            return false;
        }

        path = buffer.ToString();
        return path.Length > 0;
    }

    private static bool IsWindowsSystemPath(string path)
    {
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(windowsRoot)
            && Path.GetFullPath(path).StartsWith(
                windowsRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeGpuProviderInjectionResult Failed(
        int processId,
        string status,
        string message,
        int? win32Error = null)
    {
        return new RuntimeGpuProviderInjectionResult(
            processId,
            false,
            false,
            status,
            message,
            null,
            null,
            0,
            win32Error);
    }

    private sealed record CachedInjectionFailure(
        ulong ProcessStartKey,
        RuntimeGpuProviderInjectionResult Result);
}

public sealed record RuntimeGpuProviderInjectionResult(
    int ProcessId,
    bool Success,
    bool AlreadyLoaded,
    string Status,
    string Message,
    string? ProviderPath,
    string? PolicyPath,
    uint ProviderStatus,
    int? Win32Error);
