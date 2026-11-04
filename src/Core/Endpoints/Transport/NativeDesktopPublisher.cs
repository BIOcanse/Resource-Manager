using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Endpoints.Transport;

// Temporary HTTP desktop-publish piece. D-c04 removes this data path when the
// service and observer switch together; H2 removes remaining HTTP registration,
// R-desktop the typed store-input bridge. The platform batch removes caller
// attestation. This bridge owns no desktop facts, policy or JSON decoder.
internal static partial class NativeDesktopPublisher
{
    internal static async Task RunAsync(HttpContext context, InteractiveDesktopObservationStore store,
        LoopbackApiCaller? caller, bool admitted, int rejectionStatus,
        CancellationToken sourceEnded, CancellationToken selectionReplaced,
        CancellationToken serviceStopping)
    {
        // These are concrete request/source lifetimes. P-access owns admission
        // and any policy-defined ending of RequestAborted; this bridge creates
        // no separate policy-revocation contract or key-derived signal.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, sourceEnded, selectionReplaced, serviceStopping);
        var bridge = new Bridge(store, caller, lifetime);
        State state = default;
        try
        {
            var status = Begin(ref state, caller is not null, caller?.WindowsSessionId ?? 0,
                admitted, rejectionStatus, bridge);
            bridge.Failure?.Throw();
            if (status < 0) throw new InvalidOperationException("Native desktop publisher could not start.");
            context.Response.StatusCode = status;
            if (status != StatusCodes.Status200OK) return;
            try
            {
                if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                    size.MaxRequestBodySize = null;
                await context.Response.StartAsync(lifetime.Token);
                var options = ReaderOptions();
                bridge.Body = context.Request.BodyReader;
                while (true)
                {
                    var read = await bridge.Body.ReadAsync(lifetime.Token);
                    var consumed = 0UL;
                    int more;
                    try { more = Read(ref state, in options, bridge, read, out consumed); }
                    finally { bridge.Body.AdvanceTo(read.Buffer.GetPosition(checked((long)consumed)), read.Buffer.End); }
                    bridge.Failure?.Throw();
                    if (more < 0) throw new InvalidOperationException("Native desktop publisher input borrow failed.");
                    if (more == 0) break;
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException
                or Microsoft.AspNetCore.Http.BadHttpRequestException) { }
        }
        finally
        {
            // No native call is in flight. End cancels the original selection
            // before the request-local CTS dies; it does not stop other sources.
            var result = End(ref state, bridge);
            bridge.Failure?.Throw();
            if (result != 0) throw new InvalidOperationException("Native desktop publisher release failed.");
        }
    }

    private static Options ReaderOptions()
    {
        // Reuse the original reader's effective protocol depth, not a native
        // copy of the serializer's implicit numeric default.
        var depth = new Utf8JsonReader(ReadOnlySpan<byte>.Empty).CurrentState.Options.MaxDepth;
        if (depth <= 0) throw new InvalidOperationException("The original JSON reader did not expose its effective depth.");
        return new()
        {
            MaximumLineBytes = InteractiveDesktopObservation.MaximumRequestBytes,
            MaximumDepth = checked((uint)depth)
        };
    }

    private static unsafe Callbacks CallbacksForCall() => new()
    {
        Borrow = &Borrow, Select = &Select, Release = &Release,
        BeginObservation = &BeginObservation, Window = &Window, Publish = &Publish
    };

    private static unsafe int Begin(ref State state, bool verified, int sessionId, bool admitted,
        int rejectionStatus, Bridge bridge)
    {
        var handle = GCHandle.Alloc(bridge);
        try
        {
            var callbacks = CallbacksForCall();
            return NativeMethods.Begin(ref state, verified ? 1U : 0U, sessionId, admitted ? 1U : 0U,
                rejectionStatus, GCHandle.ToIntPtr(handle), in callbacks);
        }
        finally { handle.Free(); }
    }

    private static unsafe int End(ref State state, Bridge bridge)
    {
        var handle = GCHandle.Alloc(bridge);
        try
        {
            var callbacks = CallbacksForCall();
            return NativeMethods.End(ref state, GCHandle.ToIntPtr(handle), in callbacks);
        }
        finally { handle.Free(); }
    }

    private static unsafe int Read(ref State state, in Options options, Bridge bridge,
        ReadResult read, out ulong consumed)
    {
        var handle = GCHandle.Alloc(bridge);
        bridge.Buffer = read.Buffer;
        try
        {
            var callbacks = CallbacksForCall();
            return NativeMethods.Read(ref state, in options, checked((ulong)read.Buffer.Length),
                read.IsCompleted ? 1U : 0U, read.IsCanceled ? 1U : 0U,
                GCHandle.ToIntPtr(handle), in callbacks, out consumed);
        }
        finally
        {
            // Every borrowed pointer and typed decode builder ends before either
            // AdvanceTo or an await can give the original segments back.
            bridge.Unpin();
            bridge.Buffer = default;
            bridge.Rows = null;
            handle.Free();
        }
    }

    private static Bridge Get(nint context) => (Bridge)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Borrow(nint context, ulong offset, byte** pointer, nuint* length)
    {
        var bridge = Get(context);
        try
        {
            bridge.Unpin();
            var remaining = bridge.Buffer.Slice(checked((long)offset));
            var position = remaining.Start;
            while (remaining.TryGet(ref position, out var memory))
            {
                if (memory.IsEmpty) continue;
                bridge.Pin = memory.Pin();
                bridge.Pinned = true;
                *pointer = (byte*)bridge.Pin.Pointer;
                *length = checked((nuint)memory.Length);
                return 0;
            }
            throw new InvalidOperationException("Native desktop publisher requested input outside the borrowed sequence.");
        }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Select(nint context)
    {
        var bridge = Get(context);
        try
        {
            var caller = bridge.Caller!;
            return bridge.Store.SelectPublisher(caller.WindowsSessionId, bridge.PublisherKey, caller.ProcessId,
                new DateTime(caller.ProcessCreatedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(),
                bridge.Lifetime.Token) ? 1 : 0;
        }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Release(nint context)
    {
        var bridge = Get(context);
        try { bridge.Lifetime.Cancel(); return 0; }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int BeginObservation(nint context, Observation* sample)
    {
        var bridge = Get(context);
        try
        {
            bridge.Sample = *sample;
            bridge.Rows = sample->WindowsPresent == 0 ? null
                : new DesktopProcessWindowObservation[checked((int)sample->WindowsCount)];
            return 0;
        }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Window(nint context, ulong index, WindowValue* row)
    {
        var bridge = Get(context);
        try { bridge.Rows![checked((int)index)] = ManagedWindow(*row)!; return 0; }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    private static DesktopProcessWindowObservation? ManagedWindow(WindowValue row)
        => row.Present == 0 ? null : new(row.ProcessId, row.CreationFileTime, row.Visible != 0, row.Background != 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Publish(nint context)
    {
        var bridge = Get(context);
        try
        {
            var sample = bridge.Sample;
            // This is the original store input, not a mirrored native fact graph.
            // The original store alone validates and owns its immutable snapshot.
            return bridge.Store.Publish(new(sample.PublisherProcessId, sample.PublisherCreationFileTime,
                sample.SessionId, sample.ObservedAtTicks, sample.Complete != 0, sample.ForegroundKnown != 0,
                ManagedWindow(sample.Foreground), bridge.Rows!), bridge.PublisherKey) ? 1 : 0;
        }
        catch (Exception error) { bridge.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }

    private sealed class Bridge(InteractiveDesktopObservationStore store,
        LoopbackApiCaller? caller, CancellationTokenSource lifetime)
    {
        internal PipeReader Body = null!;
        internal InteractiveDesktopObservationStore Store => store;
        internal LoopbackApiCaller? Caller => caller;
        internal CancellationTokenSource Lifetime => lifetime;
        internal string PublisherKey { get; } = "stream:" + Guid.NewGuid().ToString("N");
        internal ReadOnlySequence<byte> Buffer;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal Observation Sample;
        internal DesktopProcessWindowObservation[]? Rows;
        internal ExceptionDispatchInfo? Failure;

        internal void Unpin()
        {
            if (!Pinned) return;
            Pin.Dispose();
            Pinned = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State { internal ulong Scanned; internal uint Phase, Selected; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Options { internal ulong MaximumLineBytes; internal uint MaximumDepth; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowValue
    {
        internal long CreationFileTime;
        internal int ProcessId;
        internal uint Visible, Background, Present;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Observation
    {
        internal long PublisherCreationFileTime, ObservedAtTicks;
        internal int PublisherProcessId, SessionId;
        internal uint Complete, ForegroundKnown;
        internal WindowValue Foreground;
        internal ulong WindowsCount;
        internal uint WindowsPresent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, ulong, byte**, nuint*, int> Borrow;
        internal delegate* unmanaged[Cdecl]<nint, int> Select, Release;
        internal delegate* unmanaged[Cdecl]<nint, Observation*, int> BeginObservation;
        internal delegate* unmanaged[Cdecl]<nint, ulong, WindowValue*, int> Window;
        internal delegate* unmanaged[Cdecl]<nint, int> Publish;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_desktop_publisher_begin")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Begin(ref State state, uint verified, int sessionId, uint admitted,
            int rejectionStatus, nint context, in Callbacks callbacks);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_desktop_publisher_read")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Read(ref State state, in Options options, ulong length, uint completed,
            uint canceled, nint context, in Callbacks callbacks, out ulong consumed);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_desktop_publisher_end")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int End(ref State state, nint context, in Callbacks callbacks);
    }
}
