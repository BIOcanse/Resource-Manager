using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.Security;

public sealed class LoopbackApiAccessToken
{
    public const string HeaderName = "X-Resource-Manager-Token";
    public const string FileName = "loopback-api-token";
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Func<TrustedAdapterCallerIdentity, bool> ownerAlive;
    private readonly Func<HttpContext, TrustedAdapterCallerIdentity, bool> requestOwned;

    public LoopbackApiAccessToken(IHostEnvironment environment) : this(VerifyOwner, VerifyRequest)
    {
        // Migration only: a legacy shared credential is never loaded or accepted.
        var legacy = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Config", "Runtime", FileName);
        try { File.Delete(legacy); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal LoopbackApiAccessToken(Func<TrustedAdapterCallerIdentity, bool> ownerAlive,
        Func<HttpContext, TrustedAdapterCallerIdentity, bool> requestOwned)
        => (this.ownerAlive, this.requestOwned) = (ownerAlive, requestOwned);

    internal string Issue(TrustedAdapterCallerIdentity owner)
    {
        if (!ownerAlive(owner)) throw new UnauthorizedAccessException("The frontend identity is no longer live.");
        lock (gate)
        {
            foreach (var expired in entries.Where(pair => !ownerAlive(pair.Value.Owner)).Select(pair => pair.Key).ToArray())
                entries.Remove(expired);
            var existing = entries.Values.FirstOrDefault(entry => SameOwner(entry.Owner, owner));
            if (existing is not null) return existing.Token;
            if (entries.Count >= 128) throw new InvalidOperationException("Frontend session capacity reached.");
            var token = RandomNumberGenerator.GetHexString(FrontendSessionCredentialContract.AccessTokenHexLength);
            entries.Add(Key(token), new(token, owner));
            return token;
        }
    }

    public bool Validate(HttpContext context, string? suppliedToken)
    {
        if (suppliedToken is null || suppliedToken.Length != FrontendSessionCredentialContract.AccessTokenHexLength) return false;
        Entry? entry;
        lock (gate) entries.TryGetValue(Key(suppliedToken), out entry);
        if (entry is null || !ownerAlive(entry.Owner) || !requestOwned(context, entry.Owner)) return false;
        context.Items[typeof(TrustedAdapterCallerIdentity)] = entry.Owner;
        return true;
    }

    private static bool SameOwner(TrustedAdapterCallerIdentity first, TrustedAdapterCallerIdentity second)
        => first.HostInstanceId == second.HostInstanceId && first.ProcessId == second.ProcessId
            && first.ProcessCreatedUtcTicks == second.ProcessCreatedUtcTicks && first.WindowsSid == second.WindowsSid
            && first.WindowsSessionId == second.WindowsSessionId
            && first.ExecutableFileIdentity == second.ExecutableFileIdentity
            && StringComparer.OrdinalIgnoreCase.Equals(first.CanonicalExecutablePath, second.CanonicalExecutablePath);

    private static bool VerifyOwner(TrustedAdapterCallerIdentity owner)
    {
        try
        {
            var process = WindowsNamedPipeCallerAttester.ReadProcessIdentity(checked((uint)owner.ProcessId));
            return process.CreatedUtcTicks == owner.ProcessCreatedUtcTicks
                && process.FileIdentity == owner.ExecutableFileIdentity
                && StringComparer.OrdinalIgnoreCase.Equals(process.CanonicalExecutablePath, owner.CanonicalExecutablePath);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        { return false; }
    }

    private static bool VerifyRequest(HttpContext context, TrustedAdapterCallerIdentity owner)
        => WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var caller)
            && caller.ProcessId != Environment.ProcessId && caller.WindowsSid == owner.WindowsSid
            && caller.WindowsSessionId == owner.WindowsSessionId;

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private sealed record Entry(string Token, TrustedAdapterCallerIdentity Owner);
}

public static class LoopbackApiAuthenticationPolicy
{
    internal static bool IsAllowedBrowserRequest(HttpRequest request, int localPort)
    {
        var host = request.Host.Host.Trim('[', ']');
        var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        if (!localHost || port != localPort) return false;
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            && origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
            && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.UserInfo.Length == 0;
    }

    public static bool IsAuthorized(
        string expectedToken,
        string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)
            || string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        return expectedToken.Length == suppliedToken.Length
            && CryptographicOperations.FixedTimeEquals(
                MemoryMarshal.AsBytes(expectedToken.AsSpan()),
                MemoryMarshal.AsBytes(suppliedToken.AsSpan()));
    }
}

public sealed class LoopbackApiAuthenticationMiddleware(
    RequestDelegate next,
    LoopbackApiAccessToken accessToken,
    ILoopbackApiAdministratorReader administratorReader)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }
        if (!LoopbackApiAuthenticationPolicy.IsAllowedBrowserRequest(context.Request, context.Connection.LocalPort))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "loopback-origin-required" });
            return;
        }
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null
            && !accessToken.Validate(context, context.Request.Headers[LoopbackApiAccessToken.HeaderName].FirstOrDefault())
            && !administratorReader.IsAdministrator(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "frontend-or-administrator-required",
                message = "This API requires the corresponding frontend or an administrator caller."
            });
            return;
        }
        if (!context.Request.Path.StartsWithSegments("/api/public/v1"))
        {
            await next(context);
            return;
        }
        var policy = context.RequestServices.GetRequiredService<ILocalPublicServiceAccessPolicy>();
        var decision = await policy.EvaluateAsync(context, context.RequestAborted);
        if (!decision.Allowed)
        {
            context.Response.StatusCode = decision.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = decision.Reason }, context.RequestAborted);
            return;
        }
        try { await next(context); }
        finally { await policy.CompleteAsync(decision.CompletionHandle, CancellationToken.None); }
    }
}
