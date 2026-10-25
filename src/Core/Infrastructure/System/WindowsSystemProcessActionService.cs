using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.LocalSystem;

public sealed class WindowsSystemProcessActionService(
    IWebHostEnvironment environment,
    IInteractiveUserSessionBroker sessionBroker,
    ILogger<WindowsSystemProcessActionService>? logger = null) : ISystemProcessActionService
{
    private const int MaxProcessOperationCount = 64;
    private const int MiniDumpWithFullMemory = 0x00000002;

    private readonly object dumpGate = new();
    private readonly string packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);

    public LocalOnlineSearchResult SearchOnline(LocalOnlineSearchRequest request, SoftwareOperationCaller caller)
    {
        UserSessionCallerValidation.RequireActiveUser(caller, sessionBroker);
        var query = CleanQuery(request.Query);
        var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
        var shellRequest = new UserShellExecuteRequest(url, "");
        _ = sessionBroker.LaunchUserShell(caller.WindowsSessionId, caller.WindowsSid,
            NativeUiLaunchHostedService.ResolveNativeUiPath(),
            UserShellExecuteRequest.Argument + " " + shellRequest.Encode());

        return new LocalOnlineSearchResult(query, url);
    }

    public LocalPathOpenResult OpenProperties(LocalPathPropertiesRequest request, SoftwareOperationCaller caller)
    {
        UserSessionCallerValidation.RequireActiveUser(caller, sessionBroker);
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException("路径不能为空。");
        }

        var normalizedPath = NormalizeExistingPath(request.Path);
        var shellRequest = new UserShellExecuteRequest(normalizedPath, "", "properties");
        _ = sessionBroker.LaunchUserShell(caller.WindowsSessionId, caller.WindowsSid,
            NativeUiLaunchHostedService.ResolveNativeUiPath(),
            UserShellExecuteRequest.Argument + " " + shellRequest.Encode());

        return new LocalPathOpenResult(
            normalizedPath,
            normalizedPath,
            "Properties");
    }

    public SystemProcessOperationResult TerminateProcesses(SystemProcessOperationRequest request, SafeAccessTokenHandle callerToken)
    {
        DemandAdministrator(callerToken);
        var targets = NormalizeProcessTargets(request.Targets);
        var items = new List<SystemProcessOperationItem>();
        foreach (var target in targets)
        {
            items.Add(TerminateProcess(target, callerToken, logger));
        }

        return new SystemProcessOperationResult(SystemProcessAction.Terminate,
            items);
    }

    public SystemProcessOperationResult CreateProcessDumps(SystemProcessOperationRequest request, SafeAccessTokenHandle callerToken)
    {
        var callerSid = DemandAdministrator(callerToken);
        var targets = NormalizeProcessTargets(request.Targets);
        var items = new List<SystemProcessOperationItem>();
        string dumpDirectory;
        lock (dumpGate)
        {
            dumpDirectory = ProcessDumpDirectoryAccess.Prepare(packageRoot, callerSid);
            foreach (var target in targets)
            {
                items.Add(CreateProcessDump(target, dumpDirectory, callerToken, logger));
            }
        }

        return new SystemProcessOperationResult(SystemProcessAction.Dump,
            items,
            dumpDirectory);
    }

    internal static SystemProcessOperationItem TerminateProcess(NormalizedProcessTarget target, SafeAccessTokenHandle callerToken,
        ILogger<WindowsSystemProcessActionService>? logger = null)
    {
        var processId = target.ProcessId;
        if (processId == Environment.ProcessId)
        {
            return OperationItem(target, "ResourceManager", "Skipped", SystemProcessOutcome.BackendProtected);
        }
        if (processId <= 4)
        {
            return OperationItem(target, "System", "Skipped", SystemProcessOutcome.CriticalProcessProtected);
        }

        var handle = IntPtr.Zero;
        try
        {
            string? processName = null;
            var mismatch = false;
            handle = WindowsIdentity.RunImpersonated(callerToken, () => OpenExactProcess(
                target, NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessTerminate,
                out processName, out mismatch));
            if (handle == IntPtr.Zero)
            {
                return OperationItem(
                    target,
                    processName,
                    "Skipped",
                    mismatch ? SystemProcessOutcome.IdentityChanged : SystemProcessOutcome.ProcessExited);
            }

            // Use the same identity-checked effect handle; an unreadable critical flag is a failure.
            if (ReadProcessCriticality(handle))
            {
                return OperationItem(target, processName, "Skipped", SystemProcessOutcome.CriticalProcessProtected);
            }
            if (!NativeMethods.TerminateProcess(handle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return OperationItem(target, processName, "Succeeded", SystemProcessOutcome.Terminated);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Process termination failed for {ProcessId} ({ProcessStartKey}).", target.ProcessId, target.ProcessStartKey);
            return OperationItem(target, null, "Failed", FailureOutcome(ex), nativeErrorCode: (ex as Win32Exception)?.NativeErrorCode);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    internal static SystemProcessOperationItem CreateProcessDump(
        NormalizedProcessTarget target,
        string dumpDirectory,
        SafeAccessTokenHandle callerToken,
        ILogger<WindowsSystemProcessActionService>? logger = null)
    {
        var processId = target.ProcessId;
        var handle = IntPtr.Zero;
        try
        {
            string? processName = null;
            var mismatch = false;
            handle = WindowsIdentity.RunImpersonated(callerToken, () => OpenExactProcess(
                target, NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead,
                out processName, out mismatch));
            if (handle == IntPtr.Zero)
            {
                return OperationItem(
                    target,
                    processName,
                    "Skipped",
                    mismatch ? SystemProcessOutcome.IdentityChanged : SystemProcessOutcome.ProcessExited);
            }

            var fileName = $"{SanitizeFileName(processName ?? "process")}_{processId}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.dmp";
            var dumpPath = Path.Combine(dumpDirectory, fileName);
            using var file = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var ok = NativeMethods.MiniDumpWriteDump(
                handle,
                processId,
                file.SafeFileHandle.DangerousGetHandle(),
                MiniDumpWithFullMemory,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return OperationItem(target, processName, "Succeeded", SystemProcessOutcome.DumpCreated, dumpPath);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Process dump failed for {ProcessId} ({ProcessStartKey}).", target.ProcessId, target.ProcessStartKey);
            return OperationItem(target, null, "Failed", FailureOutcome(ex), nativeErrorCode: (ex as Win32Exception)?.NativeErrorCode);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    private static SystemProcessOutcome FailureOutcome(Exception error)
        => error switch
        {
            UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => SystemProcessOutcome.AccessDenied,
            Win32Exception => SystemProcessOutcome.NativeFailure,
            IOException => SystemProcessOutcome.FileFailure,
            _ => SystemProcessOutcome.RequestRejected
        };

    private static string CleanQuery(string? query)
    {
        var value = query?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("搜索内容不能为空。")
            : value;
    }

    internal static bool ReadProcessCriticality(IntPtr handle)
        => NativeMethods.IsProcessCritical(handle, out var critical)
            ? critical
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static string DemandAdministrator(SafeAccessTokenHandle callerToken)
    {
        using var identity = new WindowsIdentity(callerToken.DangerousGetHandle());
        if (identity.User is not { } sid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException();
        return sid.Value;
    }

    internal static NormalizedProcessTarget[] NormalizeProcessTargets(
        IReadOnlyList<SystemProcessIdentity>? targets)
    {
        var candidates = (targets ?? [])
            .Select(static target => TryNormalizeProcessTarget(target))
            .Where(static target => target is not null)
            .Select(static target => target!.Value)
            .Distinct()
            .GroupBy(static target => target.ProcessId)
            .Where(static group => group.Select(target => target.ProcessStartKey).Distinct().Take(2).Count() == 1)
            .Select(static group => group.First())
            .Take(MaxProcessOperationCount)
            .ToArray();
        return candidates.Length == 0
            ? throw new InvalidOperationException("没有可操作的进程。")
            : candidates;
    }

    private static NormalizedProcessTarget? TryNormalizeProcessTarget(
        SystemProcessIdentity? target)
        => target is not null
            && target.ProcessId > 0
            && ulong.TryParse(
                target.ProcessStartKey,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processStartKey)
            && processStartKey > 0
                ? new NormalizedProcessTarget(target.ProcessId, processStartKey)
                : null;

    private static IntPtr OpenExactProcess(
        NormalizedProcessTarget target,
        uint access,
        out string? processName,
        out bool identityMismatch)
    {
        processName = null;
        identityMismatch = false;
        var handle = NativeMethods.OpenProcess(access, false, target.ProcessId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 6 or 87 or 1168)
            {
                return IntPtr.Zero;
            }

            throw new Win32Exception(error);
        }

        if (!NativeMethods.GetProcessTimes(
                handle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(handle);
            throw new Win32Exception(error);
        }
        if (creationTime.ToUInt64() != target.ProcessStartKey)
        {
            identityMismatch = true;
            NativeMethods.CloseHandle(handle);
            return IntPtr.Zero;
        }

        processName = TryReadProcessName(handle);
        return handle;
    }

    private static string? TryReadProcessName(IntPtr handle)
    {
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref length)
            ? Path.GetFileNameWithoutExtension(buffer.ToString(0, length))
            : null;
    }

    private static SystemProcessOperationItem OperationItem(
        NormalizedProcessTarget target,
        string? processName,
        string state,
        SystemProcessOutcome outcome,
        string? path = null, int? nativeErrorCode = null)
        => new(
            target.ProcessId,
            processName,
            state,
            outcome,
            path,
            target.ProcessStartKey.ToString(
                System.Globalization.CultureInfo.InvariantCulture), nativeErrorCode);

    private static string NormalizeExistingPath(string path)
    {
        var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            throw new InvalidOperationException("路径不存在。");
        }

        return normalized;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "process" : sanitized;
    }

    internal readonly record struct NormalizedProcessTarget(
        int ProcessId,
        ulong ProcessStartKey);
}
