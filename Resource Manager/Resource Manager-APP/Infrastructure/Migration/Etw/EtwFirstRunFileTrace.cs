using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.Migration.Etw;

public sealed class EtwFirstRunFileTraceFactory(
    IKernelEtwSessionBroker kernelEtwBroker) : IFirstRunFileTraceFactory
{
    public IFirstRunFileTrace Create()
    {
        return new EtwFirstRunFileTrace(kernelEtwBroker);
    }
}

public sealed class EtwFirstRunFileTrace(
    IKernelEtwSessionBroker kernelEtwBroker) : IFirstRunFileTrace
{
    private readonly object gate = new();
    private IKernelEtwSubscription? kernelSubscription;
    private bool stopped = true;

    public FirstRunTraceStartResult Start(FirstRunTraceOptions options, Action<FileTraceEventRecord> observe)
    {
        lock (gate)
        {
            if (!stopped)
            {
                return new FirstRunTraceStartResult(
                    false,
                    "WindowsETW",
                    "Unavailable",
                    "ETW 会话已经在运行。");
            }

            IKernelEtwSubscription? createdSubscription = null;
            try
            {
                var keywords = KernelTraceEventParser.Keywords.Process
                    | KernelTraceEventParser.Keywords.FileIO
                    | KernelTraceEventParser.Keywords.FileIOInit;
                createdSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    BuildSubscriptionId(options.SessionId),
                    keywords,
                    (kernel, _) => Subscribe(kernel, observe)));
                var brokerState = kernelEtwBroker.GetSnapshot();
                if (!brokerState.IsRunning)
                {
                    createdSubscription.Dispose();
                    return new FirstRunTraceStartResult(
                        false,
                        "WindowsETW",
                        brokerState.State,
                        $"{brokerState.Message} 已降级为目录窗口监控。");
                }

                kernelSubscription = createdSubscription;
                stopped = false;

                return new FirstRunTraceStartResult(
                    true,
                    "WindowsETW",
                    "Running",
                    "正在使用 Windows ETW 追踪首次运行窗口内的进程和文件写入事件。");
            }
            catch (Exception ex)
            {
                createdSubscription?.Dispose();
                return new FirstRunTraceStartResult(
                    false,
                    "WindowsETW",
                    "Unavailable",
                    $"无法启动 Windows ETW：{ex.Message}。已降级为目录窗口监控。");
            }
        }
    }

    public void Stop()
    {
        IKernelEtwSubscription? subscriptionToRelease;
        lock (gate)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
        }

        subscriptionToRelease?.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    private static void Subscribe(KernelTraceEventParser kernel, Action<FileTraceEventRecord> observe)
    {
        kernel.ProcessStartGroup += data => observe(CreateProcessRecord("ProcessStart", data));
        kernel.ProcessEndGroup += data => observe(CreateProcessRecord("ProcessStop", data));

        kernel.FileIOCreate += data => observe(CreateFileRecord("FileCreate", data.FileName, data.ProcessID, data.ProcessName, null, data.TimeStamp));
        kernel.FileIOFileCreate += data => observe(CreateFileRecord("FileCreateName", data.FileName, data.ProcessID, data.ProcessName, null, data.TimeStamp));
        kernel.FileIOWrite += data => observe(CreateFileRecord("FileWrite", data.FileName, data.ProcessID, data.ProcessName, data.IoSize, data.TimeStamp));
        kernel.FileIORename += data => observe(CreateFileRecord("FileRename", data.FileName, data.ProcessID, data.ProcessName, null, data.TimeStamp));
        kernel.FileIODelete += data => observe(CreateFileRecord("FileDelete", data.FileName, data.ProcessID, data.ProcessName, null, data.TimeStamp));
        kernel.FileIOFileDelete += data => observe(CreateFileRecord("FileDeleteName", data.FileName, data.ProcessID, data.ProcessName, null, data.TimeStamp));
    }

    private static FileTraceEventRecord CreateProcessRecord(string eventType, ProcessTraceData data)
    {
        var imagePath = SelectProcessImagePath(data);
        return new FileTraceEventRecord(
            "WindowsETW",
            eventType,
            imagePath,
            data.ProcessID,
            NormalizeProcessName(data.ProcessName, imagePath),
            data.ParentID,
            null,
            new DateTimeOffset(data.TimeStamp));
    }

    private static string? SelectProcessImagePath(ProcessTraceData data)
    {
        var candidates = new[] { data.ImageFileName, data.KernelImageFileName }
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToArray();

        return candidates.FirstOrDefault(LooksLikeFullPath)
            ?? candidates.OrderByDescending(static item => item.Length).FirstOrDefault();
    }

    private static bool LooksLikeFullPath(string value)
    {
        return value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':'
            || value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase);
    }

    private static FileTraceEventRecord CreateFileRecord(
        string eventType,
        string? path,
        int processId,
        string processName,
        long? sizeBytes,
        DateTime observedAt)
    {
        return new FileTraceEventRecord(
            "WindowsETW",
            eventType,
            path,
            processId,
            NormalizeProcessName(processName, null),
            null,
            sizeBytes,
            new DateTimeOffset(observedAt));
    }

    private static string NormalizeProcessName(string processName, string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(processName))
        {
            return Path.GetFileNameWithoutExtension(processName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            return Path.GetFileNameWithoutExtension(imagePath.Trim());
        }

        return "unknown";
    }

    private static string BuildSubscriptionId(string sessionId)
    {
        var safeId = new string(sessionId.Where(char.IsLetterOrDigit).Take(24).ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
        {
            safeId = Guid.NewGuid().ToString("N")[..12];
        }

        return $"first-run-file-trace:{safeId}";
    }
}
