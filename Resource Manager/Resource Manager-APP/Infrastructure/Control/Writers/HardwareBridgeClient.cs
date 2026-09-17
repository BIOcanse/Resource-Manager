using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 硬件写入辅助进程的客户端。
///
/// 那个进程单独一个仓库、单独一个许可证（GPL-3.0），因为它链接 ZenStates-Core，
/// 而本程序是 Apache-2.0 —— 直接引用会把整个程序传染成 GPL。
/// 顺带的好处是隔离：碰内核的代码崩了不拖累主程序。
///
/// **它只暴露操作，不做判断。** 写哪几项、算不算成功、越界怎么办、撤销恢复到哪儿，
/// 都在这一侧决定；它回的是原始 SMU 状态码和写完之后的回读值。
/// 两边各存一份策略迟早会对不上，所以策略只有一个属主。
///
/// 通道是标准输入输出上的一行一条 JSON：请求按 <c>id</c> 配对，一次一条，
/// 不并发 —— 底下是一颗 CPU 的 SMU 邮箱，本来就只能一条一条来。
/// </summary>
public sealed class HardwareBridgeClient(
    IHostEnvironment environment,
    ILogger<HardwareBridgeClient>? logger = null) : IDisposable
{
    private const string ComponentId = "hardware-bridge";
    private const string ExecutableName = "ResourceManager.HardwareBridge.exe";

    /// <summary>
    /// 一条请求等多久。SMU 邮箱握手最坏要一秒上下，读 PM table 还要再来一次，
    /// 留够余量但不能无限等 —— 卡住的话整个控制页都会跟着卡。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private long nextRequestId;
    private bool disposed;

    /// <summary>这台机器上装没装这个辅助进程。</summary>
    public bool IsInstalled => ResolveExecutablePath() is not null;

    /// <summary>
    /// 发一条请求，等它的应答。
    ///
    /// 进程没起来就先起。它要是中途死了，下一次调用会重新拉起来 ——
    /// 这类进程偶尔被杀（驱动、杀软）是常态，不该让整条能力从此消失。
    /// </summary>
    public async Task<JsonElement?> SendAsync(
        string operation,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var running = EnsureStarted();
            if (running is null)
            {
                return null;
            }

            var id = Interlocked.Increment(ref nextRequestId);
            var request = new Dictionary<string, object?>(arguments ?? new Dictionary<string, object?>())
            {
                ["id"] = id,
                ["op"] = operation
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            await running.StandardInput
                .WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            var line = await running.StandardOutput
                .ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                // 对面关掉了输出，说明它已经退出。丢掉这次，下次重新拉起。
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
            logger?.LogWarning(error, "硬件写入辅助进程这次没应答（{Operation}）。", operation);
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
            logger?.LogWarning(error, "起不来硬件写入辅助进程。");
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
                // 关掉标准输入就是让它自己退 —— 它读到流尾就结束。
                // 杀进程是最后手段：SMU 邮箱写到一半被打断不是好事。
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // 已经没了，正是我们想要的结果。
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
        // 跟着主程序一起走，不留后台。
        Stop();
        gate.Dispose();
    }
}
