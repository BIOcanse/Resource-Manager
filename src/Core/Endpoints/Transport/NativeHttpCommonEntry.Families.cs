using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Endpoints.Transport;

internal static partial class NativeHttpCommonEntry
{
    private static async Task<bool> TryInvokeFamilyAsync(HttpContext context)
    {
        // A direct middleware caller may have no container. Unmatched requests
        // still enter the existing fail-closed policy below this adapter.
        if (context.RequestServices is not { } services) return false;
        using var bindings = services.GetServices<INativeHttpFamilyBinding>().GetEnumerator();
        INativeHttpFamilyBinding? selected = null;
        var rank = 0;
        while (bindings.MoveNext())
        {
            var binding = bindings.Current;
            var match = MatchFamily(binding, context.Request);
            if (match <= rank) continue;
            rank = match;
            selected = binding;
        }
        if (selected is null) return false;
        var capabilities = context.RequestServices.GetRequiredService<StartupCapabilitySet>();
        var normalActions = await ReadRequiredActionPolicyAsync(context, selected);
        await SendFamilyAsync(context, new(capabilities, normalActions, false), selected);
        return true;
    }

    private static async ValueTask<bool> ReadRequiredActionPolicyAsync(HttpContext context, INativeHttpFamilyBinding binding)
    {
        var requirement = -1;
        binding.BorrowRoute(route => requirement = FamilyMethods.Requirement(route));
        if (requirement < 0) throw new InvalidOperationException("Invalid native family requirement.");
        if (requirement != (int)NativeInteractionAccess.Requirement.NormalAction) return false;
        if (!LoopbackApiAuthenticationPolicy.IsLoopbackConnection(context)
            || !LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, context.Connection.LocalPort)) return false;
        // This proof only decides whether policy IO is necessary. Release its
        // local token before awaiting; native admission reborrows the real peer.
        var ordinary = false;
        if (WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out var token))
        {
            using (token) ordinary = !caller.IsAdministrator;
        }
        if (!ordinary) return false;
        return await context.RequestServices.GetRequiredService<INativeHttpNormalActionPolicy>().ReadAsync(context.RequestAborted);
    }

    private static unsafe int MatchFamily(INativeHttpFamilyBinding binding, HttpRequest request)
    {
        var method = Encoding.UTF8.GetBytes(DispatchMethod(request));
        var target = Encoding.UTF8.GetBytes(RawTarget(request));
        var found = 0;
        binding.BorrowRoute(route =>
        {
            fixed (byte* verb = method)
            fixed (byte* path = target)
                found = FamilyMethods.Owns(route, verb, (nuint)method.Length, path, (nuint)target.Length);
        });
        return found;
    }

    internal static async Task SendFamilyAsync(HttpContext context, NativeHttpCommonParameters parameters,
        INativeHttpFamilyBinding binding)
    {
        var headerCount = HeaderCount(context.Request);
        FamilyCapacity capacity = default;
        binding.BorrowRoute(route => CheckFamily(FamilyMethods.Capacity(route, headerCount, out capacity)));
        var bodyCapacity = binding.RequestBodyBytes;
        if (bodyCapacity < 0) throw new ArgumentOutOfRangeException(nameof(binding));
        var bytes = checked((ulong)capacity.StorageBytes + (ulong)capacity.Alignment - 1
            + (ulong)capacity.OutputBytes + (ulong)bodyCapacity);
        var factory = context.RequestServices.GetService<INativeHttpFamilyWorkspaceFactory>();
        using var resource = factory is null
            ? context.RequestServices.GetRequiredService<ResourceManagerLedgerOwner>().CreateScratch(bytes)
            : factory.Create(bytes);
        using var lease = resource.Acquire();
        using var regionPin = lease.GetMemoryChunk(0, 1).Pin();
        var offset = AlignedOffset(regionPin, capacity.Alignment);
        var storage = StoragePointer(regionPin, offset);
        var outputOffset = checked(offset + (ulong)capacity.StorageBytes);
        var window = lease.GetMemoryChunk(outputOffset, checked((int)capacity.OutputBytes));
        var input = bodyCapacity == 0 ? Memory<byte>.Empty
            : lease.GetMemoryChunk(checked(outputOffset + (ulong)capacity.OutputBytes), bodyCapacity);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var cancellation = context.RequestAborted.Register(static state => ((EventWaitHandle)state!).Set(), wake);
        INativeHttpFamilyInvocation? invocation = null;
        var initialized = false;
        var terminal = false;
        var cancellationSent = false;
        ExceptionDispatchInfo? failure = null;
        void BorrowCurrent(Action<nint> consume)
        {
            if (invocation is null) binding.BorrowRoute(consume);
            else invocation.Borrow(consume);
        }
        try
        {
            binding.BorrowRoute(route => InitializeFamily(storage, capacity, route, headerCount, wake.SafeWaitHandle.DangerousGetHandle()));
            initialized = true;
            var progress = RequestFamily(context, parameters, binding.BorrowRoute, storage, window, default, admission: true);
            if (progress.Kind == 3)
            {
                var body = await ReadFamilyBodyAsync(context.Request, input, context.RequestAborted);
                invocation = binding.OpenInvocation();
                progress = RequestFamily(context, parameters, invocation.Borrow, storage, window, body, admission: false);
                body = default;
            }
            var headSent = false;
            long sent = 0;
            long? total = null;
            while (progress.Kind != 2)
            {
                if (progress.Kind == 0) await WaitForWakeAsync(wake);
                else if (progress.Kind == 1)
                {
                    if (!headSent)
                    {
                        context.Response.StatusCode = progress.Status;
                        context.Response.ContentType = progress.ContentType;
                        var index = 0;
                        while (index < progress.Headers.Length)
                        {
                            var header = progress.Headers[index++];
                            context.Response.Headers.Append(header.Key, header.Value);
                        }
                        total = progress.TotalLength ?? (progress.More ? null : progress.Length);
                        // 204 has no entity or framing header on either path.
                        context.Response.ContentLength = progress.Status == StatusCodes.Status204NoContent ? null : total;
                        headSent = true;
                    }
                    sent = checked(sent + progress.Length);
                    if (total is { } expected && sent > expected) throw new InvalidDataException("Native response exceeds its declared length.");
                    await FlushAsync(context.Response.BodyWriter, context.RequestAborted);
                }
                var cancel = context.RequestAborted.IsCancellationRequested && !cancellationSent;
                cancellationSent |= cancel;
                progress = StepFamily(context, BorrowCurrent, storage, window,
                    cancel ? 2U : progress.Kind == 0 ? 0U : 1U);
            }
            terminal = true;
            if (total is { } declared && sent != declared) throw new InvalidDataException("Native response did not complete its declared length.");
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            if (context.Response.HasStarted) context.Abort();
        }
        finally
        {
            try
            {
                if (initialized)
                {
                    // Drain using the same owned workspace, even if the aborted
                    // HTTP writer can no longer provide output memory.
                    try
                    {
                        if (!terminal)
                        {
                            var progress = StepFamily(context, BorrowCurrent, storage, window, 2, discard: true);
                            while (progress.Kind != 2)
                            {
                                if (progress.Kind == 0) await WaitForWakeAsync(wake);
                                progress = StepFamily(context, BorrowCurrent, storage, window,
                                    progress.Kind == 0 ? 0U : 1U, discard: true);
                            }
                        }
                    }
                    catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                    finally { BorrowCurrent(route => CheckFamily(FamilyMethods.Release(storage, route))); }
                }
            }
            finally { invocation?.Dispose(); }
        }
        failure?.Throw();
    }

    private static nuint HeaderCount(HttpRequest request)
    {
        nuint count = 0;
        using var headers = request.Headers.GetEnumerator();
        while (headers.MoveNext()) count = checked(count + (nuint)headers.Current.Value.Count);
        return count;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadFamilyBodyAsync(HttpRequest request, Memory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0) return default;
        if (request.ContentLength > bytes.Length) throw new BadHttpRequestException("Request body exceeds the registered family capacity.", StatusCodes.Status413PayloadTooLarge);
        var used = 0;
        while (used < bytes.Length)
        {
            var read = await request.Body.ReadAsync(bytes[used..], cancellationToken);
            if (read == 0) return bytes[..used];
            used += read;
        }
        if (request.ContentLength is null)
        {
            var extra = new byte[1];
            if (await request.Body.ReadAsync(extra, cancellationToken) != 0)
                throw new BadHttpRequestException("Request body exceeds the registered family capacity.", StatusCodes.Status413PayloadTooLarge);
        }
        return bytes[..used];
    }

    private static async Task WaitForWakeAsync(EventWaitHandle wake)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wake,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), completion, Timeout.Infinite, true);
        try { await completion.Task; }
        finally { registration.Unregister(null); }
    }

    private static unsafe ulong AlignedOffset(System.Buffers.MemoryHandle pin, nuint alignment)
        => checked((ulong)((((nuint)pin.Pointer + alignment - 1) & ~(alignment - 1)) - (nuint)pin.Pointer));
    private static unsafe nint StoragePointer(System.Buffers.MemoryHandle pin, ulong offset)
        => checked((nint)((nuint)pin.Pointer + checked((nuint)offset)));
    private static unsafe void InitializeFamily(nint storage, FamilyCapacity capacity, nint route, nuint headers, nint wake)
        => CheckFamily(FamilyMethods.Init(storage, capacity.StorageBytes, route, headers, wake, &PostFamilyWake));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PostFamilyWake(nint context) => FamilyMethods.SetEvent(context);

    private static unsafe ManagedProgress RequestFamily(HttpContext context, NativeHttpCommonParameters parameters,
        Action<Action<nint>> borrow, nint storage, Memory<byte> memory, ReadOnlyMemory<byte> body, bool admission)
    {
        ManagedProgress result = null!;
        using (var output = memory.Pin())
        {
            var pointer = (nint)output.Pointer;
            borrow(route => WithFamilyRequest(context, parameters, body, request =>
            {
                request.Output = (byte*)pointer;
                request.OutputCapacity = checked((nuint)memory.Length);
                AbiFamilyProgress progress;
                CheckFamily(admission ? FamilyMethods.Admit(storage, in request, route, out progress)
                    : FamilyMethods.Start(storage, in request, route, out progress));
                result = CaptureProgress(context, in progress, memory);
            }));
        }
        if (result.Kind == 1) context.Response.BodyWriter.Advance(result.Length);
        return result;
    }

    private static unsafe ManagedProgress StepFamily(HttpContext context, Action<Action<nint>> borrow,
        nint storage, Memory<byte> memory, uint action, bool discard = false)
    {
        ManagedProgress result = null!;
        using (var output = memory.Pin())
        {
            var pointer = (nint)output.Pointer;
            borrow(route =>
            {
                CheckFamily(FamilyMethods.Step(storage, route, action, (byte*)pointer, checked((nuint)memory.Length), out var progress));
                result = CaptureProgress(context, in progress, memory, discard);
            });
        }
        if (!discard && result.Kind == 1) context.Response.BodyWriter.Advance(result.Length);
        return result;
    }

    private static unsafe ManagedProgress CaptureProgress(HttpContext context, in AbiFamilyProgress progress, Memory<byte> output, bool discard = false)
    {
        if (progress.Kind != 1) return new(checked((int)progress.Kind), 0, null, [], 0, false, null);
        if (progress.Body.Length > (nuint)output.Length) throw new InvalidDataException("Native family exceeded its final output window.");
        var headers = new KeyValuePair<string, string>[checked((int)progress.HeaderCount)];
        var index = 0;
        while (index < headers.Length)
        {
            var header = progress.Headers[index];
            headers[index++] = new(ReadUtf8(header.Name), ReadUtf8(header.Value));
        }
        var contentType = progress.ContentType.Pointer == null ? null : ReadUtf8(progress.ContentType);
        if (!discard)
        {
            var length = checked((int)progress.Body.Length);
            new ReadOnlySpan<byte>(progress.Body.Pointer, length).CopyTo(context.Response.BodyWriter.GetSpan(length));
        }
        return new(1, checked((int)progress.Status), contentType, headers, checked((int)progress.Body.Length),
            progress.More != 0, progress.TotalKnown != 0 ? checked((long)progress.TotalLength) : null);
    }

    private static unsafe string ReadUtf8(AbiSlice value) => Encoding.UTF8.GetString(value.Pointer, checked((int)value.Length));

    private static unsafe void WithFamilyRequest(HttpContext context, NativeHttpCommonParameters parameters,
        ReadOnlyMemory<byte> body, Action<AbiRequest> consume)
    {
        using var text = new Utf8Borrow();
        var headers = new List<AbiHeader>();
        using (var enumerator = context.Request.Headers.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                var name = text.Text(item.Key);
                var index = 0;
                while (index < item.Value.Count) headers.Add(new() { Name = name, Value = text.Text(item.Value[index++] ?? "") });
            }
        }
        WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out var token);
        using (token)
        using (var input = body.Pin())
        fixed (AbiHeader* headerPointer = CollectionsMarshal.AsSpan(headers))
        fixed (char* sid = caller?.WindowsSid)
        {
            var request = new AbiRequest
            {
                Version = 1, StructSize = (uint)sizeof(AbiRequest), Method = text.Text(DispatchMethod(context.Request)),
                Target = text.Text(RawTarget(context.Request)), Scheme = text.Text(context.Request.Scheme),
                Headers = headerPointer, HeaderCount = (nuint)headers.Count,
                Body = new() { Pointer = (byte*)input.Pointer, Length = (nuint)body.Length },
                ProfileId = text.Text(parameters.Capabilities.ProfileId), AllowedCapabilities = (uint)parameters.Capabilities.Allowed,
                NormalActionsAllowed = parameters.NormalActionsAllowed ? 1U : 0U, TestRoutes = parameters.TestRoutes ? 1U : 0U,
                Cancelled = context.RequestAborted.IsCancellationRequested ? 1U : 0U,
                LocalPort = (uint)context.Connection.LocalPort, RemotePort = (uint)context.Connection.RemotePort,
                ProcessId = caller?.ProcessId ?? 0, SessionId = caller?.WindowsSessionId ?? 0, CreatedTicks = caller?.ProcessCreatedUtcTicks ?? 0,
                IsAdministrator = caller?.IsAdministrator == true ? 1U : 0U, Sid = (ushort*)sid,
                SidLength = (nuint)(caller?.WindowsSid.Length ?? 0), CallerScope = token?.DangerousGetHandle() ?? 0
            };
            WriteAddress(context.Connection.LocalIpAddress, request.LocalAddress, out request.LocalFamily);
            WriteAddress(context.Connection.RemoteIpAddress, request.RemoteAddress, out request.RemoteFamily);
            consume(request);
        }
    }

    private static void CheckFamily(int code)
    {
        if (code == 3) throw new OperationCanceledException();
        if (code != 0) throw new InvalidOperationException($"Native HTTP family transport failed ({code}).");
    }

    private sealed record ManagedProgress(int Kind, int Status, string? ContentType,
        KeyValuePair<string, string>[] Headers, int Length, bool More, long? TotalLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct FamilyCapacity { internal nuint StorageBytes, Alignment, OutputBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AbiFamilyProgress
    {
        internal uint Kind, Status;
        internal AbiSlice ContentType;
        internal AbiHeader* Headers;
        internal nuint HeaderCount;
        internal AbiSlice Body;
        internal uint More, TotalKnown;
        internal ulong TotalLength;
    }
    private static partial class FamilyMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_requirement")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Requirement(nint route);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_owns")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Owns(nint route, byte* method, nuint methodLength, byte* target, nuint targetLength);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Capacity(nint route, nuint headerCount, out FamilyCapacity capacity);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_init")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Init(nint storage, nuint length, nint route, nuint headerCount, nint wake, delegate* unmanaged[Cdecl]<nint, void> post);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_admit")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Admit(nint storage, in AbiRequest request, nint route, out AbiFamilyProgress result);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_start")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Start(nint storage, in AbiRequest request, nint route, out AbiFamilyProgress result);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_step")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Step(nint storage, nint route, uint action, byte* output, nuint outputCapacity, out AbiFamilyProgress result);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_family_release")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Release(nint storage, nint route);
        [LibraryImport("kernel32.dll", EntryPoint = "SetEvent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetEvent(nint handle);
    }
}
