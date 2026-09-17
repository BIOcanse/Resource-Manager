using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 风扇控制核心的客户端。
///
/// 那个核心单独一个仓库（Apache-2.0，和本程序同一许可证），因为它要能被别人直接用 ——
/// 笔记本风扇控制这件事不该每个软件重造一遍。它按**控制通道**分类
/// （hwmon / WMI-ACPI / HID / Raw EC / SMM）而不按品牌，要维护的资产是机型 profile。
///
/// 通道和硬件写入辅助进程是同一套：标准输入输出上一行一条 JSON，随主程序启停。
/// 用独立进程而不是引一个库，是因为**碰风扇的代码崩了不该拖累主程序**，
/// 而且那个核心自己带一条不可协商的规矩：退出时把风扇交还固件。
/// </summary>
public sealed class FanControlCoreClient(
    IHostEnvironment environment,
    ILogger<FanControlCoreClient>? logger = null) : IDisposable
{
    private const string ComponentId = "fan-control-core";
    private const string ExecutableName = "FanControlCore.exe";

    /// <summary>
    /// 一条请求等多久。读一次要走十几次 EC 往返（每个字节还要读三遍取一致），
    /// 所以比一般的 IPC 宽松，但不能无限等 —— 卡住的话整个控制页都会跟着卡。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private long nextRequestId;
    private bool disposed;

    public bool IsInstalled => ResolveExecutablePath() is not null;

    public async Task<JsonElement?> SendAsync(
        string operation,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EnsureStarted() is not { } running)
            {
                return null;
            }

            var request = new Dictionary<string, object?>(
                arguments ?? new Dictionary<string, object?>())
            {
                ["id"] = Interlocked.Increment(ref nextRequestId),
                ["op"] = operation
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            await running.StandardInput
                .WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            var line = await running.StandardOutput.ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                Stop();
                return null;
            }
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            logger?.LogWarning(error, "风扇控制核心这次没应答（{Operation}）。", operation);
            Stop();
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private Process? EnsureStarted()
    {
        if (process is { HasExited: false })
        {
            return process;
        }
        Stop();

        if (ResolveExecutablePath() is not { } executablePath)
        {
            return null;
        }
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process;
        }
        catch (Exception error) when (error is IOException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "起不来风扇控制核心。");
            process = null;
            return null;
        }
    }

    private string? ResolveExecutablePath()
    {
        var path = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies",
            ComponentId,
            ExecutableName);
        return File.Exists(path) ? path : null;
    }

    private void Stop()
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                // **关标准输入而不是杀进程**：那个核心读到流尾会把风扇交还固件再退出。
                // 直接杀掉会把风扇留在我们设的转速上 —— 那正是它要避免的事。
                process.StandardInput.Close();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // 已经没了，正是想要的结果。
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Stop();
        gate.Dispose();
    }
}
