using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.NativeUi;

internal sealed record ResidentWindowMessage(string Type, bool Flag = false, long Generation = 0, string? Value = null);

/// <summary>A private parent/child pipe. Loss of either direction withdraws the parent's demand.</summary>
internal sealed class ResidentWindowPipe : IDisposable, IAsyncDisposable
{
    internal const int MaximumLineBytes = 16 * 1024;
    private readonly Stream input;
    private readonly Stream output;
    private readonly int cancelPollMs;
    private readonly Channel<Outbound> outgoing = Channel.CreateBounded<Outbound>(64);
    private readonly object ioSync = new();
    private SafeWaitHandle? readThread;
    private SafeWaitHandle? writeThread;
    private Task? reading;
    private readonly Task writing;
    private Task? ioDisposal;
    private int disposed;

    internal ResidentWindowPipe(Stream input, Stream output, int cancelPollMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancelPollMs);
        this.input = input; this.output = output;
        this.cancelPollMs = cancelPollMs;
        writing = Task.Factory.StartNew(Write, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    internal static ResidentWindowPipe? FromArguments(IReadOnlyList<string> args)
    {
        string? read = null, write = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] is not ("--resident-pipe-read" or "--resident-pipe-write")) continue;
            var option = args[index];
            if (++index == args.Count || !ulong.TryParse(args[index], out var handle) || handle == 0)
                throw new ArgumentException("A resident pipe handle is missing or invalid.");
            if (option == "--resident-pipe-read")
            {
                if (read is not null) throw new ArgumentException("Duplicate resident pipe handle.");
                read = args[index];
            }
            else
            {
                if (write is not null) throw new ArgumentException("Duplicate resident pipe handle.");
                write = args[index];
            }
        }
        if (read is null && write is null) return null;
        if (read is null || write is null || read == write) throw new ArgumentException("Both distinct resident pipe handles are required.");
        int? cancelPollMs = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] != "--resident-cancel-poll-ms") continue;
            if (cancelPollMs is not null || ++index == args.Count || !int.TryParse(args[index], out var value) || value <= 0)
                throw new ArgumentException("A positive resident I/O cancellation poll interval is required.");
            cancelPollMs = value;
        }
        if (cancelPollMs is null) throw new ArgumentException("The resident I/O cancellation poll interval is required with pipe handles.");
        var input = new AnonymousPipeClientStream(PipeDirection.In, read);
        try { return new(input, new AnonymousPipeClientStream(PipeDirection.Out, write), cancelPollMs.Value); }
        catch { input.Dispose(); throw; }
    }

    internal static string? ApiBaseFromArguments(IReadOnlyList<string> args)
    {
        string? result = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] != "--resident-api-base") continue;
            if (result is not null || ++index == args.Count || !Uri.TryCreate(args[index], UriKind.Absolute, out var address)
                || address.Scheme != "http" || !BackendProcessConnection.IsLoopbackHost(address.Host)
                || address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new ArgumentException("A loopback resident API base address is required.");
            result = address.AbsoluteUri;
        }
        return result;
    }

    // Receive and dispatch are serial, so backend generations cannot overtake each other.
    internal Task RunAsync(Func<ResidentWindowMessage, Task> receive)
    {
        lock (ioSync)
        {
            if (reading is not null) throw new InvalidOperationException("The resident pipe already has a reader.");
            if (Volatile.Read(ref disposed) != 0) return receive(new("parent-wants-me-running", false));
            // Inherited anonymous handles are synchronous. Own their blocking read
            // thread so stopping can cancel its I/O without touching pool threads.
            return reading = Task.Factory.StartNew(() => Read(receive), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Read(Func<ResidentWindowMessage, Task> receive)
    {
        using var currentThread = OpenThread(0x0001, false, GetCurrentThreadId());
        if (currentThread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        lock (ioSync) readThread = currentThread;
        var buffer = new byte[MaximumLineBytes];
        var count = 0;
        try
        {
            while (Volatile.Read(ref disposed) == 0)
            {
                var length = input.Read(buffer, count, buffer.Length - count);
                if (length == 0) break;
                var end = count + length;
                var start = 0;
                for (var index = count; index < end; index++)
                {
                    if (buffer[index] != (byte)'\n') continue;
                    var message = Parse(buffer.AsMemory(start, index - start));
                    receive(message).GetAwaiter().GetResult();
                    start = index + 1;
                }
                count = end - start;
                if (count == buffer.Length) throw new InvalidDataException("Resident pipe line exceeds 16 KiB.");
                if (start != 0) Buffer.BlockCopy(buffer, start, buffer, 0, count);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or JsonException or InvalidDataException
            or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        { System.Diagnostics.Trace.WriteLine(error); }
        finally
        {
            try { receive(new("parent-wants-me-running", false)).GetAwaiter().GetResult(); }
            finally { lock (ioSync) readThread = null; }
        }
    }

    internal static ResidentWindowMessage Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new InvalidDataException("Missing resident message type.");
        return type switch
        {
            "parent-wants-me-running" => new(type, root.GetProperty("running").GetBoolean()),
            "stop" => new("parent-wants-me-running", false),
            "backend-session" when root.GetProperty("generation").GetInt64() >= 0 => new(type, root.GetProperty("ready").GetBoolean(),
                root.GetProperty("generation").GetInt64(), root.TryGetProperty("reason", out var reason) ? reason.GetString() : null),
            "language" => new(type, Value: root.GetProperty("language").GetString()),
            "visibility" => new(type, root.GetProperty("visible").GetBoolean()),
            "show-window" when root.GetProperty("command").GetString() is "show" or "show-passive" or "show-passive-secondary"
                => new(type, Value: root.GetProperty("command").GetString()),
            _ => throw new InvalidDataException("Unknown resident message.")
        };
    }

    internal Task SendAsync(string type, string? field = null, string? value = null)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.CompletedTask;
        // JSON escaping takes at most six bytes per UTF-8 input byte. Check
        // before allocating, then use a buffer that cannot expand past the wire cap.
        if (type.Length > MaximumLineBytes || field?.Length > MaximumLineBytes || value?.Length > MaximumLineBytes
            || Encoding.UTF8.GetByteCount(type) + (field is null ? 0 : Encoding.UTF8.GetByteCount(field))
                + (value is null ? 0 : Encoding.UTF8.GetByteCount(value)) > (MaximumLineBytes - 32) / 6)
            throw new InvalidDataException("Resident pipe message exceeds its bounded input size.");
        var bytes = new byte[MaximumLineBytes];
        using var memory = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: false);
        memory.SetLength(0);
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteString("type", type);
            if (field is not null) writer.WriteString(field, value);
            writer.WriteEndObject();
        }
        memory.WriteByte((byte)'\n');
        if (memory.Length > MaximumLineBytes) throw new InvalidDataException("Resident pipe line exceeds 16 KiB.");
        var message = new Outbound(bytes, checked((int)memory.Length), new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (outgoing.Writer.TryWrite(message)) return message.Completion.Task;
        if (Volatile.Read(ref disposed) != 0) return Task.CompletedTask;
        throw new InvalidOperationException("The bounded resident pipe output queue is full.");
    }

    private sealed record Outbound(byte[] Bytes, int Length, TaskCompletionSource Completion);

    private void Write()
    {
        using var currentThread = OpenThread(0x0001, false, GetCurrentThreadId());
        if (currentThread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        lock (ioSync) writeThread = currentThread;
        try
        {
            while (outgoing.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (outgoing.Reader.TryRead(out var message))
                {
                    try
                    {
                        if (Volatile.Read(ref disposed) == 0)
                        {
                            output.Write(message.Bytes.AsSpan(0, message.Length));
                            output.Flush();
                        }
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                    { System.Diagnostics.Trace.WriteLine(error); Dispose(); }
                    finally { message.Completion.TrySetResult(); }
                }
            }
        }
        finally { lock (ioSync) writeThread = null; }
    }

    public void Dispose()
    {
        lock (ioSync)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Volatile.Write(ref disposed, 1);
            outgoing.Writer.TryComplete();
            ioDisposal = CancelIoAndDisposeAsync(reading);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? disposal;
        lock (ioSync) disposal = ioDisposal;
        if (disposal is not null) await disposal.ConfigureAwait(false);
    }

    private async Task CancelIoAndDisposeAsync(Task? currentRead)
    {
        while (currentRead is { IsCompleted: false } || !writing.IsCompleted)
        {
            // Repeat only during shutdown to cover cancellation just before ReadFile
            // enters the kernel. The thread is private to this pipe for its whole life.
            lock (ioSync)
            {
                if (readThread is { } reader) _ = CancelSynchronousIo(reader);
                if (writeThread is { } writer) _ = CancelSynchronousIo(writer);
            }
            await Task.Delay(cancelPollMs).ConfigureAwait(false);
        }
        input.Dispose(); output.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelSynchronousIo(SafeWaitHandle thread);
}
