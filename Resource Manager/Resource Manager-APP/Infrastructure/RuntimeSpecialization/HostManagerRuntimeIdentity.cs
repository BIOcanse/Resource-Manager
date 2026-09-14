using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerRuntimeIdentity
{
    public HostManagerRuntimeIdentity()
    {
        using var process = Process.GetCurrentProcess();
        InstanceId = CreateNonZeroId();
        ProcessId = Environment.ProcessId;
        ProcessCreatedAt = process.StartTime.ToUniversalTime();
    }

    public ulong InstanceId { get; }

    public int ProcessId { get; }

    public DateTime ProcessCreatedAt { get; }

    public long ProcessCreatedUtcTicks => ProcessCreatedAt.Ticks;

    private static ulong CreateNonZeroId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (value == 0);

        return value;
    }
}
