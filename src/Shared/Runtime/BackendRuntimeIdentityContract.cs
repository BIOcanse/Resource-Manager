namespace ResourceManager.Shared.Runtime;

public static class BackendRuntimeIdentityContract
{
    public const string ProductId = "ResourceManager.Backend";
    public const int ProtocolVersion = 1;
    public const int ChallengeHexLength = 64;

    public static bool IsValidChallenge(string? challenge)
    {
        if (challenge is null || challenge.Length != ChallengeHexLength)
        {
            return false;
        }

        foreach (var character in challenge)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record BackendRuntimeIdentityResponse(
    string ProductId,
    int ProtocolVersion,
    string BuildVersion,
    ulong InstanceId,
    int ProcessId,
    long ProcessStartUtcTicks,
    string ExecutablePath,
    string Challenge);

public sealed record BackendStartupCapabilitiesResponse(
    string ProfileId,
    bool ReadOnly,
    bool MutablePersistence,
    bool LegacyPersistenceImport,
    bool GpuLaunchInterceptionReconciliation,
    bool RuntimeEffectOwners,
    bool PublicServiceCoordination,
    bool OptimizationRuntime,
    bool SharedResourceOwnership);
