using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// Temporary managed invocation/projection bridge. Platform reads and attribution decisions
// live in platform/windows/kernel_leak_attribution.zig; H3 removes the service binding.
public sealed unsafe partial class WindowsKernelLeakAttributionReader : IKernelLeakAttributionReader, IDisposable
{
    private const uint MaximumServiceBufferBytes = 1024 * 1024, MaximumServices = 16384, MaximumTextCharacters = 32768;
    private readonly ResourceManagerLedgerOwner? ledger;
    private readonly RowSizedComputeScratch? scratch;
    private readonly object gate = new();
    private readonly Func<int, long, string?>? readPath;
    private readonly Func<int, ProcessCreationRead>? readIdentity;
    private readonly Func<string, string?>? readProduct;
    private readonly Func<CancellationToken, Dictionary<int, IReadOnlyList<string>>?>? readServices;
    private readonly ILogger<WindowsKernelLeakAttributionReader>? logger;
    public WindowsKernelLeakAttributionReader(ILogger<WindowsKernelLeakAttributionReader>? logger = null) => this.logger = logger;
    public WindowsKernelLeakAttributionReader(ResourceManagerLedgerOwner ledger, ILogger<WindowsKernelLeakAttributionReader>? logger = null)
    {
        this.ledger = ledger;
        this.logger = logger;
        scratch = new(ledger.CreateScratch, "kernel-leak-attribution");
    }
    // Test-only Windows-fact injection uses the same native composition as real platform reads.
    internal WindowsKernelLeakAttributionReader(Func<int, long, string?> path, Func<int, ProcessCreationRead> identity,
        Func<string, string?> product, Func<CancellationToken, Dictionary<int, IReadOnlyList<string>>?> services)
        => (readPath, readIdentity, readProduct, readServices) = (path, identity, product, services);

    public IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default)
    {
        if (holders.Count > MaximumServices) throw new OverflowException("Kernel leak attribution capacity exceeded.");
        if (holders.Count == 0) return [];
        if (readPath is not null) return ReadRecorded(holders, cancellationToken);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Parameterless direct callers own their Ledger for this call; DI uses the product owner.
            using var localOwner = ledger is null ? new ResourceManagerLedgerOwner() : null;
            using var localScratch = scratch is null ? new RowSizedComputeScratch(localOwner!.CreateScratch, "kernel-leak-attribution") : null;
            var workspace = scratch ?? localScratch!;
            var limits = new Limits { MaximumServiceBytes = MaximumServiceBufferBytes, MaximumServices = MaximumServices, MaximumText = MaximumTextCharacters };
            var context = new Context(holders, cancellationToken);
            var reference = GCHandle.Alloc(context);
            try
            {
                var pointer = GCHandle.ToIntPtr(reference);
                var bytes = WorkspaceBytes(in limits);
                workspace.Ensure(checked((ulong)bytes + 7), $"{holders.Count} attribution requests");
                nuint needed;
                using (var lease = workspace.Acquire())
                {
                    using (var view = new ComputeScratchView(lease))
                        needed = Required(pointer, &GetQuery, checked((nuint)holders.Count), in limits, view.AlignedRegion(0, checked((ulong)bytes), 8), bytes);
                    lease.Complete();
                }
                context.Failure?.Throw();
                if (needed < bytes) throw new InvalidDataException("Kernel leak attribution workspace sizing failed.");
                workspace.Ensure(checked((ulong)needed + 7), $"{holders.Count} attribution requests and version resources");
                int result;
                using (var lease = workspace.Acquire())
                {
                    using (var view = new ComputeScratchView(lease))
                        result = Capture(pointer, &GetQuery, &Accept, checked((nuint)holders.Count), in limits, view.AlignedRegion(0, checked((ulong)needed), 8), needed);
                    lease.Complete();
                }
                context.Failure?.Throw();
                if (result != 0) throw new InvalidDataException($"Kernel leak attribution failed with native status {result}.");
                return context.Results;
            }
            finally { reference.Free(); }
        }
    }

    private IReadOnlyList<KernelLeakHolder> ReadRecorded(IReadOnlyList<KernelLeakHolder> holders, CancellationToken token)
    {
        var services = readServices!(token);
        var result = new KernelLeakHolder[holders.Count];
        for (var index = 0; index < holders.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var holder = holders[index];
            var birth = long.Parse(holder.StartKey, CultureInfo.InvariantCulture);
            var path = readPath!(holder.ProcessId, birth);
            string? product = null;
            var productAvailable = false;
            if (path is { Length: > 0 and <= (int)MaximumTextCharacters })
            {
                try { product = readProduct!(path); productAvailable = true; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
                { logger?.LogWarning(error, "Kernel leak image metadata unavailable for PID {ProcessId}.", holder.ProcessId); }
            }
            var identity = readIdentity!(holder.ProcessId);
            var names = services?.GetValueOrDefault(holder.ProcessId) ?? [];
            var pins = new List<GCHandle>(names.Count);
            try
            {
                var nativeNames = new Text[names.Count];
                for (var item = 0; item < names.Count; item++)
                {
                    var pin = GCHandle.Alloc(names[item], GCHandleType.Pinned); pins.Add(pin);
                    nativeNames[item] = new() { Pointer = pin.AddrOfPinnedObject(), Length = checked((nuint)names[item].Length) };
                }
                fixed (char* pathPointer = path)
                fixed (char* productPointer = product)
                fixed (Text* servicePointer = nativeNames)
                {
                    var facts = new Facts
                    {
                        ExpectedBirth = birth, ActualBirth = identity.Creation ?? 0,
                        IdentityStatus = checked((uint)identity.Status), HasActualBirth = identity.Creation.HasValue ? 1u : 0u,
                        ProductAvailable = productAvailable ? 1u : 0u, ServicesAvailable = services is not null ? 1u : 0u,
                        Path = new() { Pointer = (IntPtr)pathPointer, Length = checked((nuint)(path?.Length ?? 0)) },
                        Product = new() { Pointer = (IntPtr)productPointer, Length = checked((nuint)(product?.Length ?? 0)) },
                        Services = (IntPtr)servicePointer, ServiceCount = checked((nuint)nativeNames.Length)
                    };
                    Compose(in facts, MaximumTextCharacters, out var metadata);
                    result[index] = Project(holder, in metadata);
                }
            }
            finally { foreach (var pin in pins) pin.Free(); }
        }
        return result;
    }

    public void Dispose() { lock (gate) scratch?.Dispose(); }
    private sealed class Context(IReadOnlyList<KernelLeakHolder> holders, CancellationToken token)
    {
        internal readonly IReadOnlyList<KernelLeakHolder> Holders = holders;
        internal readonly KernelLeakHolder[] Results = new KernelLeakHolder[holders.Count];
        internal readonly CancellationToken Token = token;
        internal ExceptionDispatchInfo? Failure;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetQuery(IntPtr state, nuint index, Query* query)
    {
        var context = (Context)GCHandle.FromIntPtr(state).Target!;
        try
        {
            context.Token.ThrowIfCancellationRequested();
            var holder = context.Holders[checked((int)index)];
            *query = new() { ProcessId = holder.ProcessId, Birth = long.Parse(holder.StartKey, CultureInfo.InvariantCulture) };
            return 0;
        }
        catch (Exception error) { context.Failure ??= ExceptionDispatchInfo.Capture(error); return -1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Accept(IntPtr state, nuint index, Metadata* metadata)
    {
        var context = (Context)GCHandle.FromIntPtr(state).Target!;
        try
        {
            context.Token.ThrowIfCancellationRequested();
            context.Results[checked((int)index)] = Project(context.Holders[checked((int)index)], in *metadata);
            return 0;
        }
        catch (Exception error) { context.Failure ??= ExceptionDispatchInfo.Capture(error); return -1; }
    }
    private static string? Read(Text value) => value.Pointer == IntPtr.Zero ? null : new string((char*)value.Pointer, 0, checked((int)value.Length));
    private static KernelLeakHolder Project(KernelLeakHolder holder, in Metadata metadata)
    {
        var names = new string[checked((int)metadata.ServiceCount)];
        var services = (Text*)metadata.Services;
        for (var index = 0; index < names.Length; index++) names[index] = Read(services[index])!;
        return holder with
        {
            Image = new((KernelLeakMetadataStatus)metadata.ImageStatus, Read(metadata.Name), Read(metadata.Path), Read(metadata.Product)),
            Services = new((KernelLeakMetadataStatus)metadata.ServicesStatus, names)
        };
    }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { internal uint MaximumServiceBytes, MaximumServices, MaximumText; }
    [StructLayout(LayoutKind.Sequential)] private struct Text { internal IntPtr Pointer; internal nuint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct Query { internal int ProcessId; internal uint Reserved; internal long Birth; }
    [StructLayout(LayoutKind.Sequential)] private struct Facts
    {
        internal long ExpectedBirth, ActualBirth;
        internal uint IdentityStatus, HasActualBirth, ProductAvailable, ServicesAvailable;
        internal Text Path, Product;
        internal IntPtr Services;
        internal nuint ServiceCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Metadata
    {
        internal uint ImageStatus, ServicesStatus;
        internal Text Name, Path, Product;
        internal IntPtr Services;
        internal nuint ServiceCount;
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_attribution_workspace_bytes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint WorkspaceBytes(in Limits limits);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_attribution_required")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint Required(IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, nuint, Query*, int> query, nuint count, in Limits limits, IntPtr memory, nuint length);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_attribution_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Capture(IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, nuint, Query*, int> query, delegate* unmanaged[Cdecl]<IntPtr, nuint, Metadata*, int> sink, nuint count, in Limits limits, IntPtr memory, nuint length);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_attribution_compose")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Compose(in Facts facts, uint maximumText, out Metadata metadata);
}
