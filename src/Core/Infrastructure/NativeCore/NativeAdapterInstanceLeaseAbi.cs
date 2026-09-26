using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeAdapterInstanceLeaseAbi
{
    public const uint Version = 0x0003_0000;
    public const uint HardCapacityLimit = 65_536;

    public static void ValidateManagedLayout()
    {
        RequireSize<NativeAdapterInstanceLeaseConfig>(40);
        RequireSize<NativeTrustedAdapterCallerFacts>(152);
        RequireSize<NativeAdapterInstanceLeaseIssueInput>(184);
        RequireSize<NativeAdapterInstanceLeaseRenewInput>(192);
        RequireSize<NativeAdapterInstanceLeaseResolveInput>(48);
        RequireSize<NativeAdapterInstanceLeaseRevokeInput>(32);
        RequireSize<NativeAdapterInstanceLeaseReceipt>(232);
    }

    private static void RequireSize<T>(int expected) where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Managed adapter-instance lease ABI layout mismatch for {typeof(T).Name}: {actual} != {expected}.");
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public uint Capacity;
    public uint Reserved0;
    public ulong HostInstanceId;
    public ulong LeaseDurationTicks;
    public ulong MaximumAttestationAgeTicks;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTrustedAdapterCallerFacts
{
    public ulong HostInstanceId;
    public ulong TransportConnectionId;
    public long ProcessCreatedUtcTicks;
    public ulong AttestedAtTimestamp;
    public ulong AuthenticationIdLuid;
    public ulong SidDigest0;
    public ulong SidDigest1;
    public ulong SidDigest2;
    public ulong SidDigest3;
    public ulong ImagePathDigest0;
    public ulong ImagePathDigest1;
    public ulong ImagePathDigest2;
    public ulong ImagePathDigest3;
    public ulong ExecutableFileIdHigh;
    public ulong ExecutableFileIdLow;
    public ulong VolumeSerialNumber;
    public uint ProcessId;
    public uint WindowsSessionId;
    public uint IntegrityLevelRid;
    public byte IsElevated;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
    public byte Reserved3;
    public byte Reserved4;
    public byte Reserved5;
    public byte Reserved6;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseIssueInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong OwnerApplicationKey;
    public ulong Capabilities;
    public ulong NowTimestamp;
    public NativeTrustedAdapterCallerFacts Caller;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseRenewInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LeaseIdHigh;
    public ulong LeaseIdLow;
    public ulong ExpectedGeneration;
    public ulong NowTimestamp;
    public NativeTrustedAdapterCallerFacts Caller;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseResolveInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LeaseIdHigh;
    public ulong LeaseIdLow;
    public ulong ExpectedGeneration;
    public ulong RequiredCapabilities;
    public ulong NowTimestamp;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseRevokeInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LeaseIdHigh;
    public ulong LeaseIdLow;
    public ulong ExpectedGeneration;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeAdapterInstanceLeaseReceipt
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LeaseIdHigh;
    public ulong LeaseIdLow;
    public ulong InstanceIdHigh;
    public ulong InstanceIdLow;
    public ulong OwnerApplicationKey;
    public ulong Capabilities;
    public ulong IssuedAtTimestamp;
    public ulong DeadlineTimestamp;
    public ulong HeartbeatGeneration;
    public NativeTrustedAdapterCallerFacts Caller;
}
