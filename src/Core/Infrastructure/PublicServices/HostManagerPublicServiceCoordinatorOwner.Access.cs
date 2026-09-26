using System.Net;
using ResourceManager.App.Domain.PublicServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

public sealed partial class HostManagerPublicServiceCoordinatorOwner
{
    public Task<LocalPublicServiceAccessDecision> EvaluateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var holder = AcquireCurrent();
        try
        {
            var output = holder.Workspace.Admit(
                holder.Plan.HotPublish.AnonymousCallerHandle,
                ResolveRemoteScope(context.Connection.RemoteIpAddress),
                ResolveMethod(context.Request.Method),
                context.Request.Path.Value ?? "/");
            if (output.Allowed == 0)
            {
                holder.Release();
                return Task.FromResult(new LocalPublicServiceAccessDecision(
                    false,
                    checked((int)output.StatusCode),
                    ResolveReason((NativePublicServiceAccessReason)output.Reason),
                    0));
            }

            if (output.RequestHandle == 0)
            {
                throw new InvalidOperationException(
                    "The native public-service coordinator admitted a request without an identity.");
            }
            var completionHandle = NextHandle(
                ref nextRequestCompletionHandle,
                "Public-service request completion");
            if (!requestCompletions.TryAdd(
                    completionHandle,
                    new RequestCompletion(holder, output.RequestHandle)))
            {
                throw new InvalidOperationException(
                    "The public-service request completion identity collided.");
            }
            return Task.FromResult(new LocalPublicServiceAccessDecision(
                true,
                checked((int)output.StatusCode),
                ResolveReason((NativePublicServiceAccessReason)output.Reason),
                completionHandle));
        }
        catch
        {
            holder.Release();
            throw;
        }
    }

    public ValueTask CompleteAsync(
        ulong completionHandle,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (completionHandle == 0
            || !requestCompletions.TryGetValue(completionHandle, out var completion))
        {
            throw new InvalidOperationException(
                "The public-service request completion identity is unknown.");
        }
        if (!completion.TryBegin())
        {
            throw new InvalidOperationException(
                "The public-service request completion is already in progress.");
        }

        try
        {
            completion.Holder.Workspace.CompleteRequest(completion.NativeRequestHandle);
            if (!requestCompletions.TryRemove(
                    new KeyValuePair<ulong, RequestCompletion>(
                        completionHandle,
                        completion)))
            {
                throw new InvalidOperationException(
                    "The public-service request completion identity changed unexpectedly.");
            }
            completion.Holder.Release();
            RetryDesiredPlan();
            return ValueTask.CompletedTask;
        }
        catch
        {
            completion.Retry();
            throw;
        }
    }

    private static NativePublicServiceRemoteScope ResolveRemoteScope(IPAddress? address)
        => address is null
            ? NativePublicServiceRemoteScope.Unknown
            : IPAddress.IsLoopback(address)
                ? NativePublicServiceRemoteScope.Loopback
                : NativePublicServiceRemoteScope.Remote;

    private static NativePublicServiceHttpMethod ResolveMethod(string method)
    {
        if (HttpMethods.IsGet(method))
        {
            return NativePublicServiceHttpMethod.Get;
        }
        if (HttpMethods.IsHead(method))
        {
            return NativePublicServiceHttpMethod.Head;
        }
        if (HttpMethods.IsPost(method))
        {
            return NativePublicServiceHttpMethod.Post;
        }
        if (HttpMethods.IsPut(method))
        {
            return NativePublicServiceHttpMethod.Put;
        }
        if (HttpMethods.IsDelete(method))
        {
            return NativePublicServiceHttpMethod.Delete;
        }
        if (HttpMethods.IsPatch(method))
        {
            return NativePublicServiceHttpMethod.Patch;
        }
        return NativePublicServiceHttpMethod.Other;
    }

    private static string ResolveReason(NativePublicServiceAccessReason reason)
        => reason switch
        {
            NativePublicServiceAccessReason.Allowed => "allowed",
            NativePublicServiceAccessReason.RemoteForbidden => "loopback-only",
            NativePublicServiceAccessReason.ServiceDisabled => "service-disabled",
            NativePublicServiceAccessReason.RouteNotFound => "unknown-capability",
            NativePublicServiceAccessReason.CapabilityDisabled => "capability-disabled",
            NativePublicServiceAccessReason.MethodNotSupported => "method-not-supported",
            NativePublicServiceAccessReason.RateLimited => "rate-limited",
            NativePublicServiceAccessReason.CallerInflightLimit => "caller-inflight-limit",
            NativePublicServiceAccessReason.CoordinatorCapacity => "coordinator-capacity",
            _ => throw new InvalidOperationException(
                $"Unknown native public-service access reason {reason}.")
        };
}
