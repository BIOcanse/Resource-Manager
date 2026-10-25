using System.Diagnostics;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Helpers;

/// <summary>Serial JSON transport for the explicitly selected helper. The caller owns control policy.</summary>
public class HardwareBridgeProcessClient : IHardwareBridgeProcessClient
{
    private readonly Func<string?> resolveExecutablePath;
    private readonly HardwareBridgeProcessOptions options;
    private readonly Action<Exception?, string>? warning;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private Process? process;
    private HelperErrorOutput? errorOutput;
    private JsonElement? description;
    public string? UnavailableReason { get; private set; }
    private long nextRequestId;
    private volatile bool disposed;
    private volatile bool stopping;
    private readonly Func<ProcessStartInfo, Process?>? startFactory;

    public HardwareBridgeProcessClient(Func<string?> resolveExecutablePath, HardwareBridgeProcessOptions options,
        Action<Exception?, string>? warning = null, Func<ProcessStartInfo, Process?>? startFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolveExecutablePath);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestTimeout <= TimeSpan.Zero || options.DiagnosticDrainTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.resolveExecutablePath = resolveExecutablePath;
        this.options = options;
        this.warning = warning;
        this.startFactory = startFactory;
    }

    /// <summary>这台机器上装没装这个辅助进程。</summary>
    public bool IsInstalled => resolveExecutablePath() is not null;

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
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var commandStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = new CancellationTokenSource(options.RequestTimeout);
            Process? running;
            await inputGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed) return null;
                running = EnsureStarted();
                if (running is null || disposed) return null; // Startup may finish after Dispose.
                // CPU and iGPU share this transport and the same immutable session facts.
                if (operation == "describe" && description is { } known) return known;
            }
            finally
            {
                inputGate.Release();
                if (disposed || stopping) Stop();
            }

            var pendingOperation = description is null ? "describe" : operation;
            while (true)
            {
                var request = new Dictionary<string, object?>(pendingOperation == operation && arguments is not null
                    ? arguments : new Dictionary<string, object?>())
                {
                    ["id"] = Interlocked.Increment(ref nextRequestId),
                    ["op"] = pendingOperation
                };
                await inputGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    if (!commandStarted) cancellationToken.ThrowIfCancellationRequested();
                    if (disposed) return null;
                    commandStarted = true;
                    await running.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
                    await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    inputGate.Release();
                    if (disposed || stopping) Stop();
                }
                var line = await running.StandardOutput
                    .ReadLineAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    if (errorOutput is { } diagnostics)
                    {
                        try { await diagnostics.Completion.WaitAsync(options.DiagnosticDrainTimeout).ConfigureAwait(false); }
                        catch (TimeoutException) { }
                        UnavailableReason = diagnostics.FailureReason;
                        warning?.Invoke(null, diagnostics.Detail);
                    }
                    // 对面关掉了输出，说明它已经退出。丢掉这次，下次重新拉起。
                    Stop();
                    return null;
                }

                using var document = JsonDocument.Parse(line);
                var response = document.RootElement.Clone();
                if (pendingOperation == "describe") description = response;
                if (pendingOperation == operation) return response;
                // The internal handshake has completed. Cancellation still owns the
                // requested operation until its own JSON line starts below.
                commandStarted = false;
                pendingOperation = operation;
            }
        }
        catch (OperationCanceledException) when (!commandStarted && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            warning?.Invoke(error, operation);
            Stop();
            return null;
        }
        finally
        {
            gate.Release();
            if (disposed || stopping) Stop();
        }
    }

    /// <summary>Drain the owned session and hold new requests until the caller finishes replacing its files.</summary>
    public async ValueTask<IAsyncDisposable> SuspendForInstallationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var inputShutdownStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stopping = true;
            var running = process;
            if (running is not null)
            {
                await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!running.HasExited)
                    {
                        // Closing stdin is irreversible. Keep this process as the sole owner
                        // until it exits if the caller cancels or the drain times out below.
                        inputShutdownStarted = true;
                        running.StandardInput.Close();
                    }
                }
                finally { inputGate.Release(); }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.RequestTimeout);
                await running.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (errorOutput is { } diagnostics)
                    await diagnostics.Completion.WaitAsync(options.DiagnosticDrainTimeout, cancellationToken).ConfigureAwait(false);
                running.Dispose();
                process = null;
            }
            description = null;
            errorOutput = null;
            return new InstallationLease(this);
        }
        catch
        {
            // Before stdin is closed, a failed suspension can resume this helper. After
            // EOF, stopping remains set so EnsureStarted cannot start a replacement early.
            if (!inputShutdownStarted && !disposed) stopping = false;
            gate.Release();
            throw;
        }
    }

    private sealed class InstallationLease(HardwareBridgeProcessClient owner) : IAsyncDisposable
    {
        private HardwareBridgeProcessClient? current = owner;
        public ValueTask DisposeAsync()
        {
            var client = Interlocked.Exchange(ref current, null);
            if (client is not null)
            {
                client.stopping = false;
                client.gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private Process? EnsureStarted()
    {
        if (process is { HasExited: false })
        {
            return stopping ? null : process;
        }
        // Called under inputGate: an exited process has no pending input ownership.
        process?.Dispose();
        process = null;
        description = null;

        if (resolveExecutablePath() is not { } executablePath)
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process = startFactory is not null ? startFactory(start) : Process.Start(start);
            errorOutput = process is null ? null : new(process.StandardError, options.ErrorOutput);
            stopping = false;
            UnavailableReason = null;
            return process;
        }
        catch (Exception error) when (error is IOException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            warning?.Invoke(error, "helper-start-failed");
            process = null;
            return null;
        }
    }

    private void Stop()
    {
        // Close input only after the active writer has completed its accepted JSON line.
        stopping = true;
        if (!inputGate.Wait(0)) return;
        var running = process;
        try
        {
            if (running is null) return;
            if (!running.HasExited)
            {
                // 关掉标准输入就是让它自己退 —— 它读到流尾就结束。
                // 已发送的 SMU 命令必须完成，不能强杀或同时启动另一属主。
                running.StandardInput.Close();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // 已经没了，正是我们想要的结果。
        }
        finally
        {
            try
            {
                // Keep output available until the command has consumed its reply.
                if (running is { HasExited: true } && gate.Wait(0))
                {
                    try
                    {
                        running.Dispose();
                        process = null;
                        stopping = false;
                    }
                    finally { gate.Release(); }
                }
            }
            finally { inputGate.Release(); }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        // 关闭输入，辅助进程完成在途事务后自行退出。
        Stop();
        // In-flight requests still release this managed semaphore after EOF shutdown.
    }
}

public sealed record HardwareBridgeProcessOptions(TimeSpan RequestTimeout,
    TimeSpan DiagnosticDrainTimeout, HelperErrorOutputOptions ErrorOutput);
