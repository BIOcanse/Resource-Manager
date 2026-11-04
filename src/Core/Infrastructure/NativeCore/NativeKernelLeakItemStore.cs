using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.Adaptation;
using N = ResourceManager.App.Infrastructure.NativeCore.NativeKernelLeakItems;

namespace ResourceManager.App.Infrastructure.NativeCore;

// H3 removes the CLR lifetime host. H1/Z-codec removes the immutable current
// HTTP projection. All authoritative rows and metadata bytes live in Ledger.
internal sealed unsafe class NativeKernelLeakItemStore : IDisposable
{
    private readonly ResourceManagerLedgerOwner ledger;
    private readonly bool ownsLedger;
    private readonly BlockingCollection<Command> commands = new();
    private readonly Task lifetime;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<KernelLeakSuspect> projection = [];
    private int disposed;

    private sealed class Command(Func<nint, object?>? call, ulong length = 0)
    {
        internal Func<nint, object?>? Call => call;
        internal ulong Length => length;
        internal TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal NativeKernelLeakItemStore(ResourceManagerLedgerOwner? ledger = null)
    {
        this.ledger = ledger ?? new ResourceManagerLedgerOwner();
        ownsLedger = ledger is null;
        lifetime = Task.Run(Lifetime);
        try { ready.Task.GetAwaiter().GetResult(); }
        catch
        {
            try { lifetime.GetAwaiter().GetResult(); }
            finally { commands.Dispose(); if (ownsLedger) this.ledger.Dispose(); }
            throw;
        }
    }

    private void Lifetime()
    {
        try
        {
            var bytes = N.Size(KernelLeakOptions.MaximumReportedItemCapacity);
            using var resource = ledger.CreateScratch(checked((ulong)bytes + 7));
            resource.WorkArea = "kernel-leak-reported-items";
            resource.Rows = $"{KernelLeakOptions.MaximumReportedItemCapacity} bounded reported identities and measured metadata";
            using var lease = resource.Acquire();
            using (var initial = lease.GetMemoryChunk(0, 1).Pin())
                Check(N.Initialize(Aligned(initial), bytes, KernelLeakOptions.MaximumReportedItemCapacity));
            // Publication ends its initial native lease; release that pin first.
            lease.PublishAndRetain();
            var pin = lease.GetMemoryChunk(0, 1).Pin();
            try
            {
                ready.TrySetResult();
                foreach (var command in commands.GetConsumingEnumerable())
                {
                    try
                    {
                        if (command.Length > lease.Length)
                        {
                            pin.Dispose(); pin = default;
                            try { lease.Grow(command.Length); }
                            finally { pin = lease.GetMemoryChunk(0, 1).Pin(); }
                            var skipped = checked((ulong)((nuint)Aligned(pin) - (nuint)pin.Pointer));
                            Check(N.Reserve(Aligned(pin), checked((nuint)(lease.Length - skipped))));
                        }
                        command.Completion.TrySetResult(command.Call?.Invoke(Aligned(pin)));
                    }
                    catch (Exception error)
                    {
                        command.Completion.TrySetException(error);
                        if (pin.Pointer == null) throw;
                    }
                }
            }
            finally { pin.Dispose(); }
        }
        catch (Exception error)
        {
            commands.CompleteAdding();
            ready.TrySetException(error);
            while (commands.TryTake(out var command)) command.Completion.TrySetException(error);
            throw;
        }
    }

    private static nint Aligned(MemoryHandle pin) => (nint)(((nuint)pin.Pointer + 7) & ~(nuint)7);
    private T Execute<T>(Func<nint, T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var command = new Command(region => action(region));
        commands.Add(command);
        return (T)command.Completion.Task.GetAwaiter().GetResult()!;
    }
    private void Grow(nuint arenaBytes)
    {
        var aligned = checked(((ulong)arenaBytes + 7) & ~7UL);
        var bytes = checked((ulong)N.Size(KernelLeakOptions.MaximumReportedItemCapacity) + 2 * aligned + 7);
        var command = new Command(null, bytes);
        commands.Add(command);
        _ = command.Completion.Task.GetAwaiter().GetResult();
    }

    internal IReadOnlyList<KernelLeakSuspect> Current => projection;
    internal void Restore(IReadOnlyList<KernelLeakSuspect> items)
    {
        using var input = new BorrowedInput(items);
        fixed (N.Row* rows = input.Rows)
        fixed (N.Metadata* metadata = input.Metadata)
        {
            var rowAddress = (nint)rows; var metadataAddress = (nint)metadata;
            (int Status, nuint Required) Invoke(nint region)
            {
                var status = N.Restore(region, (N.Row*)rowAddress, (N.Metadata*)metadataAddress, (nuint)input.Rows.Length, out var required);
                if (status == 0) projection = Project(region, false);
                return (status, required);
            }
            var result = Execute(Invoke);
            if (result.Status == 3) { Grow(result.Required); result = Execute(Invoke); }
            Check(result.Status);
        }
    }

    internal N.PlanResult Plan(long ticks, ReadOnlySpan<N.Key> live, ReadOnlySpan<N.Key> unavailable,
        ReadOnlySpan<N.Key> judged, IReadOnlyList<KernelLeakSuspect> suspects, int capacity,
        Action<IReadOnlyList<KernelLeakSuspect>> save)
    {
        using var input = new BorrowedInput(suspects);
        fixed (N.Key* livePointer = live, unavailablePointer = unavailable, judgedPointer = judged)
        fixed (N.Row* rows = input.Rows)
        fixed (N.Metadata* metadata = input.Metadata)
        {
            var l = (nint)livePointer; var u = (nint)unavailablePointer; var j = (nint)judgedPointer;
            var rowAddress = (nint)rows; var metadataAddress = (nint)metadata;
            var liveCount = (nuint)live.Length; var unavailableCount = (nuint)unavailable.Length; var judgedCount = (nuint)judged.Length;
            (int Status, N.PlanResult Result) Invoke(nint region)
            {
                var status = N.Plan(region, ticks, (N.Key*)l, liveCount, (N.Key*)u, unavailableCount,
                    (N.Key*)j, judgedCount, (N.Row*)rowAddress, (N.Metadata*)metadataAddress, (nuint)input.Rows.Length, (uint)capacity, out var result);
                if (status == 0) Commit(region, result, save);
                return (status, result);
            }
            var result = Execute(Invoke);
            if (result.Status == 3) { Grow(result.Result.RequiredBytes); result = Execute(Invoke); }
            Check(result.Status);
            return result.Result;
        }
    }

    internal int Clear(Action<IReadOnlyList<KernelLeakSuspect>> save) => Execute(region =>
    {
        IReadOnlyList<KernelLeakSuspect>? next = null;
        var count = NativeKernelHistoryCommand.Clear(region, () =>
        {
            next = Project(region, true);
            save(next);
        });
        if (next is not null) projection = next;
        return count;
    });

    private void Commit(nint region, N.PlanResult result, Action<IReadOnlyList<KernelLeakSuspect>> save)
    {
        if (result.Changed == 0) { Check(N.Commit(region)); return; }
        var next = Project(region, true);
        save(next);
        Check(N.Commit(region));
        projection = next;
    }

    // Pins and descriptors are call-scoped references to input, never authority.
    private sealed class BorrowedInput : IDisposable
    {
        internal readonly N.Row[] Rows;
        internal readonly N.Metadata[] Metadata;
        private readonly List<GCHandle> pins = [];
        internal BorrowedInput(IReadOnlyList<KernelLeakSuspect> values)
        {
            if (values.Count > KernelLeakOptions.MaximumReportedItemCapacity)
                throw new InvalidDataException("Kernel leak reported-item input exceeds the declared boundary capacity.");
            Rows = new N.Row[values.Count]; Metadata = new N.Metadata[values.Count];
            try
            {
                for (var i = 0; i < values.Count; i++)
                {
                    var item = values[i];
                    if (item?.Holder is not { } holder) { Metadata[i].ImageStatus = uint.MaxValue; continue; }
                    var key = NativeKernelLeakSeriesAbi.ToKey(holder.ProcessId, holder.StartKey);
                    var row = new N.Row { Pid = key.Pid, Birth = key.Birth, Length = key.Length, Pad = (uint)item.ResourceKind,
                        State = (uint)item.State, Current = item.CurrentValue, Growth = item.Growth,
                        Seconds = item.ObservationSeconds, Rate = item.GrowthPerSecond, SampleCount = (uint)item.SampleCount,
                        First = item.FirstObservedAt.UtcTicks, Last = item.LastObservedAt.UtcTicks,
                        FirstOffsetMinutes = checked((int)item.FirstObservedAt.Offset.TotalMinutes),
                        LastOffsetMinutes = checked((int)item.LastObservedAt.Offset.TotalMinutes) };
                    for (var n = 0; n < 20; n++) row.Text[n] = key.Text[n];
                    Rows[i] = row;
                    var image = holder.Image; var services = holder.Services;
                    var text = services?.Names?.Select(Pin).ToArray() ?? [];
                    var namePin = GCHandle.Alloc(text, GCHandleType.Pinned); pins.Add(namePin);
                    Metadata[i] = new N.Metadata { ImageStatus = image is null ? uint.MaxValue : (uint)image.Status,
                        ServicesStatus = services is null ? uint.MaxValue : (uint)services.Status,
                        Name = Pin(image?.Name), Path = Pin(image?.Path), Product = Pin(image?.ProductName),
                        Names = (N.Text*)namePin.AddrOfPinnedObject(), NamesCount = services?.Names is null ? nuint.MaxValue : (nuint)text.Length };
                }
            }
            catch { Dispose(); throw; }
        }
        private N.Text Pin(string? text)
        {
            if (text is null) return default;
            var pin = GCHandle.Alloc(text, GCHandleType.Pinned); pins.Add(pin);
            return new() { Pointer = pin.AddrOfPinnedObject(), Length = (nuint)text.Length };
        }
        public void Dispose() { foreach (var pin in pins) pin.Free(); pins.Clear(); }
    }

    private IReadOnlyList<KernelLeakSuspect> Project(nint region, bool pending)
    {
        var bank = pending ? 1u : 0u;
        Check(N.Snapshot(region, bank, out var rows, out var length));
        var result = new KernelLeakSuspect[checked((int)length)];
        for (var i = 0; i < result.Length; i++)
        {
            var row = rows[i];
            var pid = row.Pid;
            var key = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(row.Text, checked((int)row.Length)));
            Check(N.ReadMetadata(region, bank, (nuint)i, out var metadata));
            var image = new KernelLeakImage((KernelLeakMetadataStatus)metadata.ImageStatus,
                Text(metadata.Name), Text(metadata.Path), Text(metadata.Product));
            var serviceNames = new string[checked((int)metadata.NamesCount)];
            for (var n = 0; n < serviceNames.Length; n++)
            { Check(N.ReadService(region, bank, (nuint)i, (nuint)n, out var name)); serviceNames[n] = Text(name)!; }
            var holder = new KernelLeakHolder(row.Pid, key, image, new((KernelLeakMetadataStatus)metadata.ServicesStatus, serviceNames));
            var previous = i < projection.Count && projection[i].Holder.ProcessId == pid && projection[i].Holder.StartKey == key
                ? projection[i] : projection.FirstOrDefault(item => item.Holder.ProcessId == pid && item.Holder.StartKey == key);
            if (previous is not null && previous.Holder.Image == image && previous.Holder.Services.Status == holder.Services.Status
                && previous.Holder.Services.Names.SequenceEqual(serviceNames)) holder = previous.Holder;
            var item = new KernelLeakSuspect(KernelLeakResourceKind.ProcessHandles, row.Current, row.Rate, row.Seconds,
                checked((int)row.SampleCount), row.Growth, holder) { State = (KernelLeakItemState)row.State,
                    FirstObservedAt = new DateTimeOffset(row.First, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(row.FirstOffsetMinutes)),
                    LastObservedAt = new DateTimeOffset(row.Last, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(row.LastOffsetMinutes)) };
            result[i] = previous == item ? previous! : item;
        }
        return result;
    }
    private static string? Text(N.Text text) => text.Pointer == 0 ? null : new string((char*)text.Pointer, 0, checked((int)text.Length));
    private static void Check(int status)
    {
        if (status is 2 or 3) throw new OverflowException("Kernel leak reported-item capacity exceeded.");
        if (status != 0) throw new InvalidDataException("Invalid kernel leak reported items.");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        commands.CompleteAdding();
        try { lifetime.GetAwaiter().GetResult(); }
        finally { commands.Dispose(); if (ownsLedger) ledger.Dispose(); }
    }
}
