using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

// One UAC process performs one immutable operation through the existing backend API, then exits.
internal static class ElevatedProcessActionBroker
{
    internal static async Task<string> ExecuteAsync(ProcessActionBatch batch,
        FrontendSessionCredentialReply service, CancellationToken cancellationToken)
    {
        batch.Validate();
        using var own = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var request = new ElevatedProcessActionRequest(Guid.NewGuid().ToString("N"), own.Id,
            own.StartTime.ToUniversalTime().Ticks, service.ServiceProcessId,
            service.ServiceProcessCreatedUtcTicks, service.ServiceInstanceId, batch);
        using var pipe = WindowsSessionPipeFactory.Create(request.PipeName,
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
            start.ArgumentList.Add(ElevatedProcessActionRequest.Argument);
            start.ArgumentList.Add(request.Encode());
            helper = Process.Start(start);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new UacApprovalCanceledException(error); }
        using (helper)
        {
            if (helper is null) throw new IOException("The process-action helper did not start.");
            var helperStarted = helper.StartTime.ToUniversalTime().Ticks;
            var accepting = pipe.WaitForConnectionAsync(deadline.Token);
            while (!accepting.IsCompleted)
            {
                if (helper.HasExited) throw new IOException("The process-action helper exited before connecting.");
                await Task.WhenAny(accepting, Task.Delay(100, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
            await accepting;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id
                || !BackendProcessConnection.VerifyProcess(helper.Id, helperStarted, Environment.ProcessPath!))
                throw new UnauthorizedAccessException("The process-action reply has the wrong owner.");
            var hello = new byte[1];
            await pipe.ReadExactlyAsync(hello, deadline.Token);
            if (hello[0] != 1) throw new InvalidDataException("Invalid process-action handshake.");
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var result = await ElevatedProcessActionRequest.ReadResultAsync(pipe, deadline.Token);
            ValidateResult(result, batch);
            return result;
        }
    }

    internal static int? TryExecute(string[] args)
    {
        if (!args.Contains(ElevatedProcessActionRequest.Argument, StringComparer.Ordinal)) return null;
        if (args.Length != 2 || args[0] != ElevatedProcessActionRequest.Argument) return 64;
        try { return RunHelperAsync(ElevatedProcessActionRequest.Decode(args[1])).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or JsonException or ArgumentException or FormatException or HttpRequestException or OperationCanceledException)
        { return 2; }
    }

    private static async Task<int> RunHelperAsync(ElevatedProcessActionRequest request)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var own = Process.GetCurrentProcess();
        if (identity.IsSystem || own.SessionId == 0
            || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || request.ParentProcessId == own.Id
            || !BackendProcessConnection.VerifyProcess(request.ParentProcessId, request.ParentCreatedUtcTicks,
                Environment.ProcessPath!)) return 5;
        using var parent = Process.GetProcessById(request.ParentProcessId);
        if (parent.SessionId != own.SessionId) return 5;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var pipe = new NamedPipeClientStream(".", request.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var parentPid) || parentPid != request.ParentProcessId
            || !BackendProcessConnection.VerifyProcess(request.ParentProcessId, request.ParentCreatedUtcTicks,
                Environment.ProcessPath!)) return 5;
        await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        var permit = new byte[1];
        await pipe.ReadExactlyAsync(permit, deadline.Token);
        if (permit[0] != 1) return 5;

        var backend = new BackendProcessIdentity(request.ServiceProcessId,
            request.ServiceCreatedUtcTicks, BackendServiceSession.ResolveBackendPath());
        using var client = new HttpClient(BackendProcessConnection.CreateHandler(() => backend))
        { BaseAddress = new Uri("http://127.0.0.1:9321"), Timeout = TimeSpan.FromMinutes(2) };
        var challenge = RandomNumberGenerator.GetHexString(BackendRuntimeIdentityContract.ChallengeHexLength);
        var runtimeJson = await SendBoundedAsync(client, new(HttpMethod.Get,
            $"/api/runtime/identity?challenge={challenge}"), deadline.Token);
        var runtime = JsonSerializer.Deserialize<BackendRuntimeIdentityResponse>(runtimeJson,
            ElevatedProcessActionRequest.JsonOptions);
        if (runtime is null || runtime.ProductId != BackendRuntimeIdentityContract.ProductId
            || runtime.ProtocolVersion != BackendRuntimeIdentityContract.ProtocolVersion
            || runtime.InstanceId != request.ServiceInstanceId || runtime.ProcessId != backend.ProcessId
            || runtime.ProcessStartUtcTicks != backend.CreatedUtcTicks || runtime.Challenge != challenge
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(runtime.ExecutablePath), backend.ExecutablePath))
            throw new UnauthorizedAccessException("The backend generation changed.");
        var result = await SendBoundedAsync(client, new(HttpMethod.Post,
            "/api/system/processes/" + request.Batch.Action)
        { Content = JsonContent.Create(new { request.Batch.Targets }) }, deadline.Token);
        ValidateResult(result, request.Batch);
        await ElevatedProcessActionRequest.WriteResultAsync(pipe, result, deadline.Token);
        return 0;
    }

    private static async Task<string> SendBoundedAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentLength is > ElevatedProcessActionRequest.MaximumResultBytes)
                throw new InvalidDataException("Invalid backend process-action response.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (payload.Length + count > ElevatedProcessActionRequest.MaximumResultBytes)
                    throw new InvalidDataException("The backend response exceeds its bound.");
                payload.Write(buffer, 0, count);
            }
            return new System.Text.UTF8Encoding(false, true).GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
        }
    }

    internal static void ValidateResult(string payload, ProcessActionBatch batch)
    {
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() != batch.Targets.Count) throw new InvalidDataException("Incomplete process-action result.");
        var expected = batch.Targets.ToDictionary(target => target.ProcessId, target => target.ProcessStartKey);
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("processId", out var pid) || !pid.TryGetInt32(out var processId)
                || !expected.Remove(processId, out var start)
                || !item.TryGetProperty("processStartKey", out var key) || key.GetString() != start
                || !item.TryGetProperty("state", out var state) || state.GetString() is not ("Succeeded" or "Skipped" or "Failed"))
                throw new InvalidDataException("A process-action result changed its identity.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

internal sealed class UacApprovalCanceledException(Exception inner)
    : OperationCanceledException("The UAC request was canceled.", inner);
