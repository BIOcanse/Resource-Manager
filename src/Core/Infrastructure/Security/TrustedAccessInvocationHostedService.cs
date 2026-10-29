using System.IO.Pipes;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security;

/// <summary>
/// Serves native GPU and interactive-session operations. Bearer keys
/// carry purpose and scope; keyless calls require a separately verified Windows administrator.
/// </summary>
public sealed class TrustedAccessInvocationHostedService(
    ITrustedAccessKeyService keys,
    IGpuStartupPlacementResolver resolver,
    IGpuLaunchExecutionReportStore reports,
    IInteractiveUserSessionBroker sessions,
    HostManagerRuntimeIdentity runtime,
    ILogger<TrustedAccessInvocationHostedService> logger,
    string pipeName = TrustedAccessInvocationContract.PipeName) : BackgroundService
{
    private const string PipeAcl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;BU)";
    private const int MaximumPayloadCharacters = 64 * 1024;
    private long nextConnection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var pipe = WindowsLocalPipeFactory.Create(
                    pipeName,
                    PipeAcl,
                    TrustedAccessInvocationContract.MaximumFrameBytes,
                    TrustedAccessInvocationContract.MaximumFrameBytes);
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ServeConnectionAsync(pipe, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError("Trusted Access invocation pipe stopped ({ErrorType}).", error.GetType().Name);
        }
    }

    internal async Task ServeConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        string? operation = "unknown";
        string? authorizationId = null;
        var result = TrustedAccessInvocationContract.InvalidRequest;
        try
        {
            var caller = new WindowsNamedPipeCallerAttester().Attest(
                pipe,
                runtime.InstanceId,
                checked((ulong)Interlocked.Increment(ref nextConnection)));
            var request = await TrustedAccessInvocationContract.ReadRequestAsync(pipe, deadline.Token);
            var verifiedAdministrator = (request.Key.Length == 0
                || request.Operation == TrustedAccessInvocationContract.SessionLaunchOperation)
                && IsVerifiedAdministrator(caller);
            operation = request.Operation switch
            {
                TrustedAccessInvocationContract.GpuResolveOperation => TrustedAccessInvocationContract.GpuResolveOperation,
                TrustedAccessInvocationContract.GpuReportOperation => TrustedAccessInvocationContract.GpuReportOperation,
                TrustedAccessInvocationContract.SessionLaunchOperation => TrustedAccessInvocationContract.SessionLaunchOperation,
                _ => "unknown"
            };
            var response = await InvokeAsync(request, caller, verifiedAdministrator, deadline.Token,
                id => authorizationId = id);
            result = response.Status;
            await TrustedAccessInvocationContract.WriteResponseAsync(pipe, response, deadline.Token);

            // Keep the response available until the one-request native client closes its end.
            var end = new byte[1];
            if (await pipe.ReadAsync(end, deadline.Token) != 0)
            {
                throw new InvalidDataException("Unexpected bytes after the trusted access response.");
            }
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                result = error is InvalidDataException or JsonException
                    ? TrustedAccessInvocationContract.InvalidRequest
                    : error is UnauthorizedAccessException or System.ComponentModel.Win32Exception
                        ? TrustedAccessInvocationContract.Unauthorized
                        : TrustedAccessInvocationContract.Unavailable;
                logger.LogDebug("Trusted Access invocation {Operation} rejected ({ErrorType}).",
                    operation ?? "unknown", error.GetType().Name);
            }
            if (pipe.IsConnected && error is (InvalidDataException or JsonException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception))
            {
                try
                {
                    await TrustedAccessInvocationContract.WriteResponseAsync(
                        pipe,
                        new(result, null),
                        deadline.Token);
                }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException or InvalidDataException)
                {
                }
            }
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Trusted Access invocation authorization {AuthorizationId} operation {Operation} completed with {Result}.",
                    authorizationId ?? "unknown",
                    operation ?? "unknown",
                    result);
            }
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
    }

    internal async Task<TrustedAccessInvocationResponse> InvokeAsync(
        TrustedAccessInvocationRequest request,
        TrustedAdapterCallerIdentity caller,
        bool verifiedAdministrator,
        CancellationToken stoppingToken,
        Action<string> authorizationAccepted)
    {
        if (request.Operation is not (TrustedAccessInvocationContract.GpuResolveOperation
            or TrustedAccessInvocationContract.GpuReportOperation
            or TrustedAccessInvocationContract.SessionLaunchOperation)
            || request.Payload.Length > MaximumPayloadCharacters)
        {
            return new(TrustedAccessInvocationContract.InvalidRequest, null);
        }

        AccessKeySummary? credential = null;
        if (request.Key.Length == 0)
        {
            if (!verifiedAdministrator)
            {
                return new(TrustedAccessInvocationContract.Unauthorized, null);
            }
            authorizationAccepted("administrator-bypass");
        }
        else
        {
            try
            {
                credential = await keys.AuthenticateAsync(
                    AccessKeyPurposes.Control,
                    request.Key,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning("Trusted Access authentication is unavailable ({ErrorType}).", error.GetType().Name);
                return new(TrustedAccessInvocationContract.Unavailable, null);
            }

            if (credential is null)
            {
                return new(TrustedAccessInvocationContract.Unauthorized, null);
            }
            // The completion log names the authenticated key, so a holder or scope refusal below is attributable.
            authorizationAccepted(credential.Id);
            // Same holder rule as HTTP: a key carrying a scope whose holder must be an elevated administrator is
            // refused from any other caller (docs/trusted_access_scopes_2026-10-29.md). Keyed calls arrive without
            // the administrator fact, so it is read here only for such keys. The pipe answers Forbidden, the same
            // status as a missing scope.
            if (AccessKeyScopes.RequiresAdministratorHolder(credential.Scopes)
                && !verifiedAdministrator && !IsVerifiedAdministrator(caller))
            {
                return new(TrustedAccessInvocationContract.Forbidden, null);
            }

            var requiredScope = request.Operation switch
            {
                TrustedAccessInvocationContract.GpuResolveOperation => AccessKeyScopes.GpuResolve,
                TrustedAccessInvocationContract.SessionLaunchOperation => AccessKeyScopes.Session,
                _ => AccessKeyScopes.GpuReport
            };
            if (!credential.Scopes.Contains(requiredScope, StringComparer.Ordinal))
            {
                return new(TrustedAccessInvocationContract.Forbidden, null);
            }
        }

        var revoked = CancellationToken.None;
        if (credential is not null)
        {
            try
            {
                revoked = keys.GetRevocationToken(credential.Id);
            }
            catch (Exception error)
            {
                logger.LogWarning("Trusted Access revocation state is unavailable ({ErrorType}).", error.GetType().Name);
                return new(TrustedAccessInvocationContract.Unavailable, null);
            }
        }
        if (revoked.IsCancellationRequested)
        {
            return new(TrustedAccessInvocationContract.Unauthorized, null);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, revoked);
        try
        {
            if (request.Operation == TrustedAccessInvocationContract.SessionLaunchOperation)
            {
                var command = SessionLaunchContract.Decode(request.Payload);
                operationCancellation.Token.ThrowIfCancellationRequested();
                var processId = SessionLaunchExecutor.Launch(sessions, caller, verifiedAdministrator, command);
                // An accepted launch is already committed; cancellation does not pretend to undo it.
                return new(TrustedAccessInvocationContract.Success, SessionLaunchContract.EncodeResult(processId));
            }

            if (request.Operation == TrustedAccessInvocationContract.GpuResolveOperation)
            {
                if (string.IsNullOrWhiteSpace(request.Payload) || request.Payload.Length > 32_767)
                {
                    return new(TrustedAccessInvocationContract.InvalidRequest, null);
                }
                var decision = await resolver.ResolveAsync(
                    new GpuStartupPlacementRequest(request.Payload),
                    operationCancellation.Token);
                operationCancellation.Token.ThrowIfCancellationRequested();
                return new(TrustedAccessInvocationContract.Success, GpuLaunchBrokerProtocol.Serialize(decision));
            }

            if (!GpuLaunchBrokerProtocol.TryParseExecutionReport(request.Payload, out var report, out _)
                || report is null)
            {
                return new(TrustedAccessInvocationContract.InvalidRequest, null);
            }

            await reports.RecordAsync(report, operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            return new(TrustedAccessInvocationContract.Success, "ok");
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested
            && !stoppingToken.IsCancellationRequested)
        {
            return new(TrustedAccessInvocationContract.Unauthorized, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (request.Operation == TrustedAccessInvocationContract.SessionLaunchOperation
            && error is (InvalidDataException or JsonException or ArgumentException))
        {
            return new(TrustedAccessInvocationContract.InvalidRequest, null);
        }
        catch (UnauthorizedAccessException) when (request.Operation == TrustedAccessInvocationContract.SessionLaunchOperation)
        {
            return new(TrustedAccessInvocationContract.Forbidden, null);
        }
        catch (Exception error)
        {
            logger.LogWarning("Trusted Access operation {Operation} failed ({ErrorType}).",
                request.Operation,
                error.GetType().Name);
            return new(TrustedAccessInvocationContract.Unavailable, null);
        }
    }

    private static bool IsVerifiedAdministrator(TrustedAdapterCallerIdentity attestedCaller)
    {
        if (!WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(
                attestedCaller.ProcessId,
                out var process))
        {
            return false;
        }

        var caller = process.Caller;
        return caller.ProcessId == attestedCaller.ProcessId
            && caller.ProcessCreatedUtcTicks == attestedCaller.ProcessCreatedUtcTicks
            && StringComparer.Ordinal.Equals(caller.WindowsSid, attestedCaller.WindowsSid)
            && caller.WindowsSessionId == attestedCaller.WindowsSessionId
            && caller.IsAdministrator;
    }
}
