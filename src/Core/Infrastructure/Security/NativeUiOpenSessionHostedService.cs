using System.ComponentModel;
using System.IO.Pipes;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.Security;

/// <summary>
/// Owns only the explicit Launcher-to-backend UI opening pipe. It does not issue or
/// transport backend API keys.
/// </summary>
public sealed class NativeUiOpenSessionHostedService(
    IInteractiveUserSessionBroker sessions,
    HostManagerRuntimeIdentity runtime,
    ILogger<NativeUiOpenSessionHostedService> logger) : BackgroundService
{
    private const int SessionScanIntervalMilliseconds = 5000;
    private readonly Dictionary<uint, Worker> workers = [];
    private long nextConnection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var active = sessions.GetActiveSessionIds()
                        .Where(static id => id > 0)
                        .ToHashSet();
                    foreach (var id in workers.Keys.ToArray())
                    {
                        var worker = workers[id];
                        if (active.Contains(id)
                            && !worker.Task.IsCompleted
                            && sessions.GetUserSid(id) == worker.Sid)
                        {
                            continue;
                        }
                        await worker.StopAsync();
                        workers.Remove(id);
                    }

                    foreach (var id in active)
                    {
                        if (workers.ContainsKey(id) || sessions.GetUserSid(id) is not { } sid)
                        {
                            continue;
                        }
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        workers.Add(id, new(sid, stop, ServeSessionAsync(id, sid, stop.Token)));
                    }
                }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
                {
                    logger.LogWarning("Cannot prepare a Native UI open-session pipe ({ErrorType}).", error.GetType().Name);
                }
                await Task.Delay(SessionScanIntervalMilliseconds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var worker in workers.Values)
            {
                await worker.StopAsync();
            }
            workers.Clear();
        }
    }

    internal async Task ServeConnectionAsync(
        NamedPipeServerStream pipe,
        int expectedSession,
        string expectedSid,
        string expectedLauncher,
        string expectedNativeUi,
        CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var caller = new WindowsNamedPipeCallerAttester().Attest(
                pipe,
                runtime.InstanceId,
                checked((ulong)Interlocked.Increment(ref nextConnection)));
            var operation = new byte[1];
            await pipe.ReadExactlyAsync(operation, deadline.Token);
            if (operation[0] != NativeUiOpenSessionContract.OpenOperation)
            {
                throw new InvalidDataException("Unsupported Native UI open-session operation.");
            }
            var background = await NativeUiOpenSessionContract.ReadRequestAsync(pipe, deadline.Token);
            var processId = NativeUiLaunchHostedService.OpenInterface(
                sessions,
                caller,
                checked((uint)expectedSession),
                expectedSid,
                Path.GetFullPath(expectedLauncher),
                Path.GetFullPath(expectedNativeUi),
                background);
            logger.LogInformation(
                "Explicitly opened standard-user Native UI in Windows session {SessionId}, PID {ProcessId}.",
                expectedSession,
                processId);
            await pipe.WriteAsync(new byte[] { NativeUiOpenSessionContract.Accepted }, deadline.Token);
            await pipe.FlushAsync(deadline.Token);

            // The Launcher validates the accepted byte and closes before the pipe is reused.
            var end = new byte[1];
            if (await pipe.ReadAsync(end, deadline.Token) != 0)
            {
                throw new InvalidDataException("Unexpected bytes after the Native UI open request.");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            if (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Native UI open-session request was rejected ({ErrorType}).", error.GetType().Name);
            }
        }
        finally
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
    }

    private async Task ServeSessionAsync(uint session, string sid, CancellationToken stoppingToken)
    {
        var launcherPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "ResourceManagerLauncher",
            "ResourceManager.Launcher.exe"));
        var nativeUiPath = NativeUiLaunchHostedService.ResolveNativeUiPath();
        try
        {
            using var pipe = WindowsLocalPipeFactory.Create(
                NativeUiOpenSessionContract.PipeName(session),
                $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})",
                4096,
                4096);
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ServeConnectionAsync(
                    pipe,
                    checked((int)session),
                    sid,
                    launcherPath,
                    nativeUiPath,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogWarning("Native UI open-session pipe for session {SessionId} stopped ({ErrorType}).",
                session,
                error.GetType().Name);
        }
    }

    private sealed record Worker(string Sid, CancellationTokenSource Stop, Task Task)
    {
        internal async Task StopAsync()
        {
            await Stop.CancelAsync();
            try
            {
                await Task;
            }
            catch (OperationCanceledException)
            {
            }
            Stop.Dispose();
        }
    }
}
