using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// H2 removes the existing managed subscription invocation; R-resource removes
// borrowed ProcessFields projection. Replay and sample decisions are native.
public sealed partial class KernelLeakProcessSampleSource(IResourceBreakdownPushSource processes) : IKernelLeakSampleSource
{
    public const string HandleField = "process_all_handle_count_actual";
    public async IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = ResourceBreakdownSampleRequest.ForResourceTable([HandleField], new Dictionary<string, string>());
        var state = default(State);
        await foreach (var snapshot in processes.SubscribeAsync(holder, request, interval, cancellationToken))
        {
            var frame = Project(snapshot, ref state);
            if (frame is not null) yield return frame;
        }
    }

    private static unsafe KernelLeakSampleFrame? Project(ResourceBreakdownSnapshot snapshot, ref State state)
    {
        var context = new Context(snapshot);
        var reference = GCHandle.Alloc(context);
        Result result;
        try
        {
            fixed (char* status = snapshot.Sampling.Status)
            {
                var input = new Capture
                {
                    SnapshotTicks = snapshot.CapturedAt.UtcTicks,
                    FieldTicks = snapshot.ProcessFields?.CapturedAt?.UtcTicks ?? 0,
                    HasFieldTime = snapshot.ProcessFields?.CapturedAt is null ? 0u : 1u,
                    RowCount = checked((uint)(snapshot.ProcessFields?.Processes.Count ?? 0)),
                    Status = new() { Pointer = (IntPtr)status, Length = (nuint)snapshot.Sampling.Status.Length }
                };
                CaptureNative(ref state, in input, GCHandle.ToIntPtr(reference), &QueryRow, &Accept, out result);
            }
            context.Failure?.Throw();
        }
        finally { context.ReleaseKey(); reference.Free(); }
        return result.Disposition switch
        {
            0 => null,
            1 => new(snapshot.CapturedAt, false, []),
            2 => new(snapshot.ProcessFields!.CapturedAt!.Value, result.Available != 0, context.Samples)
                { UnavailableHolderCount = checked((int)result.UnavailableCount), UnavailableHolders = context.Unavailable },
            3 => throw new OverflowException("Kernel leak input capacity exceeded."),
            _ => throw new InvalidDataException("Kernel leak input projection callback failed.")
        };
    }

    private sealed class Context(ResourceBreakdownSnapshot snapshot)
    {
        internal readonly ResourceBreakdownSnapshot Snapshot = snapshot;
        internal readonly List<KernelLeakSample> Samples = [];
        internal readonly List<KernelLeakUnavailableHolder> Unavailable = [];
        internal ExceptionDispatchInfo? Failure;
        internal GCHandle Key;
        internal void ReleaseKey() { if (Key.IsAllocated) Key.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint QueryRow(IntPtr pointer, nuint index, RawRow* output)
    {
        var context = (Context)GCHandle.FromIntPtr(pointer).Target!;
        try
        {
            context.ReleaseKey();
            var process = context.Snapshot.ProcessFields!.Processes[checked((int)index)];
            var value = process.Values.FirstOrDefault(item => item.Field == HandleField)?.IntegerValue;
            var key = process.CreationWindowsFileTime100Ns;
            var text = default(Text);
            if (key is not null)
            {
                context.Key = GCHandle.Alloc(key, GCHandleType.Pinned);
                text = new() { Pointer = context.Key.AddrOfPinnedObject(), Length = (nuint)key.Length };
            }
            *output = new()
            {
                ProcessId = process.ProcessId,
                HasCount = value.HasValue ? 1u : 0u,
                StartKey = text,
                Negative = value?.Negative == true ? 1u : 0u,
                HighBits = value?.HighBits ?? 0,
                LowBits = value?.LowBits ?? 0
            };
            return 0;
        }
        catch (Exception error) { context.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Accept(IntPtr pointer, Emission* input)
    {
        var context = (Context)GCHandle.FromIntPtr(pointer).Target!;
        try
        {
            var key = input->HasBirth != 0 ? Encoding.ASCII.GetString(input->Key, checked((int)input->KeyLength)) : null;
            if (input->Unavailable != 0) context.Unavailable.Add(new(input->ProcessId, key));
            else context.Samples.Add(new(input->ProcessId, key!, KernelLeakSampleKind.ProcessHandles, input->Value));
            return 0;
        }
        catch (Exception error) { context.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State { internal long LastTicks, HasLast; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Text { internal IntPtr Pointer; internal nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Capture
    {
        internal long SnapshotTicks, FieldTicks;
        internal uint HasFieldTime, RowCount;
        internal Text Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawRow
    {
        internal int ProcessId;
        internal uint HasCount;
        internal Text StartKey;
        internal uint Negative, Reserved;
        internal ulong HighBits, LowBits;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Emission
    {
        internal int ProcessId;
        internal uint Unavailable;
        internal ulong Birth, Value;
        internal fixed byte Key[20];
        internal uint KeyLength, HasBirth;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { internal uint Disposition, Available, FromFields, UnavailableCount; }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_input_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial void CaptureNative(ref State state, in Capture capture, IntPtr context,
        delegate* unmanaged[Cdecl]<IntPtr, nuint, RawRow*, uint> query,
        delegate* unmanaged[Cdecl]<IntPtr, Emission*, uint> sink, out Result result);
}
