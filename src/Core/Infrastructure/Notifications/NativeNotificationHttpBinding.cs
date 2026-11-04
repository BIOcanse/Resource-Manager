using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Infrastructure.Notifications;

// H-cutover removes INativeHttpFamilyBinding glue. Typed source access remains
// only until notification producer/facts and store bridges leave CLR in R/H3.
internal sealed partial class NativeNotificationHttpBinding(Func<INotificationOwner> source, nuint window = 0) : INativeHttpFamilyBinding
{
    internal NativeNotificationHttpBinding(INotificationOwner owner, nuint window = 0)
        : this(() => owner, window) { }

    public int RequestBodyBytes => 0;
    public void BorrowRoute(Action<nint> consume) => Borrow(null, window, consume);
    public INativeHttpFamilyInvocation OpenInvocation() => new Invocation(source(), window);

    private sealed class Invocation(INotificationOwner owner, nuint window) : INativeHttpFamilyInvocation
    {
        internal NotificationList? List;
        internal readonly INotificationOwner Owner = owner;
        public void Borrow(Action<nint> consume) => NativeNotificationHttpBinding.Borrow(this, window, consume);
        public void Dispose() => List = null;
    }
    private sealed class Scope(Invocation? invocation, Action<nint> consume)
    {
        internal readonly Invocation? Invocation = invocation;
        internal readonly Action<nint> Consume = consume;
        internal ExceptionDispatchInfo? Failure;
    }
    private static unsafe void Borrow(Invocation? invocation, nuint window, Action<nint> consume)
    {
        var scope = new Scope(invocation, consume);
        var handle = GCHandle.Alloc(scope);
        try
        {
            var source = new Source { Context = GCHandle.ToIntPtr(handle), Read = &Read,
                Meta = &ReadMeta, Kind = &ReadKind, Facts = &ReadFacts, End = &End };
            var code = BorrowNative(invocation is null ? null : &source, window, GCHandle.ToIntPtr(handle), &Consume);
            scope.Failure?.Throw();
            if (code != 0) throw new InvalidOperationException($"Native notification descriptor failed ({code}).");
        }
        finally { handle.Free(); }
    }
    private static Scope Get(nint context) => (Scope)GCHandle.FromIntPtr(context).Target!;
    private static NotificationItem Item(nint context, nuint index)
        => Get(context).Invocation!.List!.Notifications[checked((int)index)];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Consume(nint context, nint descriptor)
    {
        var scope = Get(context);
        try { scope.Consume(descriptor); return 0; }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Read(nint context, nuint* count)
    {
        try
        {
            var invocation = Get(context).Invocation!;
            // Accepted once by native begin. Resuming codec windows never
            // restores, polls or rereads the owner, nor clones its facts.
            invocation.List = invocation.Owner.Read();
            *count = checked((nuint)invocation.List.Notifications.Count);
            return 0;
        }
        catch { return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void End(nint context) => Get(context).Invocation!.List = null;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadMeta(nint context, nuint index, Meta* meta)
    {
        try
        {
            var item = Item(context, index);
            var date = item.OccurredAt;
            *meta = new() { Sequence = item.Sequence, Date = new() { Year = date.Year, Month = date.Month, Day = date.Day,
                Hour = date.Hour, Minute = date.Minute, Second = date.Second, Fraction = (int)(date.Ticks % TimeSpan.TicksPerSecond),
                OffsetMinutes = checked((int)date.Offset.TotalMinutes) } };
            return 0;
        }
        catch { return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    // Fixed within this exact immutable original-result borrow (C0-view rule).
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static unsafe int ReadKind(nint context, nuint index, nint receiverContext,
        delegate* unmanaged[Cdecl]<nint, char*, nuint, int> receiver)
    {
        try
        {
            var kind = Item(context, index).Kind;
            fixed (char* pointer = kind) return receiver(receiverContext, pointer, (nuint)kind.Length);
        }
        catch { return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadFacts(nint context, nuint index, nint receiverContext,
        delegate* unmanaged[Cdecl]<nint, uint, char*, nuint, int> receiver)
    {
        try { return Visit(Item(context, index).Facts, receiverContext, receiver); }
        catch { return 1; }
    }
    // JsonElement is still the producer's source. Enumerate typed facts only;
    // punctuation, escaping, dates, selection and final bytes are native-owned.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static unsafe int Visit(JsonElement value, nint context,
        delegate* unmanaged[Cdecl]<nint, uint, char*, nuint, int> receiver)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (receiver(context, 0, null, 0) != 0) return 1;
                foreach (var property in value.EnumerateObject())
                {
                    var name = property.Name;
                    fixed (char* pointer = name)
                        if (receiver(context, 3, pointer, (nuint)name.Length) != 0) return 1;
                    if (Visit(property.Value, context, receiver) != 0) return 1;
                }
                return receiver(context, 2, null, 0);
            case JsonValueKind.Array:
                if (receiver(context, 1, null, 0) != 0) return 1;
                foreach (var element in value.EnumerateArray())
                    if (Visit(element, context, receiver) != 0) return 1;
                return receiver(context, 2, null, 0);
            case JsonValueKind.String:
            case JsonValueKind.Number:
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                fixed (char* pointer = text) return receiver(context, value.ValueKind == JsonValueKind.String ? 4U : 5U, pointer, (nuint)text.Length);
            case JsonValueKind.True: return receiver(context, 6, null, 0);
            case JsonValueKind.False: return receiver(context, 7, null, 0);
            case JsonValueKind.Null: return receiver(context, 8, null, 0);
            default: return 1;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DateParts { internal int Year, Month, Day, Hour, Minute, Second, Fraction, OffsetMinutes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Meta { internal long Sequence; internal DateParts Date; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Source
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, nuint*, int> Read;
        internal delegate* unmanaged[Cdecl]<nint, nuint, Meta*, int> Meta;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nint, delegate* unmanaged[Cdecl]<nint, char*, nuint, int>, int> Kind;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nint, delegate* unmanaged[Cdecl]<nint, uint, char*, nuint, int>, int> Facts;
        internal delegate* unmanaged[Cdecl]<nint, void> End;
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_notifications_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int BorrowNative(Source* source, nuint window, nint context, delegate* unmanaged[Cdecl]<nint, nint, int> consume);
}
