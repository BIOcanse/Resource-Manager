using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.NativeUi;

// W0's narrow binding. W4 removes Task/UI/call plumbing. The Stream bridge is
// P-window/child-pipe's remaining OS bridge, also used by the old product tests.
// No queue, decoder, encoder or cancellation loop lives in managed code.
internal sealed unsafe class NativeResidentWindowPipe : IDisposable, IAsyncDisposable
{
    // Integration supplies the original effective values; there are no native
    // defaults. The shared Desktop DLL/project wiring is maintenance-owned.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Policy
    {
        internal uint QueueCapacity;
        internal uint CancelPollMs;
        internal nuint ReaderStackBytes;
        internal nuint WriterStackBytes;
        internal uint JsonMaxDepth;
        internal uint InputMarginBytes;
        internal uint InputEscapeMultiplier;
    }

    // Implement with the existing Ledger owner: acquire locally, call use once,
    // then release in finally. The provider is an owner handle, never a lease.
    internal delegate void WorkspaceScope(nuint bytes, Action<nint, nuint> use);

    private readonly object lifetime = new();
    private readonly Stream input;
    private readonly Stream output;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource readDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task run;
    private nint instance;
    private GCHandle root;
    private Func<ResidentWindowMessage, Task>? receive;
    private ExceptionDispatchInfo? readFailure;
    private ExceptionDispatchInfo? closeFailure;
    private bool finalReceiveFailed;
    private bool readerStarted;
    private bool disposed;

    internal NativeResidentWindowPipe(Stream input, Stream output, int cancelPollMs, Policy policy, WorkspaceScope workspace)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancelPollMs);
        this.input = input;
        this.output = output;
        policy.CancelPollMs = checked((uint)cancelPollMs);
        Check(Methods.WorkspaceBytes(in policy, out var bytes));
        root = GCHandle.Alloc(this);
        var callbacks = new Io { Read = &Read, Write = &Write, Close = &Close, Ready = &Ready, Sent = &Sent };
        try { Check(Methods.Create(in policy, in callbacks, GCHandle.ToIntPtr(root), out instance)); }
        catch { root.Free(); throw; }
        // This run method's local Ledger lease is the only asynchronous region
        // hold. Native returns only after both workers and callbacks have joined.
        run = Task.Factory.StartNew(() => RunWorkspace(workspace, bytes), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { run.GetAwaiter().GetResult(); throw; }
    }

    private void RunWorkspace(WorkspaceScope workspace, nuint bytes)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            workspace(bytes, (region, length) =>
            {
                if (region == 0 || length < bytes) throw new InvalidOperationException("The resident pipe requires its complete Ledger workspace.");
                Check(Methods.Run(instance, region, length));
            });
            closeFailure?.Throw();
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            readDone.TrySetException(error);
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            lock (lifetime)
            {
                // Destroy also closes an unstarted owner after acquisition fails.
                Check(Methods.Destroy(instance));
                instance = 0;
                root.Free();
            }
        }
        failure?.Throw();
    }

    internal Task RunAsync(Func<ResidentWindowMessage, Task> callback)
    {
        lock (lifetime)
        {
            if (readerStarted) throw new InvalidOperationException("The resident pipe already has a reader.");
            if (disposed || instance == 0) return callback(new("parent-wants-me-running", false));
            receive = callback;
            var receiver = new Receiver { Context = GCHandle.ToIntPtr(root), Receive = &Receive, Finished = &Finished };
            var result = Methods.Start(instance, in receiver);
            if (result == 1) return callback(new("parent-wants-me-running", false));
            Check(result);
            readerStarted = true;
            return readDone.Task;
        }
    }

    internal Task SendAsync(string type, string? field = null, string? value = null)
    {
        lock (lifetime)
        {
            if (disposed || instance == 0) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ticket = GCHandle.Alloc(completion);
            int result;
            // Only this synchronous encode call borrows the original UTF-16.
            // The queue contains its final native slot, never these pinned pointers.
            try
            {
                fixed (char* t = type, f = field, v = value)
                    result = Methods.Send(instance, new(t, type.Length), new(f, field?.Length ?? 0), new(v, value?.Length ?? 0), GCHandle.ToIntPtr(ticket));
            }
            catch { ticket.Free(); throw; }
            if (result == 0) return completion.Task;
            ticket.Free();
            return result switch
            {
                1 => Task.CompletedTask,
                2 => throw new InvalidOperationException("The bounded resident pipe output queue is full."),
                3 => throw new InvalidDataException("Resident pipe message exceeds its bounded input size."),
                _ => throw new InvalidOperationException($"Native resident pipe send failed ({result}).")
            };
        }
    }

    // Parse remains a synchronous facade for the existing product contract. Its
    // caller supplies scoped scratch; no temporary pointer crosses an await.
    internal static ResidentWindowMessage Parse(ReadOnlyMemory<byte> json, uint maxDepth, WorkspaceScope workspace)
    {
        ResidentWindowMessage? result = null;
        ExceptionDispatchInfo? failure = null;
        var sink = new ParseSink(message => result = message, error => failure = ExceptionDispatchInfo.Capture(error));
        var handle = GCHandle.Alloc(sink);
        try
        {
            Check(Methods.DecodeBytes(checked((nuint)json.Length), maxDepth, out var bytes));
            workspace(bytes, (region, length) =>
            {
                if (region == 0 || length < bytes) throw new InvalidOperationException("The resident decoder requires its complete scoped scratch.");
                using var pin = json.Pin();
                var receiver = new Receiver { Context = GCHandle.ToIntPtr(handle), Receive = &ParseReceive, Finished = null };
                var status = Methods.Decode((nint)pin.Pointer, checked((nuint)json.Length), region, length, maxDepth, in receiver);
                failure?.Throw();
                if (status != 0) throw new InvalidDataException("Unknown or invalid resident message.");
            });
            return result ?? throw new InvalidDataException("Missing resident message.");
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            if (instance != 0) Methods.Stop(instance);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(run);
    }

    private static NativeResidentWindowPipe Borrow(nint context) => (NativeResidentWindowPipe)GCHandle.FromIntPtr(context).Target!;
    private static bool IsOriginalReadRefusal(Exception error) => error is IOException or InvalidDataException or ObjectDisposedException or
        System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Read(nint context, byte* buffer, nuint length)
    {
        var self = Borrow(context);
        try { return self.input.Read(new Span<byte>(buffer, checked((int)length))); }
        catch (Exception error)
        {
            if (!IsOriginalReadRefusal(error)) self.readFailure = ExceptionDispatchInfo.Capture(error);
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(nint context, byte* buffer, nuint length)
    {
        var self = Borrow(context);
        try { self.output.Write(new ReadOnlySpan<byte>(buffer, checked((int)length))); self.output.Flush(); return 0; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(nint context)
    {
        var self = Borrow(context);
        // Preserve the original input-then-output sequence and async failure,
        // while preventing managed exceptions from crossing the native ABI.
        try { self.input.Dispose(); self.output.Dispose(); }
        catch (Exception error) { self.closeFailure = ExceptionDispatchInfo.Capture(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Ready(nint context, int status)
    {
        var self = Borrow(context);
        if (status == 0) self.ready.TrySetResult();
        else self.ready.TrySetException(new InvalidOperationException("Native resident pipe startup failed."));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Sent(nint ticket)
    {
        var handle = GCHandle.FromIntPtr(ticket);
        ((TaskCompletionSource)handle.Target!).TrySetResult();
        handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Receive(nint context, Message* message, uint final)
    {
        var self = Borrow(context);
        try
        {
            // Materialize only the original facade value. UI holds this object;
            // native still owns the decoded region until this Task has finished.
            self.receive!(ToManaged(*message)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            if (final != 0 || !IsOriginalReadRefusal(error)) self.readFailure = ExceptionDispatchInfo.Capture(error);
            if (final != 0) self.finalReceiveFailed = true;
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Finished(nint context, int status)
    {
        var self = Borrow(context);
        if (self.readFailure is { } failure) self.readDone.TrySetException(failure.SourceException);
        else if (self.finalReceiveFailed) self.readDone.TrySetException(new InvalidOperationException("Resident pipe withdrawal failed."));
        else self.readDone.TrySetResult();
    }

    private static ResidentWindowMessage ToManaged(Message message) => new(message.Kind switch
    {
        0 => "parent-wants-me-running", 1 => "backend-session", 2 => "language", 3 => "visibility", 4 => "show-window",
        _ => throw new InvalidDataException("Unknown resident message kind.")
    }, message.Flag != 0, message.Generation,
        message.Value == null ? null : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(message.Value, checked((int)message.ValueLength))));

    private sealed record ParseSink(Action<ResidentWindowMessage> Set, Action<Exception> Fail);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ParseReceive(nint context, Message* message, uint final)
    {
        var sink = (ParseSink)GCHandle.FromIntPtr(context).Target!;
        try { sink.Set(ToManaged(*message)); return 0; }
        catch (Exception error) { sink.Fail(error); return -1; }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Native resident pipe operation failed ({status}).");
    }
    internal static nuint LiveOwnerCount => Methods.LiveOwners();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Text(char* data, int length)
    {
        private readonly char* data = data;
        private readonly nuint length = checked((nuint)length);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal uint Kind, Flag;
        internal long Generation;
        internal byte* Value;
        internal nuint ValueLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Io
    {
        internal delegate* unmanaged[Cdecl]<nint, byte*, nuint, nint> Read;
        internal delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> Write;
        internal delegate* unmanaged[Cdecl]<nint, void> Close;
        internal delegate* unmanaged[Cdecl]<nint, int, void> Ready;
        internal delegate* unmanaged[Cdecl]<nint, void> Sent;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Receiver
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, Message*, uint, int> Receive;
        internal delegate* unmanaged[Cdecl]<nint, int, void> Finished;
    }
    private static class Methods
    {
        // All window slices share this maintenance-owned temporary output.
        private const string Library = "ResourceManager.Desktop.Native";
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_workspace_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WorkspaceBytes(in Policy config, out nuint length);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(in Policy config, in Io io, nint context, out nint instance);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_run", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Run(nint instance, nint region, nuint length);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_start", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Start(nint instance, in Receiver receiver);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_send", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Send(nint instance, Text type, Text field, Text value, nint completion);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_stop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Stop(nint instance);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Destroy(nint instance);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_live_owners", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint LiveOwners();
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_decode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Decode(nint input, nuint length, nint scratch, nuint scratchLength, uint maxDepth, in Receiver receiver);
        [DllImport(Library, EntryPoint = "rm_window_child_pipe_decode_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeBytes(nuint length, uint maxDepth, out nuint bytes);
    }
}
