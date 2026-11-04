using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Notifications;

// Transitional typed/async/platform binding to the single Zig owner. H3 removes
// this CLR binding after the producer R batches and H1/H2 retire its callers.
public sealed partial class NotificationOwner : INotificationOwner, IDisposable
{
    private readonly IRuntimePlanProvider plans;
    private readonly RuntimePersistenceCapabilityPolicy persistence;
    private readonly TimeProvider time;
    private readonly ILogger<NotificationOwner> logger;
    private readonly string storePath;
    private readonly OwnerHandle handle;

    public unsafe NotificationOwner(IRuntimePlanProvider plans, IHostEnvironment environment,
        RuntimePersistenceCapabilityPolicy persistence, TimeProvider time, ILogger<NotificationOwner> logger)
    {
        this.plans = plans;
        this.persistence = persistence;
        this.time = time;
        this.logger = logger;
        storePath = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "UserData", "Notifications", "notifications.json");
        // Preserve the original structural subscriber limit. Retention still
        // comes exclusively from the compiled notifications freedom point.
        var config = new NativeNotificationMethods.Config { MaximumItems = NotificationOptions.MaximumRetainedCount, MaximumSubscribers = 64 };
        var callbacks = new NativeNotificationMethods.Callbacks { Release = &Release, Describe = &Describe,
            Load = &Load, LoadedItem = &LoadedItem, LoadedEnd = &LoadedEnd, CreateItem = &CreateItem,
            Save = &Save, Deliver = &Deliver, LogError = &LogError };
        var pointer = NativeNotificationMethods.Create(in config, in callbacks);
        if (pointer == 0) throw new OutOfMemoryException("Could not create the bounded native notification owner.");
        handle = new(pointer);
    }

    public NotificationList Read()
    {
        using var call = new Call(this);
        call.Check(NativeNotificationMethods.Read(handle, call.Context, Persist));
        return call.List!;
    }

    public NotificationItem Post(string kind, JsonElement facts)
    {
        using var call = new Call(this) { Kind = kind, Facts = facts };
        var flags = (NativeNotificationMethods.Scope(kind) != 0 ? 8u : 0u) | (facts.ValueKind == JsonValueKind.Object ? 4u : 0u);
        // Validate input before asking for the plan, matching the old entry.
        if ((flags & 8) == 0) throw new ArgumentException("The notification kind is not registered.", nameof(kind));
        if ((flags & 4) == 0) throw new ArgumentException("Notification facts must be one JSON object.", nameof(facts));
        var capacity = plans.Current.HostManager.Notifications?.RetainedCount
            ?? throw new InvalidOperationException("The notification retained count is unavailable.");
        call.Check(NativeNotificationMethods.Post(handle, call.Context, Persist, checked((nuint)capacity), flags));
        return call.Item!;
    }

    public async IAsyncEnumerable<NotificationList> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only the host's bounded asynchronous delivery buffer remains managed.
        // Zig owns registration, the current frame and all publication decisions.
        var channel = Channel.CreateBounded<NotificationList>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        var reader = GCHandle.Alloc(channel);
        var retained = false;
        var registered = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            using (var call = new Call(this))
            {
                call.Check(NativeNotificationMethods.Subscribe(handle, call.Context, Persist, GCHandle.ToIntPtr(reader)));
                registered = true;
            }
            await foreach (var list in channel.Reader.ReadAllAsync(cancellationToken)) yield return list;
        }
        finally
        {
            if (registered) NativeNotificationMethods.Unsubscribe(handle.DangerousGetHandle(), GCHandle.ToIntPtr(reader));
            reader.Free();
            if (retained) handle.DangerousRelease();
        }
    }

    // Current and peak metadata bytes are identical: there is no growth path.
    public (ulong CurrentBytes, ulong PeakBytes) ReadNativeMemory()
    {
        var bytes = (ulong)NativeNotificationMethods.OwnedBytes(handle);
        return (bytes, bytes);
    }
    public void Dispose() => handle.Dispose();
    private uint Persist => persistence.MutablePersistenceEnabled ? 1u : 0u;

    internal sealed class OwnerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal OwnerHandle(nint pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { NativeNotificationMethods.Destroy(handle); return true; }
    }

    private sealed class Call : IDisposable
    {
        private readonly GCHandle root;
        internal Call(NotificationOwner owner) { Owner = owner; root = GCHandle.Alloc(this); }
        internal readonly NotificationOwner Owner;
        internal nint Context => GCHandle.ToIntPtr(root);
        internal string Kind = "";
        internal JsonElement Facts;
        internal NotificationItem? Item;
        internal NotificationList? List;
        internal StoredNotifications? Loaded;
        internal Exception? StoreFailure;
        internal ExceptionDispatchInfo? Failure;
        internal void Check(int code)
        {
            Failure?.Throw();
            if (code == 3) throw new InvalidOperationException("Notification subscriber capacity exceeded.");
            if (code != 0) throw new InvalidOperationException($"Native notification operation failed ({code}).");
        }
        public void Dispose() => root.Free();
    }
    private static Call Borrow(nint context) => (Call)GCHandle.FromIntPtr(context).Target!;
    private static NotificationItem Payload(nint reference) => (NotificationItem)GCHandle.FromIntPtr(reference).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(nint reference) => GCHandle.FromIntPtr(reference).Free();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeNotificationMethods.Meta Describe(nint reference)
    {
        var item = Payload(reference);
        return new() { Sequence = item.Sequence, Flags =
            (!string.IsNullOrWhiteSpace(item.Kind) ? 1u : 0u) | (item.OccurredAt != default ? 2u : 0u)
            | (item.Facts.ValueKind == JsonValueKind.Object ? 4u : 0u) };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint CreateItem(nint context, long sequence)
    {
        var call = Borrow(context);
        try
        {
            call.Item = new(sequence, call.Kind, call.Owner.time.GetUtcNow(), call.Facts.Clone());
            return GCHandle.ToIntPtr(GCHandle.Alloc(call.Item));
        }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Deliver(nint context, nint* items, nuint count, nint* readers, nuint readerCount)
    {
        var call = Borrow(context);
        try
        {
            var array = new NotificationItem[checked((int)count)];
            for (var i = 0; i < array.Length; i++) array[i] = Payload(items[i]);
            var list = new NotificationList(array);
            if (readerCount == 0) call.List = list;
            else for (nuint i = 0; i < readerCount; i++)
                ((Channel<NotificationList>)GCHandle.FromIntPtr(readers[i]).Target!).Writer.TryWrite(list);
            return 0;
        }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 7; }
    }
}
