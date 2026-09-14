using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class TrustedAdapterLeaseGrantCompiler(
    ITrustedAdapterRegistrationCatalog registrationCatalog)
    : ITrustedAdapterLeaseGrantCompiler
{
    private const TrustedAdapterCapability KnownCapabilities =
        TrustedAdapterCapability.Register
        | TrustedAdapterCapability.DispatchPolicy;

    public async ValueTask<TrustedAdapterLeaseGrant> CompileAsync(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseAssertion assertion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(assertion.Claims);
        cancellationToken.ThrowIfCancellationRequested();

        var adapterId = RequireIdentity(assertion.Claims.AdapterId, "adapter ID");
        var appId = RequireIdentity(assertion.Claims.AppId, "application ID");
        ValidateRequestedCapabilities(assertion.RequestedCapabilities);
        var registration = await registrationCatalog.FindAsync(
            adapterId,
            appId,
            cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            if (assertion.RequestedCapabilities != TrustedAdapterCapability.Register)
            {
                throw new UnauthorizedAccessException(
                    "An unregistered adapter may request only the registration capability.");
            }

            var executablePath = NormalizePath(caller.CanonicalExecutablePath);
            var executableDirectory = Path.GetDirectoryName(executablePath)
                ?? throw new UnauthorizedAccessException(
                    "The attested executable does not have a canonical program directory.");
            return new TrustedAdapterLeaseGrant(
                new TrustedAdapterInstanceClaims(
                    adapterId,
                    appId,
                    RequireIdentity(assertion.Claims.DisplayName, "display name"),
                    [executableDirectory]),
                TrustedAdapterCapability.Register);
        }

        var trustedClaims = ValidateRegistration(registration, adapterId, appId);
        if (!string.Equals(
                NormalizePath(caller.CanonicalExecutablePath),
                NormalizePath(registration.CanonicalExecutablePath),
                StringComparison.OrdinalIgnoreCase)
            || caller.ExecutableFileIdentity != registration.ExecutableFileIdentity)
        {
            throw new UnauthorizedAccessException(
                "The attested executable does not match the trusted adapter registration.");
        }

        if ((assertion.RequestedCapabilities & ~registration.CapabilityCeiling) != 0)
        {
            throw new UnauthorizedAccessException(
                "The requested adapter capabilities exceed the trusted registration ceiling.");
        }

        return new TrustedAdapterLeaseGrant(
            trustedClaims,
            assertion.RequestedCapabilities);
    }

    private static void ValidateRequestedCapabilities(TrustedAdapterCapability capabilities)
    {
        if (capabilities == TrustedAdapterCapability.None
            || (capabilities & TrustedAdapterCapability.Register) == 0
            || (capabilities & ~KnownCapabilities) != 0)
        {
            throw new ArgumentException(
                "The adapter capability assertion is invalid.",
                nameof(capabilities));
        }
    }

    private static TrustedAdapterInstanceClaims ValidateRegistration(
        TrustedAdapterRegistration registration,
        string assertedAdapterId,
        string assertedAppId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if ((registration.CapabilityCeiling & TrustedAdapterCapability.Register) == 0
            || (registration.CapabilityCeiling & ~KnownCapabilities) != 0
            || registration.ExecutableFileIdentity == default)
        {
            throw new InvalidDataException(
                "The trusted adapter registration capability or file identity is invalid.");
        }
        var executablePath = NormalizePath(registration.CanonicalExecutablePath);
        var trustedClaims = NormalizeTrustedClaims(registration.Claims);
        if (!string.Equals(trustedClaims.AdapterId, assertedAdapterId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(trustedClaims.AppId, assertedAppId, StringComparison.OrdinalIgnoreCase)
            || !trustedClaims.CanonicalProgramRootPaths.Any(root => IsWithinRoot(executablePath, root)))
        {
            throw new InvalidDataException(
                "The trusted adapter registration identity or program root is inconsistent.");
        }
        return trustedClaims;
    }

    private static TrustedAdapterInstanceClaims NormalizeTrustedClaims(
        TrustedAdapterInstanceClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var roots = (claims.CanonicalProgramRootPaths ?? [])
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            throw new InvalidDataException(
                "The trusted adapter registration has no canonical program roots.");
        }
        return new TrustedAdapterInstanceClaims(
            RequireIdentity(claims.AdapterId, "trusted adapter ID"),
            RequireIdentity(claims.AppId, "trusted application ID"),
            RequireIdentity(claims.DisplayName, "trusted display name"),
            roots);
    }

    private static string RequireIdentity(string? value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"The {field} must not be empty.", field);
        }
        return normalized;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("A canonical executable or root path is missing.");
        }
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
