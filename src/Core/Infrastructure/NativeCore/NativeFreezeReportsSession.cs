using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.FreezeReports;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Byte-only transport. Native owns settings, persisted reports, classification and ring records.
// This CLR lifetime/JSON bridge retires with the H1/H3 native-host migration.
internal sealed unsafe partial class NativeFreezeReportsSession : IDisposable
{
    private readonly FreezeReportsWorkspaceBridge workspace;
    private GCHandle callbackRoot;
    private nint handle;
    internal event Action<uint>? Changed;

    internal NativeFreezeReportsSession(string userDataPath, ResourceManagerLedgerOwner ledger)
    {
        workspace = new(ledger);
        try
        {
            callbackRoot = GCHandle.Alloc(this);
            var path = Encoding.UTF8.GetBytes(userDataPath);
            var scratch = workspace.Callbacks;
            fixed (byte* pointer = path)
                Check(Create(pointer, (nuint)path.Length, null, &scratch, &OnChanged,
                    GCHandle.ToIntPtr(callbackRoot), out handle));
        }
        catch
        {
            if (callbackRoot.IsAllocated) callbackRoot.Free();
            workspace.Dispose();
            throw;
        }
    }

    internal double Setting(uint key) => ReadSetting(handle, key);
    internal void SetCommitThreshold(double threshold) => Lifecycle(2, threshold);
    internal void StartEvents() => Lifecycle(0);
    internal void StopEvents() => Lifecycle(3);
    internal void StartRecorder() => Lifecycle(1);
    internal void StopRecorder() => Lifecycle(4);
    private void Lifecycle(uint operation, double threshold = 0)
    {
        using var output = new RequestOutput();
        var root = GCHandle.Alloc(output);
        try
        {
            var result = LifecycleCore(handle, operation, threshold, &WriteError, GCHandle.ToIntPtr(root));
            output.Failure?.Throw();
            if (result == 0) return;
            if (output.Error.Length == 0) throw new NativeFreezeReportsException(result);
            using var body = JsonDocument.Parse(output.Error.ToArray());
            throw new NativeFreezeReportsException(result, body.RootElement.Clone());
        }
        finally { root.Free(); }
    }
    internal void Submit(ReadOnlySpan<byte> value)
    { fixed (byte* pointer = value) Check(SubmitCore(handle, pointer, (nuint)value.Length)); }
    internal void UpdateProcesses(ResourceBreakdownSnapshot snapshot)
    {
        var fields = snapshot.ProcessFields;
        var available = snapshot.Sampling.Status == ResourceBreakdownSamplingStatuses.Ready && fields?.CapturedAt is not null;
        var context = new ProcessFrameContext(available ? fields!.Processes : null);
        var root = GCHandle.Alloc(context);
        try
        {
            var result = ProcessFrameCore(handle, fields?.CapturedAt?.ToUnixTimeMilliseconds() ?? 0,
                available ? 1u : 0u, checked((nuint)(context.Rows?.Count ?? 0)), &ReadProcess, GCHandle.ToIntPtr(root));
            if (context.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            Check(result);
        }
        finally { root.Free(); }
    }
    // The original publication is borrowed only for this synchronous native call.
    private sealed class ProcessFrameContext(IReadOnlyList<ProcessMetricFieldValues>? rows)
    {
        internal readonly IReadOnlyList<ProcessMetricFieldValues>? Rows = rows;
        internal Exception? Failure;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadProcess(nint context, nuint index, uint* pid, ulong* commit, byte* image, nuint capacity, nuint* length)
    {
        ProcessFrameContext? frame = null;
        try
        {
            frame = (ProcessFrameContext)GCHandle.FromIntPtr(context).Target!;
            if (pid == null || commit == null || image == null || length == null) return -1;
            *pid = 0; *commit = 0; *length = 0;
            if (frame.Rows is not { } rows || index >= (nuint)rows.Count) return 1;
            var row = rows[checked((int)index)];
            if (row.ProcessId <= 0) return 1;
            if (row.Values.FirstOrDefault(value => value.Field == "process_all_private_commit_actual")?.IntegerValue
                is not { Negative: false, HighBits: 0 } bytes) return 1;
            var path = row.Values.FirstOrDefault(value => value.Field == "process_all_image_path_actual")?.StringValue;
            var name = Path.GetFileName(path.AsSpan());
            if (name.IsEmpty || (nuint)Encoding.UTF8.GetByteCount(name) > capacity) return 1;
            *pid = checked((uint)row.ProcessId); *commit = bytes.LowBits;
            *length = checked((nuint)Encoding.UTF8.GetBytes(name, new Span<byte>(image, checked((int)capacity))));
            return 0;
        }
        catch (Exception error)
        {
            if (frame is not null) frame.Failure = error;
            return -1;
        }
    }
    internal byte[] Request(uint operation, ReadOnlySpan<byte> input = default)
    {
        using var output = new RequestOutput();
        var root = GCHandle.Alloc(output);
        try
        {
            int result;
            fixed (byte* pointer = input)
                result = RequestCore(handle, operation, pointer, (nuint)input.Length, &Write, &WriteError, GCHandle.ToIntPtr(root));
            output.Failure?.Throw();
            if (result != 0)
            {
                // Success may contain a partial listing. It is never parsed or
                // returned after native reports failure on its separate stream.
                if (output.Error.Length == 0) throw new NativeFreezeReportsException(result);
                using var body = JsonDocument.Parse(output.Error.ToArray());
                throw new NativeFreezeReportsException(result, body.RootElement.Clone());
            }
            return output.Success.ToArray();
        }
        finally { root.Free(); }
    }
    internal static (int StatusCode, JsonElement Body) DescribeError(int statusCode)
    {
        using var output = new RequestOutput();
        var root = GCHandle.Alloc(output);
        try
        {
            var result = ErrorResponseCore(statusCode, &WriteError, GCHandle.ToIntPtr(root));
            output.Failure?.Throw();
            using var body = JsonDocument.Parse(output.Error.ToArray());
            return (result, body.RootElement.Clone());
        }
        finally { root.Free(); }
    }
    private static void Check(int result)
    { if (result != 0) throw new NativeFreezeReportsException(result); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChanged(nint context, uint stream)
    {
        try { ((NativeFreezeReportsSession)GCHandle.FromIntPtr(context).Target!).Changed?.Invoke(stream); }
        catch { /* Never unwind managed exceptions through native event callbacks. */ }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(nint context, byte* bytes, nuint length)
        => Transfer(context, bytes, length, error: false);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteError(nint context, byte* bytes, nuint length)
        => Transfer(context, bytes, length, error: true);
    private static int Transfer(nint context, byte* bytes, nuint length, bool error)
    {
        RequestOutput? output = null;
        try
        {
            output = (RequestOutput)GCHandle.FromIntPtr(context).Target!;
            (error ? output.Error : output.Success).Write(new ReadOnlySpan<byte>(bytes, checked((int)length)));
            return 0;
        }
        catch (Exception failure)
        {
            if (output is not null) output.Failure = ExceptionDispatchInfo.Capture(failure);
            return -1;
        }
    }
    private sealed class RequestOutput : IDisposable
    {
        internal readonly MemoryStream Success = new();
        internal readonly MemoryStream Error = new();
        internal ExceptionDispatchInfo? Failure;
        public void Dispose() { Success.Dispose(); Error.Dispose(); }
    }
    public void Dispose()
    {
        if (handle == 0) return;
        Destroy(handle); handle = 0;
        callbackRoot.Free();
        workspace.Dispose();
    }
    private const string Library = "ResourceManager.NativeCore";
    [LibraryImport(Library, EntryPoint = "rm_freeze_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Create(byte* path, nuint length, NativeKernelEtwHostAllocatorCallbacks* allocator,
        FreezeReportsWorkspaceBridge.ScratchCallbacks* scratch,
        delegate* unmanaged[Cdecl]<nint, uint, void> changed, nint context, out nint handle);
    [LibraryImport(Library, EntryPoint = "rm_freeze_setting")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial double ReadSetting(nint handle, uint key);
    [LibraryImport(Library, EntryPoint = "rm_freeze_lifecycle_with_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int LifecycleCore(nint handle, uint operation, double threshold,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> errorWriter, nint context);
    [LibraryImport(Library, EntryPoint = "rm_freeze_submit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SubmitCore(nint handle, byte* value, nuint length);
    [LibraryImport(Library, EntryPoint = "rm_freeze_process_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ProcessFrameCore(nint handle, long at, uint available, nuint count,
        delegate* unmanaged[Cdecl]<nint, nuint, uint*, ulong*, byte*, nuint, nuint*, int> reader, nint context);
    [LibraryImport(Library, EntryPoint = "rm_freeze_request_with_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RequestCore(nint handle, uint operation, byte* input, nuint length,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> writer,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> errorWriter, nint context);
    [LibraryImport(Library, EntryPoint = "rm_freeze_error_response")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ErrorResponseCore(int statusCode,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> errorWriter, nint context);
    [LibraryImport(Library, EntryPoint = "rm_freeze_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Destroy(nint handle);
}

public sealed class NativeFreezeReportsException : Exception
{
    public int StatusCode { get; }
    public JsonElement ErrorBody { get; }
    public string ReasonCode => ErrorBody.GetProperty("reasonCode").GetString()!;
    public string Detail => ErrorBody.GetProperty("detail").GetString()!;

    public NativeFreezeReportsException(int statusCode)
        : this(NativeFreezeReportsSession.DescribeError(statusCode)) { }
    private NativeFreezeReportsException((int StatusCode, JsonElement Body) failure)
        : this(failure.StatusCode, failure.Body) { }
    internal NativeFreezeReportsException(int statusCode, JsonElement body)
        : base($"{body.GetProperty("reasonCode").GetString()}: {body.GetProperty("detail").GetString()}")
    { StatusCode = statusCode; ErrorBody = body; }
}
