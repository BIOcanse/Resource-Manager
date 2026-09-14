using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.LocalSystem;

public sealed class WindowsSystemProcessActionService(IWebHostEnvironment environment) : ISystemProcessActionService
{
    private const int MaxProcessOperationCount = 64;
    private const int MiniDumpWithFullMemory = 0x00000002;

    private readonly object dumpGate = new();
    private readonly string packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);

    public LocalOnlineSearchResult SearchOnline(LocalOnlineSearchRequest request)
    {
        var query = CleanQuery(request.Query);
        var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        return new LocalOnlineSearchResult(query, url, "已用默认浏览器打开在线搜索。");
    }

    public LocalPathOpenResult OpenProperties(LocalPathPropertiesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException("路径不能为空。");
        }

        var normalizedPath = NormalizeExistingPath(request.Path);
        Process.Start(new ProcessStartInfo
        {
            FileName = normalizedPath,
            Verb = "properties",
            UseShellExecute = true
        });

        return new LocalPathOpenResult(
            normalizedPath,
            normalizedPath,
            "Properties",
            "已打开属性窗口。");
    }

    public SystemProcessOperationResult TerminateProcesses(SystemProcessOperationRequest request)
    {
        var targets = NormalizeProcessTargets(request.Targets);
        var items = new List<SystemProcessOperationItem>();
        foreach (var target in targets)
        {
            items.Add(TerminateProcess(target));
        }

        var succeeded = items.Count(static item => item.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase));
        return new SystemProcessOperationResult(
            succeeded == items.Count
                ? $"已结束 {succeeded} 个进程。"
                : $"已结束 {succeeded} / {items.Count} 个进程。",
            items);
    }

    public SystemProcessOperationResult CreateProcessDumps(SystemProcessOperationRequest request)
    {
        var targets = NormalizeProcessTargets(request.Targets);
        var dumpDirectory = Path.Combine(packageRoot, "Misc", "ProcessDumps");
        Directory.CreateDirectory(dumpDirectory);

        var items = new List<SystemProcessOperationItem>();
        lock (dumpGate)
        {
            foreach (var target in targets)
            {
                items.Add(CreateProcessDump(target, dumpDirectory));
            }
        }

        var succeeded = items.Count(static item => item.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase));
        return new SystemProcessOperationResult(
            succeeded == items.Count
                ? $"已创建 {succeeded} 个转储文件。"
                : $"已创建 {succeeded} / {items.Count} 个转储文件。",
            items,
            dumpDirectory);
    }

    private SystemProcessOperationItem TerminateProcess(NormalizedProcessTarget target)
    {
        var processId = target.ProcessId;
        if (processId == Environment.ProcessId)
        {
            return OperationItem(target, "ResourceManager", "Skipped", "拒绝结束 Resource Manager 后端进程。");
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = OpenExactProcess(
                target,
                NativeMethods.ProcessQueryLimitedInformation
                    | NativeMethods.ProcessTerminate,
                out var processName,
                out var mismatch);
            if (handle == IntPtr.Zero)
            {
                return OperationItem(
                    target,
                    processName,
                    "Skipped",
                    mismatch ? "进程实例已变化，未执行操作。" : "进程不存在或已经退出。");
            }

            if (!NativeMethods.TerminateProcess(handle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return OperationItem(target, processName, "Succeeded", "已结束进程。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return OperationItem(target, null, "Failed", ex.Message);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    private SystemProcessOperationItem CreateProcessDump(
        NormalizedProcessTarget target,
        string dumpDirectory)
    {
        var processId = target.ProcessId;
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenExactProcess(
                target,
                NativeMethods.ProcessQueryInformation
                    | NativeMethods.ProcessVmRead,
                out var processName,
                out var mismatch);
            if (handle == IntPtr.Zero)
            {
                return OperationItem(
                    target,
                    processName,
                    "Skipped",
                    mismatch ? "进程实例已变化，未创建转储。" : "进程不存在或已经退出。");
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

            return OperationItem(target, processName, "Succeeded", "已创建内存转储文件。", dumpPath);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return OperationItem(target, null, "Failed", ex.Message);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    private static string CleanQuery(string? query)
    {
        var value = query?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("搜索内容不能为空。")
            : value;
    }

    private static NormalizedProcessTarget[] NormalizeProcessTargets(
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
        string message,
        string? path = null)
        => new(
            target.ProcessId,
            processName,
            state,
            message,
            path,
            target.ProcessStartKey.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

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

    private readonly record struct NormalizedProcessTarget(
        int ProcessId,
        ulong ProcessStartKey);
}
