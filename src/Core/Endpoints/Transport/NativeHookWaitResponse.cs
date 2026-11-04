using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

// Z-hook-wait's synchronous source borrow. It carries no hook/credential state,
// gate, serializer or fallback. H1 removes the ASP.NET/output binding;
// Z-codecs-finish removes the temporary managed hook-field callbacks.
internal static partial class NativeHookWaitResponse
{
    internal static Task SendError(HttpContext context, SubscriptionChannelRejection rejection, CancellationToken token)
        => TrySend(context, null, null, rejection, token, token);

    // The existing writer invokes this inside its gate, after admission checks.
    // The caller cancels the original lifetime only after a selected send completes.
    internal static Task<bool> TrySend(HttpContext context, string? id, ExpandedMonitoringHook? hook,
        object? value, CancellationToken deliveryToken, CancellationToken requestToken)
    {
        var result = Encode(context, id, hook, value);
        return result.Selected == 0 ? Task.FromResult(false)
            : FlushAsync(context.Response.BodyWriter, result.Rejection != 0 ? requestToken : deliveryToken);
    }

    private static async Task<bool> FlushAsync(PipeWriter output, CancellationToken token)
    {
        var result = await output.FlushAsync(token);
        if (result.IsCanceled) throw new OperationCanceledException(token);
        return true;
    }

    private static unsafe Response Encode(HttpContext context, string? id, ExpandedMonitoringHook? hook, object? value)
    {
        // Only a type discriminator crosses here. State matching and status
        // selection are native, using the original UTF-16 string synchronously.
        var state = (value as MonitoringHookEvent)?.State;
        fixed (char* pointer = state)
        {
            var input = new Input
            {
                Kind = value switch { SubscriptionChannelRejection => 1, MonitoringHookEvent => 2, MonitoringEventHookEvent => 3, _ => 0 },
                RejectionStatus = value is SubscriptionChannelRejection rejection ? checked((uint)rejection.StatusCode) : 0,
                State = (ushort*)pointer,
                StateLength = checked((nuint)(state?.Length ?? 0))
            };
            Check(NativeMethods.Select(in input, out var response));
            if (response.Selected == 0) return response;
            context.Response.StatusCode = checked((int)response.Status);
            context.Response.ContentType = "application/json; charset=utf-8";
            using var borrowed = new BorrowedResult(id, hook, value, context.Response.BodyWriter);
            var handle = GCHandle.Alloc(borrowed);
            try
            {
                var callbacks = new Callbacks { Field = &ReadField, Shape = &ReadShape };
                var output = new OutputCallbacks { Reserve = &Reserve, Commit = &Commit };
                var code = NativeMethods.EncodeStream(in input, GCHandle.ToIntPtr(handle), in callbacks, in output, out response);
                borrowed.Failure?.Throw();
                Check(code);
                return response;
            }
            finally
            {
                handle.Free();
                GC.KeepAlive(hook);
                GC.KeepAlive(value);
            }
        }
    }

    private static void Check(int code)
    {
        if (code != 0) throw new InvalidOperationException($"Native hook wait encoding failed ({code}).");
    }
    private static BorrowedResult Borrow(nint context) => (BorrowedResult)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadField(nint context, uint node, nuint index, uint field, nint writer)
    {
        var borrowed = Borrow(context);
        try { return borrowed.Field((Node)node, checked((int)index), field, writer); }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadShape(nint context, uint query, nuint* result)
    {
        var borrowed = Borrow(context);
        try { *result = checked((nuint)borrowed.Shape((Query)query)); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Reserve(nint context, nuint completed, byte** pointer, nuint* capacity)
    {
        var borrowed = Borrow(context);
        try
        {
            borrowed.Commit(completed);
            var memory = borrowed.Output.GetMemory();
            borrowed.Pin = memory.Pin(); borrowed.Pinned = true; borrowed.Capacity = memory.Length;
            *pointer = (byte*)borrowed.Pin.Pointer; *capacity = checked((nuint)memory.Length); return 0;
        }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(nint context, nuint completed)
    {
        var borrowed = Borrow(context);
        try { borrowed.Commit(completed); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    // No flattened DTO or copied array: each access reaches the same immutable
    // producer object. This call-scoped owner is gone before the first await.
    private sealed class BorrowedResult(string? id, ExpandedMonitoringHook? hook, object? value, PipeWriter output) : IDisposable
    {
        internal PipeWriter Output => output;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal int Capacity;
        internal ExceptionDispatchInfo? Failure;
        private MonitoringHookEvent Metric => (MonitoringHookEvent)value!;
        private MonitoringEventHookEvent EventValue => (MonitoringEventHookEvent)value!;
        private HostMemoryActionRecord Action => EventValue.Event.Action!;
        private HostMemoryProtectionLiveStatus Status => EventValue.Status;
        private SubscriptionChannelRejection Rejection => (SubscriptionChannelRejection)value!;

        internal void Commit(nuint completed)
        {
            if (completed > checked((nuint)Capacity)) throw new InvalidOperationException("Native segment extent exceeds its borrow.");
            if (Pinned)
            {
                Pin.Dispose(); Pinned = false; Capacity = 0;
                output.Advance(checked((int)completed));
            }
            else if (completed != 0) throw new InvalidOperationException("No output segment is borrowed.");
        }
        internal int Shape(Query query) => query switch
        {
            Query.RulePresent => hook!.Rule is null ? 0 : 1,
            Query.IntervalPresent => hook!.IntervalMs == 0 ? 0 : 1,
            Query.EventPresent => hook!.Event is null ? 0 : 1,
            Query.RuleSoftwarePresent => hook!.Rule!.SoftwareId is null ? 0 : 1,
            Query.RuleProcessPresent => hook!.Rule!.ProcessId is null ? 0 : 1,
            Query.RuleStartPresent => hook!.Rule!.StartKey is null ? 0 : 1,
            Query.RuleDevicePresent => hook!.Rule!.DeviceIdentity is null ? 0 : 1,
            Query.RejectionPositionPresent => Rejection.Position is null ? 0 : 1,
            Query.RejectionReasonPresent => Rejection.Reason is null ? 0 : 1,
            Query.OwnerCount => Metric.Owners.Count,
            Query.ActionPresent => EventValue.Event.Action is null ? 0 : 1,
            Query.TreePresent => Action.TreeRoot is null ? 0 : 1,
            Query.DumpCount => Action.DumpPaths.Count,
            Query.CommitPresent => Status.CurrentCommit is null ? 0 : 1,
            Query.EntryCount => Status.Reserve.Entries.Count,
            Query.ExclusionCount => Status.ExcludedCandidates.Count,
            Query.ProtectedCount => Status.ProtectedProcesses.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };

        internal int Field(Node node, int index, uint field, nint w) => node switch
        {
            Node.Root when field == 0 => Write(w, id),
            Node.Hook => field switch { 0 => Write(w, hook!.Id), 1 => Write(w, hook!.IntervalMs), 2 => Write(w, hook!.Event), _ => Invalid() },
            Node.Rule => field switch
            {
                0 => Write(w, hook!.Rule!.Binding), 1 => Write(w, hook!.Rule!.MetricId), 2 => Write(w, hook!.Rule!.Quantity),
                3 => Write(w, hook!.Rule!.Comparison), 4 => Write(w, hook!.Rule!.Threshold), 5 => Write(w, hook!.Rule!.HoldSeconds),
                6 => Write(w, hook!.Rule!.TopOwners), 7 => Write(w, hook!.Rule!.SoftwareId), 8 => Write(w, hook!.Rule!.ProcessId),
                9 => Write(w, hook!.Rule!.StartKey), 10 => Write(w, hook!.Rule!.DeviceIdentity), _ => Invalid()
            },
            Node.Metric => field switch
            {
                0 => Write(w, Metric.State), 1 => Write(w, Metric.Quantity), 2 => Write(w, Metric.Reading), 3 => Write(w, Metric.Unit),
                4 => Write(w, Metric.Threshold), 5 => Write(w, Metric.Since), 6 => Write(w, Metric.Reason), _ => Invalid()
            },
            Node.Target => field switch
            {
                0 => Write(w, Metric.Target.Binding), 1 => Write(w, Metric.Target.MetricId), 2 => Write(w, Metric.Target.SoftwareId),
                3 => Write(w, Metric.Target.ProcessId), 4 => Write(w, Metric.Target.StartKey), 5 => Write(w, Metric.Target.DeviceIdentity), _ => Invalid()
            },
            Node.Owner => field switch { 0 => Write(w, Metric.Owners[index].Name), 1 => Write(w, Metric.Owners[index].ProcessId), 2 => Write(w, Metric.Owners[index].Reading), _ => Invalid() },
            Node.EventValue when field == 0 => Write(w, EventValue.State),
            Node.Event => field switch { 0 => Write(w, EventValue.Event.Kind), 1 => Write(w, EventValue.Event.Reason), _ => Invalid() },
            Node.Action => field switch
            {
                0 => Write(w, Action.At), 1 => Write(w, Action.ProcessId), 2 => Write(w, Action.ProcessStartKey), 3 => Write(w, Action.Name),
                4 => Write(w, Action.BaselineBytes), 5 => Write(w, Action.CurrentBytes), 6 => Write(w, Action.GrowthBytes),
                7 => Write(w, Action.BytesWritten), 8 => Write(w, Action.Outcome), 9 => Write(w, Action.Reason), 10 => Write(w, Action.ActionId),
                11 => Write(w, Action.TargetKind), 12 => Write(w, Action.Detail), 13 => NativeMethods.ActionState(w, (int)Action.State), _ => Invalid()
            },
            Node.Selection => field switch
            {
                0 => Write(w, Action.SelectionBasis.Rule), 1 => Write(w, Action.SelectionBasis.ProcessGrowthBytes),
                2 => Write(w, Action.SelectionBasis.DirectChildrenMemoryGrowthBytes), 3 => Write(w, Action.SelectionBasis.MinimumProcessGrowthBytes), _ => Invalid()
            },
            Node.Tree => field switch { 0 => Write(w, Action.TreeRoot!.ProcessId), 1 => Write(w, Action.TreeRoot!.ProcessStartKey), 2 => Write(w, Action.TreeRoot!.Name), _ => Invalid() },
            Node.Status => field switch
            {
                0 => Write(w, Status.Enabled), 1 => Write(w, Status.ThresholdPercent), 2 => Write(w, Status.CheckIntervalSeconds),
                3 => Write(w, Status.TriggerBytes), 4 => Write(w, Status.RemainingBytes), 5 => Write(w, Status.InactiveReason),
                6 => Write(w, Status.ActionMode), 7 => Write(w, Status.Active), 8 => Write(w, Status.AtOrAboveThreshold),
                9 => Write(w, Status.CurrentActionId), 10 => Write(w, Status.MinimumProcessGrowthBytes), 11 => Write(w, Status.DecisionReason), _ => Invalid()
            },
            Node.Reserve => field switch { 0 => Write(w, Status.Reserve.ConfiguredSizeBytes), 1 => Write(w, Status.Reserve.Built), 2 => Write(w, Status.Reserve.Reason), 3 => Write(w, Status.Reserve.RemainingBytes), _ => Invalid() },
            Node.Entry => field switch
            {
                0 => Write(w, Status.Reserve.Entries[index].Location), 1 => Write(w, Status.Reserve.Entries[index].ConfiguredSizeBytes),
                2 => Write(w, Status.Reserve.Entries[index].Built), 3 => Write(w, Status.Reserve.Entries[index].Reason),
                4 => Write(w, Status.Reserve.Entries[index].RemainingBytes), 5 => Write(w, Status.Reserve.Entries[index].ClusterBytes), _ => Invalid()
            },
            Node.Commit => field switch { 0 => Write(w, Status.CurrentCommit!.CurrentBytes), 1 => Write(w, Status.CurrentCommit!.LimitBytes), 2 => Write(w, Status.CurrentCommit!.Percent), _ => Invalid() },
            Node.Exclusion => field switch
            {
                0 => Write(w, Status.ExcludedCandidates[index].ProcessId), 1 => Write(w, Status.ExcludedCandidates[index].ProcessStartKey),
                2 => Write(w, Status.ExcludedCandidates[index].Name), 3 => Write(w, Status.ExcludedCandidates[index].Reason), _ => Invalid()
            },
            Node.Rejection => field switch { 0 => Write(w, Rejection.StatusCode), 1 => Write(w, Rejection.Error), 2 => Write(w, Rejection.Position), 3 => Write(w, Rejection.Reason), _ => Invalid() },
            Node.DumpPath when field == 0 => Write(w, Action.DumpPaths[index]),
            Node.ProtectedProcess when field == 0 => Write(w, Status.ProtectedProcesses[index]),
            _ => Invalid()
        };
        private static int Invalid() => throw new ArgumentOutOfRangeException("field");
        public void Dispose() { if (Pinned) { Pin.Dispose(); Pinned = false; } }
    }

    private static unsafe int Write(nint w, string? value)
    {
        fixed (char* pointer = value) return NativeMethods.String(w, (ushort*)pointer, checked((nuint)(value?.Length ?? 0)));
    }
    private static int Write(nint w, int value) => NativeMethods.I64(w, value);
    private static int Write(nint w, int? value) => value is { } number ? Write(w, number) : NativeMethods.Null(w);
    private static int Write(nint w, long value) => NativeMethods.I64(w, value);
    private static int Write(nint w, ulong value) => NativeMethods.U64(w, value);
    private static int Write(nint w, ulong? value) => value is { } number ? Write(w, number) : NativeMethods.Null(w);
    private static int Write(nint w, double value) => NativeMethods.F64(w, value);
    private static int Write(nint w, double? value) => value is { } number ? Write(w, number) : NativeMethods.Null(w);
    private static int Write(nint w, bool value) => NativeMethods.Bool(w, value ? 1U : 0U);
    private static int Write(nint w, bool? value) => value is { } flag ? Write(w, flag) : NativeMethods.Null(w);
    private static int Write(nint w, decimal? value)
    {
        if (value is not { } number) return NativeMethods.Null(w);
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(number, bits);
        return NativeMethods.Decimal(w, unchecked((uint)bits[0]), unchecked((uint)bits[1]), unchecked((uint)bits[2]), unchecked((uint)bits[3]));
    }
    private static int Write(nint w, DateTimeOffset? value)
    {
        if (value is not { } date) return NativeMethods.Null(w);
        var parts = new DateParts
        {
            Year = date.Year, Month = date.Month, Day = date.Day, Hour = date.Hour, Minute = date.Minute, Second = date.Second,
            Fraction = checked((int)(date.Ticks % TimeSpan.TicksPerSecond)), OffsetMinutes = checked((int)(date.Offset.Ticks / TimeSpan.TicksPerMinute))
        };
        return NativeMethods.DateTime(w, in parts);
    }

    private enum Node : uint { Root, Hook, Rule, Metric, Target, Owner, EventValue, Event, Action, Selection, Tree, Status, Reserve, Entry, Commit, Exclusion, Rejection, DumpPath, ProtectedProcess }
    private enum Query : uint { RulePresent, IntervalPresent, EventPresent, RuleSoftwarePresent, RuleProcessPresent, RuleStartPresent, RuleDevicePresent, RejectionPositionPresent, RejectionReasonPresent, OwnerCount, ActionPresent, TreePresent, DumpCount, CommitPresent, EntryCount, ExclusionCount, ProtectedCount }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Input { internal uint Kind; internal uint RejectionStatus; internal ushort* State; internal nuint StateLength; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Response { internal uint Selected; internal uint Status; internal uint Rejection; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, uint, nuint, uint, nint, int> Field;
        internal delegate* unmanaged[Cdecl]<nint, uint, nuint*, int> Shape;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputCallbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, byte**, nuint*, int> Reserve;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Commit;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DateParts { internal int Year, Month, Day, Hour, Minute, Second, Fraction, OffsetMinutes; }
    private static partial class NativeMethods
    {
        private const string Library = "ResourceManager.NativeCore";
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_select")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Select(in Input input, out Response response);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_encode_stream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int EncodeStream(in Input input, nint context, in Callbacks callbacks, in OutputCallbacks output, out Response response);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_string")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int String(nint writer, ushort* text, nuint length);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_datetime")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int DateTime(nint writer, in DateParts value);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_null")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Null(nint writer);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_bool")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Bool(nint writer, uint value);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_i64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int I64(nint writer, long value);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_u64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int U64(nint writer, ulong value);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_f64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int F64(nint writer, double value);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_decimal")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Decimal(nint writer, uint low, uint middle, uint high, uint flags);
        [LibraryImport(Library, EntryPoint = "rm_hook_wait_action_state")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ActionState(nint writer, int state);
    }
}
