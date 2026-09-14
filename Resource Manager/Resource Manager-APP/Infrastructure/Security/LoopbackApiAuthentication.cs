using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Security;

public sealed class LoopbackApiAccessToken
{
    public const string HeaderName = "X-Resource-Manager-Token";
    public const string FileName = "loopback-api-token";

    public LoopbackApiAccessToken(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        StoragePath = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Config",
            "Runtime",
            FileName);
        Token = RandomNumberGenerator.GetHexString(64);
        WriteTokenAtomically(StoragePath, Token);
    }

    public string StoragePath { get; }

    public string Token { get; }

    public bool Validate(string? suppliedToken)
    {
        return LoopbackApiAuthenticationPolicy.IsAuthorized(
            Token,
            suppliedToken);
    }

    private static void WriteTokenAtomically(string path, string token)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Loopback token path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public static class LoopbackApiAuthenticationPolicy
{
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
    IRuntimePlanProvider runtimePlanProvider,
    ILoopbackApiAdministratorReader administratorReader)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || runtimePlanProvider.Current.Diagnostics.DebugModeEnabled
            || accessToken.Validate(
                context.Request.Headers[LoopbackApiAccessToken.HeaderName].FirstOrDefault())
            || administratorReader.IsAdministrator(context))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "frontend-or-administrator-required",
            message = "This API requires the corresponding frontend or an administrator caller."
        });
    }
}
