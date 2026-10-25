using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapTrustedAccessEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet(TrustedAccessApiContract.AccessKeysPath, async (
            HttpContext context,
            ITrustedAccessKeyService keys,
            CancellationToken cancellationToken) =>
        {
            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            var credentials = await keys.ListAsync(cancellationToken);
            var defaultKey = credentials.SingleOrDefault(static key =>
                key.IsDefault && key.Purpose == AccessKeyPurposes.Control);
            return Results.Ok(new TrustedAccessKeysSnapshotWireResponse(
                credentials.Where(static key => key.Purpose == AccessKeyPurposes.Control)
                    .Select(ToWire)
                    .ToArray(),
                defaultKey is not null,
                defaultKey?.Id,
                (authorization.IsAdministrator
                    ? TrustedAccessScopeCatalog.All
                    : authorization.EffectiveScopes).ToArray()));
        }).AllowAnonymous();

        app.MapPost(TrustedAccessApiContract.AccessKeysPath, async (
            TrustedAccessKeyCreateWireRequest? request,
            HttpContext context,
            ITrustedAccessKeyService keys,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeKeyRequest(request, out var name, out var purpose, out var scopes, out var error))
                return Results.BadRequest(new EndpointErrorPayload(error));

            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!CanGrantScopes(authorization, scopes))
                return Results.Json(new EndpointErrorPayload("access-scope-escalation-denied"),
                    statusCode: StatusCodes.Status403Forbidden);

            var created = await keys.CreateAsync(
                new AccessKeyCreateRequest(name, purpose, scopes),
                cancellationToken);
            return Results.Ok(new TrustedAccessKeyCreatedWireResponse(
                ToWire(created.Credential),
                created.ApiKey));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        app.MapDelete(TrustedAccessApiContract.AccessKeysPath + "/{id}", async (
            string id,
            HttpContext context,
            ITrustedAccessKeyService keys,
            CancellationToken cancellationToken) =>
        {
            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            var credentials = await keys.ListAsync(cancellationToken);
            var target = credentials.SingleOrDefault(key => StringComparer.Ordinal.Equals(key.Id, id));
            if (target is null)
                return Results.NotFound();
            if (!CanManageTarget(authorization, target))
                return Results.Json(new EndpointErrorPayload("access-key-management-scope-exceeded"),
                    statusCode: StatusCodes.Status403Forbidden);

            if (target.IsDefault)
                _ = await keys.SetDefaultAsync(false, cancellationToken);
            else
                await keys.RevokeAsync(target.Id, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapPost(TrustedAccessApiContract.AccessKeysPath + "/{id}/rotate", async (
            string id,
            HttpContext context,
            ITrustedAccessKeyService keys,
            CancellationToken cancellationToken) =>
        {
            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            var credentials = await keys.ListAsync(cancellationToken);
            var target = credentials.SingleOrDefault(key => StringComparer.Ordinal.Equals(key.Id, id));
            if (target is null)
                return Results.NotFound();
            if (!CanManageTarget(authorization, target))
                return Results.Json(new EndpointErrorPayload("access-key-management-scope-exceeded"),
                    statusCode: StatusCodes.Status403Forbidden);

            var rotated = await keys.RotateAsync(target.Id, cancellationToken);
            if (!target.IsDefault)
                return Results.Ok(new TrustedAccessKeyCreatedWireResponse(
                    ToWire(rotated.Credential),
                    rotated.ApiKey));

            var updated = (await keys.ListAsync(cancellationToken))
                .SingleOrDefault(static key => key.IsDefault);
            return updated is null
                ? Results.Json(new EndpointErrorPayload("default-key-unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new TrustedAccessDefaultWireResponse(true, updated.Id));
        }).AllowAnonymous();

        app.MapPost(TrustedAccessApiContract.AccessKeysPath + "/default", async (
            TrustedAccessDefaultWireRequest? request,
            HttpContext context,
            ITrustedAccessKeyService keys,
            CancellationToken cancellationToken) =>
        {
            if (request is null || (!request.Enabled && request.Rotate))
                return Results.BadRequest(new EndpointErrorPayload("default-key-request-invalid"));

            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!authorization.IsAdministrator
                && !ContainsAll(authorization.EffectiveScopes, TrustedAccessScopeCatalog.DefaultScopes))
            {
                return Results.Json(new EndpointErrorPayload("default-key-scope-escalation-denied"),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!request.Enabled)
            {
                _ = await keys.SetDefaultAsync(false, cancellationToken);
            }
            else if (request.Rotate)
            {
                var currentDefault = (await keys.ListAsync(cancellationToken))
                    .SingleOrDefault(static key => key.IsDefault);
                if (currentDefault is null)
                    _ = await keys.SetDefaultAsync(true, cancellationToken);
                else
                    _ = await keys.RotateAsync(currentDefault.Id, cancellationToken);
            }
            else
            {
                _ = await keys.SetDefaultAsync(true, cancellationToken);
            }

            var updatedDefault = (await keys.ListAsync(cancellationToken))
                .SingleOrDefault(static key => key.IsDefault);
            if (request.Enabled != (updatedDefault is not null))
                return Results.Json(new EndpointErrorPayload("default-key-state-unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new TrustedAccessDefaultWireResponse(
                updatedDefault is not null,
                updatedDefault?.Id));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        app.MapPost(TrustedAccessApiContract.AccessRequestsPath, async (
            TrustedAccessRequestCreateWireRequest? request,
            HttpContext context,
            ITrustedAccessRequestOwner requestOwner,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeRequest(request, out var name, out var scopes, out var error))
                return Results.BadRequest(new EndpointErrorPayload(error));
            if (!WindowsLoopbackApiAdministratorReader.TryReadConnectedCaller(context, out var caller))
                return Results.Unauthorized();

            var created = await requestOwner.CreateAsync(
                new TrustedAccessRequestCreateCommand(
                    name,
                    scopes,
                    caller.ExecutablePath,
                    caller.Caller.ProcessId),
                cancellationToken);
            if (!created.Created)
            {
                var statusCode = created.ErrorCode switch
                {
                    "request-capacity-reached" or "request-rate-limited" => StatusCodes.Status429TooManyRequests,
                    _ => StatusCodes.Status400BadRequest
                };
                return Results.Json(new EndpointErrorPayload(created.ErrorCode ?? "request-rejected"),
                    statusCode: statusCode);
            }

            return Results.Accepted(
                $"{TrustedAccessApiContract.AccessRequestsPath}/{created.RequestId}/wait",
                new TrustedAccessRequestCreatedWireResponse(
                    created.RequestId!,
                    created.DeliverySecret!,
                    Status(TrustedAccessRequestStatus.Pending),
                    created.ExpiresAt!.Value));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        app.MapPost(TrustedAccessApiContract.AccessRequestsPath + "/{requestId}/wait", async (
            string requestId,
            TrustedAccessRequestWaitWireRequest? request,
            ITrustedAccessRequestOwner requestOwner,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request?.DeliverySecret))
                return Results.BadRequest(new EndpointErrorPayload("request-proof-required"));

            var result = await requestOwner.WaitAsync(requestId, request.DeliverySecret, cancellationToken);
            if (result.ErrorCode is not null)
            {
                var statusCode = result.ErrorCode switch
                {
                    "request-wait-capacity-reached" => StatusCodes.Status429TooManyRequests,
                    "request-wait-already-active" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status401Unauthorized
                };
                return Results.Json(new EndpointErrorPayload(result.ErrorCode), statusCode: statusCode);
            }

            return Results.Ok(new TrustedAccessRequestWaitWireResponse(
                Status(result.Status),
                result.Key is null ? null : ToWire(result.Key),
                result.ApiKey,
                result.DenialReason is null ? null : DenialReason(result.DenialReason.Value)));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        app.MapPost(TrustedAccessApiContract.AccessRequestsPath + "/{requestId}/approve", async (
            string requestId,
            TrustedAccessRequestApproveWireRequest? request,
            HttpContext context,
            ITrustedAccessRequestOwner requestOwner,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeScopes(request?.Scopes, out var scopes))
                return Results.BadRequest(new EndpointErrorPayload("approved-scopes-invalid"));
            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);

            var result = await requestOwner.ApproveAsync(
                requestId,
                scopes,
                authorization.EffectiveScopes,
                authorization.IsAdministrator,
                cancellationToken);
            if (result.ErrorCode is "request-not-pending" or "request-expired")
            {
                return Results.Json(new TrustedAccessRequestDecisionWireResponse(
                    Status(result.Status),
                    result.Key is null ? null : ToWire(result.Key),
                    result.DenialReason is null ? null : DenialReason(result.DenialReason.Value)),
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (result.ErrorCode is not null)
            {
                var statusCode = result.ErrorCode switch
                {
                    "request-not-found" => StatusCodes.Status404NotFound,
                    "approved-scope-exceeds-grant" => StatusCodes.Status403Forbidden,
                    "approved-scope-not-requested" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status409Conflict
                };
                return Results.Json(new EndpointErrorPayload(result.ErrorCode), statusCode: statusCode);
            }

            return Results.Ok(new TrustedAccessRequestDecisionWireResponse(
                Status(result.Status),
                result.Key is null ? null : ToWire(result.Key),
                result.DenialReason is null ? null : DenialReason(result.DenialReason.Value)));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        app.MapPost(TrustedAccessApiContract.AccessRequestsPath + "/{requestId}/deny", async (
            string requestId,
            TrustedAccessRequestDenyWireRequest? request,
            HttpContext context,
            ITrustedAccessRequestOwner requestOwner,
            CancellationToken cancellationToken) =>
        {
            var authorization = ReadAuthorization(context);
            if (authorization is null)
                return Results.Unauthorized();
            if (!CanManageKeys(authorization))
                return Results.Json(new EndpointErrorPayload("access-scope-required"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!TryParseDenialReason(request?.ReasonCode, out var reason))
                return Results.BadRequest(new EndpointErrorPayload("denial-reason-invalid"));

            var result = await requestOwner.DenyAsync(requestId, reason, cancellationToken);
            if (result.ErrorCode == "request-not-pending")
            {
                return Results.Json(new TrustedAccessRequestDecisionWireResponse(
                    Status(result.Status),
                    result.Key is null ? null : ToWire(result.Key),
                    result.DenialReason is null ? null : DenialReason(result.DenialReason.Value)),
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (result.ErrorCode is not null)
            {
                var statusCode = result.ErrorCode == "request-not-found"
                    ? StatusCodes.Status404NotFound
                    : result.ErrorCode == "denial-reason-invalid"
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status409Conflict;
                return Results.Json(new EndpointErrorPayload(result.ErrorCode), statusCode: statusCode);
            }

            return Results.Ok(new TrustedAccessRequestDecisionWireResponse(
                Status(result.Status),
                result.Key is null ? null : ToWire(result.Key),
                result.DenialReason is null ? null : DenialReason(result.DenialReason.Value)));
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            TrustedAccessApiContract.AccessRequestMaximumBodyBytes));

        return app;
    }

    internal static TrustedAccessKeyWireSummary ToWire(AccessKeySummary key)
        => new(key.Id, key.Name, key.Purpose, key.Scopes.ToArray(), key.CreatedAt, key.IsDefault);

    private static TrustedAccessAuthorizationContext? ReadAuthorization(HttpContext context)
        => context.Items.TryGetValue(typeof(TrustedAccessAuthorizationContext), out var value)
            ? value as TrustedAccessAuthorizationContext
            : null;

    private static bool CanManageKeys(TrustedAccessAuthorizationContext? authorization)
        => authorization is not null
            && (authorization.IsAdministrator
                || authorization.EffectiveScopes.Contains(AccessKeyScopes.KeysManage, StringComparer.Ordinal));

    private static bool CanGrantScopes(
        TrustedAccessAuthorizationContext authorization,
        IReadOnlyCollection<string> scopes)
        => authorization.IsAdministrator || ContainsAll(authorization.EffectiveScopes, scopes);

    private static bool CanManageTarget(
        TrustedAccessAuthorizationContext authorization,
        AccessKeySummary target)
        => authorization.IsAdministrator || ContainsAll(authorization.EffectiveScopes, target.Scopes);

    private static bool TryNormalizeKeyRequest(
        TrustedAccessKeyCreateWireRequest? request,
        out string name,
        out string purpose,
        out string[] scopes,
        out string error)
    {
        name = request?.Name?.Trim() ?? string.Empty;
        purpose = request?.Purpose?.Trim() ?? string.Empty;
        scopes = request?.Scopes ?? [];
        error = string.Empty;
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
        {
            error = "access-key-name-invalid";
            return false;
        }
        if (!StringComparer.Ordinal.Equals(purpose, AccessKeyPurposes.Control))
        {
            error = "access-key-purpose-invalid";
            return false;
        }
        if (!TryNormalizeScopes(scopes, out scopes))
        {
            error = "access-key-scopes-invalid";
            return false;
        }
        return true;
    }

    private static bool TryNormalizeRequest(
        TrustedAccessRequestCreateWireRequest? request,
        out string name,
        out string[] scopes,
        out string error)
    {
        name = request?.Name?.Trim() ?? string.Empty;
        scopes = request?.RequestedScopes ?? [];
        error = string.Empty;
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
        {
            error = "request-name-invalid";
            return false;
        }
        if (!TryNormalizeScopes(scopes, out scopes))
        {
            error = "request-scopes-invalid";
            return false;
        }
        return true;
    }

    private static bool TryNormalizeScopes(
        IReadOnlyCollection<string>? input,
        out string[] scopes)
    {
        scopes = input?.ToArray() ?? [];
        return scopes.Length is > 0 and <= 11
            && scopes.All(static scope => TrustedAccessScopeCatalog.All.Contains(scope, StringComparer.Ordinal))
            && scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Length;
    }

    private static bool ContainsAll(
        IReadOnlyCollection<string> granted,
        IEnumerable<string> requested)
    {
        var grants = new HashSet<string>(granted, StringComparer.Ordinal);
        return requested.All(grants.Contains);
    }

    private static bool TryParseDenialReason(
        string? code,
        out TrustedAccessRequestDenialReason reason)
    {
        reason = code switch
        {
            "not-approved" => TrustedAccessRequestDenialReason.NotApproved,
            "unsupported" => TrustedAccessRequestDenialReason.Unsupported,
            "duplicate" => TrustedAccessRequestDenialReason.Duplicate,
            "not-needed" => TrustedAccessRequestDenialReason.NotNeeded,
            _ => (TrustedAccessRequestDenialReason)byte.MaxValue
        };
        return Enum.IsDefined(reason);
    }

    private static string DenialReason(TrustedAccessRequestDenialReason reason)
        => reason switch
        {
            TrustedAccessRequestDenialReason.NotApproved => "not-approved",
            TrustedAccessRequestDenialReason.Unsupported => "unsupported",
            TrustedAccessRequestDenialReason.Duplicate => "duplicate",
            TrustedAccessRequestDenialReason.NotNeeded => "not-needed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    private static string Status(TrustedAccessRequestStatus status)
        => status switch
        {
            TrustedAccessRequestStatus.Pending => "pending",
            TrustedAccessRequestStatus.Approved => "approved",
            TrustedAccessRequestStatus.Denied => "denied",
            TrustedAccessRequestStatus.Expired => "expired",
            TrustedAccessRequestStatus.Delivered => "delivered",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}
