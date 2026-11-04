using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Publications;

/// <summary>Temporary C7 bridge. Zig owns registration, pending edges, source end
/// and callback drain. The channel only adapts an enqueued wake to managed async.
/// No sampling demand, payload or source lifetime is owned by this bridge.</summary>
public sealed class NativeCurrentValuePublicationSignal : IDisposable
{
    private readonly object gate = new();
    private readonly SignalHandle signal = new();
    private bool ended;

    public Subscription Subscribe()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(ended, this);
            return new(new NodeHandle(signal));
        }
    }

    public void Publish()
    {
        lock (gate)
            if (!ended) Abi.Publish(signal);
    }

    /// <summary>Called only by the actual source lifetime owner. Consumer
    /// cancellation disposes its Subscription instead.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (ended) return;
            ended = true;
            Abi.End(signal);
            signal.Dispose(); // Active node handles retain the storage until drain.
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly NodeHandle node;
        internal Subscription(NodeHandle node) => this.node = node;

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await node.Wakes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var status = node.Poll();
                if (status == 1) return;
                if (status == 2) throw new ChannelClosedException();
            }
        }

        public void Dispose() => node.Dispose();
    }

    internal sealed class SignalHandle : SafeHandle
    {
        internal unsafe SignalHandle() : base(0, true)
        {
            ReadWriteNativeLibraryResolver.EnsureRegistered();
            if (Abi.Version() != 1) throw new InvalidOperationException("C7 publication ABI mismatch.");
            var size = Abi.SignalSize();
            var pointer = NativeMemory.AlignedAlloc(size, Abi.SignalAlignment());
            if (pointer is null) throw new OutOfMemoryException();
            if (Abi.Init((nint)pointer, size) != 0)
            {
                NativeMemory.AlignedFree(pointer);
                throw new InvalidOperationException("C7 publication initialization failed.");
            }
            SetHandle((nint)pointer);
        }
        public override bool IsInvalid => handle == 0;
        protected override unsafe bool ReleaseHandle()
        {
            Abi.Deinit(handle);
            NativeMemory.AlignedFree((void*)handle);
            return true;
        }
    }

    internal sealed class NodeHandle : SafeHandle
    {
        internal readonly Channel<byte> Wakes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
        });
        private readonly object gate = new();
        private readonly SignalHandle owner;
        private GCHandle context;
        private bool ownerReference;

        internal unsafe NodeHandle(SignalHandle owner) : base(0, true)
        {
            this.owner = owner;
            owner.DangerousAddRef(ref ownerReference);
            void* pointer = null;
            try
            {
                var size = Abi.NodeSize();
                pointer = NativeMemory.AlignedAlloc(size, Abi.NodeAlignment());
                if (pointer is null) throw new OutOfMemoryException();
                NativeMemory.Clear(pointer, size);
                context = GCHandle.Alloc(Wakes);
                if (Abi.Subscribe(owner.DangerousGetHandle(), (nint)pointer, size,
                    GCHandle.ToIntPtr(context), &Notify) != 0)
                    throw new InvalidOperationException("C7 publication registration failed.");
                SetHandle((nint)pointer);
            }
            catch
            {
                if (context.IsAllocated) context.Free();
                if (pointer is not null) NativeMemory.AlignedFree(pointer);
                if (ownerReference) { ownerReference = false; owner.DangerousRelease(); }
                throw;
            }
        }

        internal uint Poll()
        {
            lock (gate)
            {
                // Like the old Channel contract, a wake already read before
                // holder disposal may finish; disposal closes further waits.
                if (IsClosed) return 1;
                // Disposal cannot free the node until this synchronous use ends.
                var added = false;
                try
                {
                    DangerousAddRef(ref added);
                    return Abi.Poll(owner.DangerousGetHandle(), handle);
                }
                catch (ObjectDisposedException) { return 1; }
                finally { if (added) DangerousRelease(); }
            }
        }

        public override bool IsInvalid => handle == 0;
        protected override unsafe bool ReleaseHandle()
        {
            // Unsubscribe waits for all callbacks before the GC root is freed.
            Abi.Unsubscribe(owner.DangerousGetHandle(), handle);
            Wakes.Writer.TryComplete();
            context.Free();
            NativeMemory.AlignedFree((void*)handle);
            if (ownerReference) { ownerReference = false; owner.DangerousRelease(); }
            return true;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Notify(nint context, uint kind)
        {
            var channel = (Channel<byte>)GCHandle.FromIntPtr(context).Target!;
            if (kind == 2) channel.Writer.TryComplete();
            else channel.Writer.TryWrite(0);
        }
    }

    private static unsafe class Abi
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_current_publication_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Version();
        [DllImport(Library, EntryPoint = "rm_current_publication_signal_size", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint SignalSize();
        [DllImport(Library, EntryPoint = "rm_current_publication_signal_alignment", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint SignalAlignment();
        [DllImport(Library, EntryPoint = "rm_current_publication_subscription_size", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint NodeSize();
        [DllImport(Library, EntryPoint = "rm_current_publication_subscription_alignment", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint NodeAlignment();
        [DllImport(Library, EntryPoint = "rm_current_publication_signal_init", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Init(nint storage, nuint size);
        [DllImport(Library, EntryPoint = "rm_current_publication_subscribe", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Subscribe(nint owner, nint storage, nuint size, nint context, delegate* unmanaged[Cdecl]<nint, uint, void> notify);
        [DllImport(Library, EntryPoint = "rm_current_publication_unsubscribe", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Unsubscribe(nint owner, nint storage);
        [DllImport(Library, EntryPoint = "rm_current_publication_poll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Poll(nint owner, nint storage);
        [DllImport(Library, EntryPoint = "rm_current_publication_publish", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Publish(SignalHandle owner);
        [DllImport(Library, EntryPoint = "rm_current_publication_end", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void End(SignalHandle owner);
        [DllImport(Library, EntryPoint = "rm_current_publication_signal_deinit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Deinit(nint owner);
    }
}
