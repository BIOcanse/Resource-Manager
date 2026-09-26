using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal static class HostManagerPlanIdentity
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string ComputeDigest<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static ulong CreateGeneration<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        var hash = SHA256.HashData(bytes);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return generation == 0 ? 1UL : generation;
    }
}
