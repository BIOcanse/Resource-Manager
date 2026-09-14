using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.Shared.Runtime;

public static class LoopbackSessionProofContract
{
    public const string EndpointPath = "/api/runtime/session-proof";
    public const string ProductId = "ResourceManager.Backend.LoopbackSession";
    public const int ProtocolVersion = 1;
    public const int ChallengeHexLength = 64;
    public const int ProofHexLength = 64;

    private const string DomainSeparator = "ResourceManager.LoopbackSessionProof.v1\n";

    public static bool IsValidChallenge(string? challenge) =>
        IsHexString(challenge, ChallengeHexLength);

    public static string ComputeProof(string accessToken, string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (!IsValidChallenge(challenge))
        {
            throw new ArgumentException(
                "The loopback session challenge is invalid.",
                nameof(challenge));
        }

        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(accessToken),
            Encoding.UTF8.GetBytes(DomainSeparator + challenge)));
    }

    public static bool VerifyProof(
        string accessToken,
        string challenge,
        string? suppliedProof)
    {
        if (string.IsNullOrWhiteSpace(accessToken)
            || !IsValidChallenge(challenge)
            || !IsHexString(suppliedProof, ProofHexLength))
        {
            return false;
        }

        var expectedProof = ComputeProof(accessToken, challenge);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedProof),
            Encoding.ASCII.GetBytes(suppliedProof!));
    }

    private static bool IsHexString(string? value, int expectedLength)
    {
        if (value is null || value.Length != expectedLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record LoopbackSessionProofResponse(
    string ProductId,
    int ProtocolVersion,
    string Challenge,
    string Proof);
