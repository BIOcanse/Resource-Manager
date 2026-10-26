using System.IO.Pipes;
using System.Text;

namespace ResourceManager.NativeUi;

public sealed class NativeUiSingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\ResourceManager.NativeUi";
    private const string PipeName = "ResourceManager.NativeUi.Instance";

    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private readonly string pipeName;
    private readonly object listenSync = new();
    private CancellationTokenSource? listenCancellation;
    private Task? listenTask;
    private int disposed;

    private NativeUiSingleInstanceCoordinator(
        Mutex mutex,
        bool ownsMutex,
        string pipeName)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
        this.pipeName = pipeName;
    }

    public event EventHandler<string>? RequestReceived;

    public bool IsPrimary => ownsMutex;

    public static NativeUiSingleInstanceCoordinator Create()
        => Create(MutexName, PipeName);

    internal static NativeUiSingleInstanceCoordinator Create(
        string mutexName,
        string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        return new NativeUiSingleInstanceCoordinator(mutex, createdNew, pipeName);
    }

    public bool SignalRequest(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void StartListening(CancellationToken cancellationToken)
        => _ = RunAsync(cancellationToken);

    // The mutex belongs to the process; each invocation owns only the listener.
    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (listenSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!ownsMutex) return Task.CompletedTask;
            if (listenTask is { IsCompleted: false }) return listenTask;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            listenCancellation = cancellation;
            listenTask = ListenOwnedAsync(cancellation);
            return listenTask;
        }
    }

    public Task StopAsync()
    {
        lock (listenSync)
        {
            listenCancellation?.Cancel();
            return listenTask ?? Task.CompletedTask;
        }
    }

    private async Task ListenOwnedAsync(CancellationTokenSource cancellation)
    {
        try { await ListenAsync(cancellation.Token).ConfigureAwait(false); }
        finally
        {
            lock (listenSync)
            {
                if (ReferenceEquals(listenCancellation, cancellation)) listenCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                RequestReceived?.Invoke(this, string.IsNullOrWhiteSpace(command) ? "show" : command);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Final disposal is performed on the original mutex-owning thread,
        // after the caller has awaited StopAsync / RunAsync.
        _ = StopAsync();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
}
