using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

internal static partial class ElevatedProcessActionBroker
{
    internal static async Task<ElevatedBackendResponse> ExecuteBackendRequestAsync(ElevatedBackendMutation mutation,
        BackendProcessIdentity service, CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process?>? launchHelper = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mutation.Validate();
        using var own = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var invocation = new ElevatedBackendInvocation(Guid.NewGuid().ToString("N"), own.Id,
            own.StartTime.ToUniversalTime().Ticks, service.ProcessId, service.CreatedUtcTicks);
        using var pipe = WindowsSessionPipeFactory.Create(invocation.PipeName,
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{identity.User!.Value})", 4096, 4096);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        Process? helper;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, UseShellExecute = true, Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(ElevatedBackendInvocation.Argument);
            start.ArgumentList.Add(ElevatedBackendRequestWire.EncodeInvocation(invocation));
            deadline.Token.ThrowIfCancellationRequested();
            helper = launchHelper is null ? Process.Start(start) : launchHelper(start);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new UacApprovalCanceledException(error); }
        using (helper)
        {
            if (helper is null) throw new IOException("The elevated request helper did not start.");
            var created = helper.StartTime.ToUniversalTime().Ticks;
            var accepting = pipe.WaitForConnectionAsync(deadline.Token);
            while (!accepting.IsCompleted)
            {
                if (helper.HasExited) throw new IOException("The elevated request helper exited before connecting.");
                await Task.WhenAny(accepting, Task.Delay(100, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
            await accepting;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id
                || helper.SessionId != own.SessionId
                || !BackendProcessConnection.VerifyProcess(helper.Id, created, Environment.ProcessPath!))
                throw new UnauthorizedAccessException("The elevated request helper identity does not match.");
            var hello = new byte[1];
            await pipe.ReadExactlyAsync(hello, deadline.Token);
            if (hello[0] != 1) throw new InvalidDataException("Invalid elevated request handshake.");
            // No mutation bytes are passed via argv or released before attesting the actual helper.
            await ElevatedBackendRequestWire.WriteMutationAsync(pipe, mutation, deadline.Token);
            return await ElevatedBackendRequestWire.ReadResponseAsync(pipe, deadline.Token);
        }
    }

    internal static int? TryExecuteBackendRequest(string[] args)
    {
        if (!args.Contains(ElevatedBackendInvocation.Argument, StringComparer.Ordinal)) return null;
        if (args.Length != 2 || args[0] != ElevatedBackendInvocation.Argument) return 64;
        try { return RunBackendRequestHelperAsync(ElevatedBackendRequestWire.DecodeInvocation(args[1])).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or JsonException or ArgumentException or FormatException or HttpRequestException or OperationCanceledException)
        { return 2; }
    }

    private static bool IsAuthorizedElevatedHelperParent(int parentId, long parentCreatedUtcTicks)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var own = Process.GetCurrentProcess();
        if (identity.IsSystem || own.SessionId == 0
            || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || parentId == own.Id
            || !BackendProcessConnection.VerifyProcess(parentId, parentCreatedUtcTicks, Environment.ProcessPath!)) return false;
        using var parent = Process.GetProcessById(parentId);
        return parent.SessionId == own.SessionId;
    }

    private static async Task<int> RunBackendRequestHelperAsync(ElevatedBackendInvocation invocation)
    {
        if (!IsAuthorizedElevatedHelperParent(invocation.ParentProcessId, invocation.ParentCreatedUtcTicks)) return 5;
        using var packageUse = new NativeUiPackageUseLifetime();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var pipe = new NamedPipeClientStream(".", invocation.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var parentPid) || parentPid != invocation.ParentProcessId
            || !BackendProcessConnection.VerifyProcess(invocation.ParentProcessId, invocation.ParentCreatedUtcTicks,
                Environment.ProcessPath!)) return 5;
        await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        var mutation = await ElevatedBackendRequestWire.ReadMutationAsync(pipe, deadline.Token);
        var backendPath = BackendServiceSession.ResolveBackendPath();
        var backend = BackendProcessConnection.CaptureInstalledServiceIdentity(backendPath);
        if (backend is null || backend.ProcessId != invocation.ServiceProcessId
            || backend.CreatedUtcTicks != invocation.ServiceCreatedUtcTicks) return 5;
        using var client = new HttpClient(BackendProcessConnection.CreateHandler(() => backend))
        { BaseAddress = new Uri("http://127.0.0.1:9321"), Timeout = TimeSpan.FromMinutes(2) };
        var response = await SendBackendMutationAsync(client, mutation, deadline.Token);
        await ElevatedBackendRequestWire.WriteResponseAsync(pipe, response, deadline.Token);
        return 0;
    }

    internal static async Task<ElevatedBackendResponse> SendBackendMutationAsync(HttpClient client,
        ElevatedBackendMutation mutation, CancellationToken cancellationToken)
    {
        mutation.Validate();
        using var request = new HttpRequestMessage(new HttpMethod(mutation.Method), mutation.Path)
        { Content = new StringContent(mutation.Body, Encoding.UTF8, "application/json") };
        // This client has no key and no user-controlled headers. Actual admin policy remains authoritative.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var emptyResponse = response.StatusCode == System.Net.HttpStatusCode.NoContent
            || response.Content.Headers.ContentLength == 0;
        if (response.Content.Headers.ContentLength is > ElevatedBackendRequestWire.MaximumResponseBytes
            || !emptyResponse && (mediaType is null
                || !(mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Invalid elevated backend response content.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (payload.Length + count > ElevatedBackendRequestWire.MaximumResponseBytes)
                throw new InvalidDataException("Elevated backend response exceeds its bound.");
            payload.Write(buffer, 0, count);
        }
        var result = new ElevatedBackendResponse((int)response.StatusCode,
            new UTF8Encoding(false, true).GetString(payload.GetBuffer(), 0, checked((int)payload.Length)));
        result.Validate();
        return result;
    }
}
