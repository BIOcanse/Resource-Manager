using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Application.Settings;

namespace ResourceManager.App.Endpoints.Transport;

// Transitional Kestrel transport only. H-cutover removes this entry and its
// route registration. The native host owns matching, access and final bytes.
internal static partial class NativeHttpCommonEntry
{
    internal static bool Owns(HttpRequest request)
    {
        var method = Encoding.UTF8.GetBytes(request.Method);
        var target = Encoding.UTF8.GetBytes(RawTarget(request));
        return Owns(method, target);
    }

    private static unsafe bool Owns(byte[] method, byte[] target)
    {
        fixed (byte* methodPointer = method)
        fixed (byte* targetPointer = target)
            return NativeMethods.Owns(methodPointer, checked((nuint)method.Length),
                targetPointer, checked((nuint)target.Length)) != 0;
    }

    internal static async Task<bool> TryInvokeAsync(HttpContext context)
    {
        if (!Owns(Encoding.UTF8.GetBytes(DispatchMethod(context.Request)),
            Encoding.UTF8.GetBytes(RawTarget(context.Request)))) return await TryInvokeFamilyAsync(context);
        var capabilities = context.RequestServices.GetRequiredService<StartupCapabilitySet>();
        await SendProductAsync(context, capabilities);
        return true;
    }

    internal static async Task SendProductAsync(HttpContext context, StartupCapabilitySet capabilities)
    {
        // The native product capabilities descriptor is anonymous and has no
        // ordinary-action policy dependency.
        await SendAsync(context, new NativeHttpCommonParameters(capabilities,
            NormalActionsAllowed: false, TestRoutes: false));
    }

    // Body is prepared by the transport consumer only for a route that consumes
    // it. The first product route is GET capabilities, which never binds a body.
    // TestRoutes is passed exclusively by an explicitly registered test handler.
    internal static Task SendAsync(HttpContext context, NativeHttpCommonParameters parameters,
        ReadOnlyMemory<byte> body = default)
    {
        Encode(context, parameters, body);
        return FlushAsync(context.Response.BodyWriter, context.RequestAborted);
    }

    private static async Task FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        var result = await output.FlushAsync(cancellationToken);
        if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
    }

    private static string RawTarget(HttpRequest request)
    {
        var target = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return string.IsNullOrEmpty(target)
            ? string.Concat(request.PathBase.Value, request.Path.Value, request.QueryString.Value)
            : target;
    }

    // Kestrel endpoint matching accepts the standard verbs without regard to
    // case. Preserve that transport contract before entering the exact native
    // matcher; the raw Owns API above still reports native ownership unchanged.
    private static string DispatchMethod(HttpRequest request) => HttpMethods.GetCanonicalizedValue(request.Method);

    private static unsafe void Encode(HttpContext context, NativeHttpCommonParameters parameters,
        ReadOnlyMemory<byte> body)
    {
        using var borrow = new Utf8Borrow();
        var method = borrow.Text(DispatchMethod(context.Request));
        var target = borrow.Text(RawTarget(context.Request));
        var scheme = borrow.Text(context.Request.Scheme);
        var profile = borrow.Text(parameters.Capabilities.ProfileId);
        var headers = new List<AbiHeader>();
        using (var enumerator = context.Request.Headers.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                var header = enumerator.Current;
                var name = borrow.Text(header.Key);
                var index = 0;
                while (index < header.Value.Count)
                {
                    headers.Add(new AbiHeader { Name = name, Value = borrow.Text(header.Value[index] ?? "") });
                    index++;
                }
            }
        }

        // This reader uses the real connection tuple, opens the live process
        // token and verifies TCP ownership again. Keep that token alive until
        // native dispatch returns; request headers never supply caller facts.
        WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out var token);
        using (token)
        using (var bodyPin = body.Pin())
        {
            var outputBound = NativeMethods.OutputBound(profile.Length,
                checked((nuint)headers.Count), checked((nuint)body.Length));
            var output = context.Response.BodyWriter.GetMemory(checked((int)outputBound));
            AbiResult response;
            using (var outputPin = output.Pin())
            fixed (AbiHeader* headerPointer = CollectionsMarshal.AsSpan(headers))
            fixed (char* sid = caller?.WindowsSid)
            {
                var request = new AbiRequest
                {
                    Version = 1, StructSize = checked((uint)sizeof(AbiRequest)),
                    Method = method, Target = target, Scheme = scheme,
                    Headers = headerPointer, HeaderCount = checked((nuint)headers.Count),
                    Body = new AbiSlice { Pointer = (byte*)bodyPin.Pointer, Length = checked((nuint)body.Length) },
                    ProfileId = profile, AllowedCapabilities = (uint)parameters.Capabilities.Allowed,
                    NormalActionsAllowed = parameters.NormalActionsAllowed ? 1U : 0U,
                    TestRoutes = parameters.TestRoutes ? 1U : 0U,
                    Cancelled = context.RequestAborted.IsCancellationRequested ? 1U : 0U,
                    LocalPort = checked((uint)context.Connection.LocalPort),
                    RemotePort = checked((uint)context.Connection.RemotePort),
                    ProcessId = caller?.ProcessId ?? 0, SessionId = caller?.WindowsSessionId ?? 0,
                    CreatedTicks = caller?.ProcessCreatedUtcTicks ?? 0,
                    IsAdministrator = caller?.IsAdministrator == true ? 1U : 0U,
                    Sid = (ushort*)sid, SidLength = checked((nuint)(caller?.WindowsSid.Length ?? 0)),
                    CallerScope = token?.DangerousGetHandle() ?? 0,
                    Output = (byte*)outputPin.Pointer, OutputCapacity = checked((nuint)output.Length)
                };
                WriteAddress(context.Connection.LocalIpAddress, request.LocalAddress, out request.LocalFamily);
                WriteAddress(context.Connection.RemoteIpAddress, request.RemoteAddress, out request.RemoteFamily);
                var code = NativeMethods.Dispatch(in request, out response);
                if (code == 3) throw new OperationCanceledException(context.RequestAborted);
                if (code != 0) throw new InvalidOperationException($"Native HTTP dispatch failed ({code}).");
                if (response.Length > checked((nuint)output.Length))
                    throw new InvalidOperationException("Native HTTP output exceeds its borrowed segment.");
            }
            // Neither caller token nor a source/output pointer can cross the
            // later asynchronous flush. PipeWriter owns only final wire bytes.
            context.Response.StatusCode = checked((int)response.Status);
            context.Response.ContentType = response.ContentType switch
            {
                0 => null,
                1 => "application/json; charset=utf-8",
                _ => throw new InvalidOperationException("Unknown native HTTP content type.")
            };
            context.Response.ContentLength = checked((long)response.Length);
            context.Response.BodyWriter.Advance(checked((int)response.Length));
        }
    }

    private static unsafe void WriteAddress(IPAddress? address, byte* destination, out uint family)
    {
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        family = address?.AddressFamily switch
        {
            AddressFamily.InterNetwork => 4,
            AddressFamily.InterNetworkV6 => 6,
            _ => 0
        };
        new Span<byte>(destination, 16).Clear();
        address?.TryWriteBytes(new Span<byte>(destination, 16), out _);
    }

    private sealed unsafe class Utf8Borrow : IDisposable
    {
        private readonly List<GCHandle> pins = [];

        internal AbiSlice Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            pins.Add(pin);
            return new AbiSlice { Pointer = (byte*)pin.AddrOfPinnedObject(), Length = checked((nuint)bytes.Length) };
        }

        public void Dispose()
        {
            var index = pins.Count;
            while (index > 0) pins[--index].Free();
            pins.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AbiSlice { internal byte* Pointer; internal nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AbiHeader { internal AbiSlice Name, Value; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AbiRequest
    {
        internal uint Version, StructSize;
        internal AbiSlice Method, Target, Scheme;
        internal AbiHeader* Headers;
        internal nuint HeaderCount;
        internal AbiSlice Body, ProfileId;
        internal uint AllowedCapabilities, NormalActionsAllowed, TestRoutes, Cancelled;
        internal uint LocalFamily, RemoteFamily;
        internal fixed byte LocalAddress[16];
        internal fixed byte RemoteAddress[16];
        internal uint LocalPort, RemotePort;
        internal int ProcessId, SessionId;
        internal long CreatedTicks;
        internal uint IsAdministrator, Reserved;
        internal ushort* Sid;
        internal nuint SidLength;
        internal nint CallerScope;
        internal byte* Output;
        internal nuint OutputCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AbiResult { internal uint Status, ContentType; internal nuint Length; }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_owns")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Owns(byte* method, nuint methodLength, byte* target, nuint targetLength);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_output_bound")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nuint OutputBound(nuint profileLength, nuint headerCount, nuint bodyLength);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_common_dispatch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Dispatch(in AbiRequest request, out AbiResult response);
    }
}

internal sealed record NativeHttpCommonParameters(StartupCapabilitySet Capabilities,
    bool NormalActionsAllowed, bool TestRoutes);
