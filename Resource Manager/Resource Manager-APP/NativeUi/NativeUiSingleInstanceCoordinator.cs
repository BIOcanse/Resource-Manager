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
    private CancellationTokenSource? listenCancellation;
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
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!ownsMutex || listenCancellation is not null)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenCancellation = cancellation;
        _ = Task.Run(() => ListenAsync(cancellation.Token), CancellationToken.None);
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
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken);
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

        var cancellation = Interlocked.Exchange(ref listenCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
}
