using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeAdapterInstanceLeaseSession : IDisposable
{
    private readonly object gate = new();
    private IntPtr handle;

    public NativeAdapterInstanceLeaseSession(
        uint capacity,
        ulong hostInstanceId,
        ulong leaseDurationTicks,
        ulong maximumAttestationAgeTicks)
    {
        if (capacity is 0 or > NativeAdapterInstanceLeaseAbi.HardCapacityLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (hostInstanceId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostInstanceId));
        }
        if (leaseDurationTicks == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDurationTicks));
        }
        if (maximumAttestationAgeTicks == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttestationAgeTicks));
        }
        NativeAdapterInstanceLeaseAbi.ValidateManagedLayout();
        if (NativeCoreLibrary.GetAdapterInstanceLeaseAbiVersion() != NativeAdapterInstanceLeaseAbi.Version)
        {
            throw new InvalidOperationException("Native adapter-instance lease ABI mismatch.");
        }

        var config = new NativeAdapterInstanceLeaseConfig
        {
            AbiVersion = NativeAdapterInstanceLeaseAbi.Version,
            StructSize = SizeOf<NativeAdapterInstanceLeaseConfig>(),
            Capacity = capacity,
            HostInstanceId = hostInstanceId,
            LeaseDurationTicks = leaseDurationTicks,
            MaximumAttestationAgeTicks = maximumAttestationAgeTicks
        };
        var result = (NativeCoreResultCode)NativeCoreLibrary.CreateAdapterInstanceLease(config, out handle);
        if (result != NativeCoreResultCode.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException($"Native adapter-instance lease creation failed: {result}.");
        }
    }

    public NativeAdapterInstanceLeaseReceipt Issue(
        ulong ownerApplicationKey,
        TrustedAdapterCapability capabilities,
        TrustedAdapterCallerIdentity caller)
    {
        lock (gate)
        {
            var input = new NativeAdapterInstanceLeaseIssueInput
            {
                AbiVersion = NativeAdapterInstanceLeaseAbi.Version,
                StructSize = SizeOf<NativeAdapterInstanceLeaseIssueInput>(),
                OwnerApplicationKey = ownerApplicationKey,
                Capabilities = (ulong)capabilities,
                NowTimestamp = MonotonicNow(),
                Caller = ProjectCaller(caller)
            };
            var result = (NativeCoreResultCode)NativeCoreLibrary.IssueAdapterInstanceLease(
                RequireHandle(),
                input,
                out var receipt,
                SizeOf<NativeAdapterInstanceLeaseReceipt>());
            RequireSuccess(result, "issue");
            ValidateReceipt(receipt);
            return receipt;
        }
    }

    public bool TryRenew(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCallerIdentity caller,
        out NativeAdapterInstanceLeaseReceipt receipt)
    {
        lock (gate)
        {
            var input = new NativeAdapterInstanceLeaseRenewInput
            {
                AbiVersion = NativeAdapterInstanceLeaseAbi.Version,
                StructSize = SizeOf<NativeAdapterInstanceLeaseRenewInput>(),
                LeaseIdHigh = leaseId.High,
                LeaseIdLow = leaseId.Low,
                ExpectedGeneration = expectedGeneration,
                NowTimestamp = MonotonicNow(),
                Caller = ProjectCaller(caller)
            };
            var result = (NativeCoreResultCode)NativeCoreLibrary.RenewAdapterInstanceLease(
                RequireHandle(),
                input,
                out receipt,
                SizeOf<NativeAdapterInstanceLeaseReceipt>());
            if (result is NativeCoreResultCode.InvalidArgument
                or NativeCoreResultCode.Unavailable
                or NativeCoreResultCode.StaleFrame)
            {
                receipt = default;
                return false;
            }
            RequireSuccess(result, "renew");
            ValidateReceipt(receipt);
            return true;
        }
    }

    public bool TryResolve(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCapability requiredCapability,
        out NativeAdapterInstanceLeaseReceipt receipt)
    {
        lock (gate)
        {
            var input = new NativeAdapterInstanceLeaseResolveInput
            {
                AbiVersion = NativeAdapterInstanceLeaseAbi.Version,
                StructSize = SizeOf<NativeAdapterInstanceLeaseResolveInput>(),
                LeaseIdHigh = leaseId.High,
                LeaseIdLow = leaseId.Low,
                ExpectedGeneration = expectedGeneration,
                RequiredCapabilities = (ulong)requiredCapability,
                NowTimestamp = MonotonicNow()
            };
            var result = (NativeCoreResultCode)NativeCoreLibrary.ResolveAdapterInstanceLease(
                RequireHandle(),
                input,
                out receipt,
                SizeOf<NativeAdapterInstanceLeaseReceipt>());
            if (result is NativeCoreResultCode.Unavailable or NativeCoreResultCode.StaleFrame)
            {
                receipt = default;
                return false;
            }
            RequireSuccess(result, "resolve");
            ValidateReceipt(receipt);
            return true;
        }
    }

    public bool Revoke(AdapterInstanceLeaseId leaseId, ulong expectedGeneration)
    {
        lock (gate)
        {
            var input = new NativeAdapterInstanceLeaseRevokeInput
            {
                AbiVersion = NativeAdapterInstanceLeaseAbi.Version,
                StructSize = SizeOf<NativeAdapterInstanceLeaseRevokeInput>(),
                LeaseIdHigh = leaseId.High,
                LeaseIdLow = leaseId.Low,
                ExpectedGeneration = expectedGeneration
            };
            var result = (NativeCoreResultCode)NativeCoreLibrary.RevokeAdapterInstanceLease(
                RequireHandle(),
                input);
            if (result is NativeCoreResultCode.Unavailable or NativeCoreResultCode.StaleFrame)
            {
                return false;
            }
            RequireSuccess(result, "revoke");
            return true;
        }
    }

    public int Expire()
    {
        lock (gate)
        {
            var result = (NativeCoreResultCode)NativeCoreLibrary.ExpireAdapterInstanceLeases(
                RequireHandle(),
                MonotonicNow(),
                out var expiredCount);
            RequireSuccess(result, "expire");
            return checked((int)expiredCount);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            var current = handle;
            handle = IntPtr.Zero;
            if (current != IntPtr.Zero)
            {
                NativeCoreLibrary.DestroyAdapterInstanceLease(current);
            }
        }
        GC.SuppressFinalize(this);
    }

    private static NativeTrustedAdapterCallerFacts ProjectCaller(TrustedAdapterCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Span<byte> sidDigest = stackalloc byte[32];
        Span<byte> pathDigest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(caller.WindowsSid.ToUpperInvariant()), sidDigest);
        SHA256.HashData(Encoding.UTF8.GetBytes(caller.CanonicalExecutablePath.ToUpperInvariant()), pathDigest);
        return new NativeTrustedAdapterCallerFacts
        {
            HostInstanceId = caller.HostInstanceId,
            TransportConnectionId = caller.TransportConnectionId,
            ProcessCreatedUtcTicks = caller.ProcessCreatedUtcTicks,
            AttestedAtTimestamp = caller.AttestedAtMonotonicTimestamp,
            AuthenticationIdLuid = caller.AuthenticationIdLuid,
            SidDigest0 = BinaryPrimitives.ReadUInt64LittleEndian(sidDigest),
            SidDigest1 = BinaryPrimitives.ReadUInt64LittleEndian(sidDigest[8..]),
            SidDigest2 = BinaryPrimitives.ReadUInt64LittleEndian(sidDigest[16..]),
            SidDigest3 = BinaryPrimitives.ReadUInt64LittleEndian(sidDigest[24..]),
            ImagePathDigest0 = BinaryPrimitives.ReadUInt64LittleEndian(pathDigest),
            ImagePathDigest1 = BinaryPrimitives.ReadUInt64LittleEndian(pathDigest[8..]),
            ImagePathDigest2 = BinaryPrimitives.ReadUInt64LittleEndian(pathDigest[16..]),
            ImagePathDigest3 = BinaryPrimitives.ReadUInt64LittleEndian(pathDigest[24..]),
            ExecutableFileIdHigh = caller.ExecutableFileIdentity.FileIdHigh,
            ExecutableFileIdLow = caller.ExecutableFileIdentity.FileIdLow,
            VolumeSerialNumber = caller.ExecutableFileIdentity.VolumeSerialNumber,
            ProcessId = checked((uint)caller.ProcessId),
            WindowsSessionId = checked((uint)caller.WindowsSessionId),
            IntegrityLevelRid = caller.IntegrityLevelRid,
            IsElevated = caller.IsElevated ? (byte)1 : (byte)0
        };
    }

    private static void ValidateReceipt(in NativeAdapterInstanceLeaseReceipt receipt)
    {
        if (receipt.AbiVersion != NativeAdapterInstanceLeaseAbi.Version
            || receipt.StructSize != SizeOf<NativeAdapterInstanceLeaseReceipt>()
            || (receipt.LeaseIdHigh | receipt.LeaseIdLow) == 0
            || (receipt.InstanceIdHigh | receipt.InstanceIdLow) == 0
            || receipt.HeartbeatGeneration == 0)
        {
            throw new InvalidOperationException("Native adapter-instance lease returned an invalid receipt.");
        }
    }

    private static void RequireSuccess(NativeCoreResultCode result, string operation)
    {
        if (result != NativeCoreResultCode.Ok)
        {
            throw new InvalidOperationException($"Native adapter-instance lease {operation} failed: {result}.");
        }
    }

    private IntPtr RequireHandle()
    {
        var current = Volatile.Read(ref handle);
        return current != IntPtr.Zero
            ? current
            : throw new ObjectDisposedException(nameof(NativeAdapterInstanceLeaseSession));
    }

    private static uint SizeOf<T>() where T : struct => checked((uint)Marshal.SizeOf<T>());

    public static ulong MonotonicNow()
    {
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        return timestamp > 0 ? checked((ulong)timestamp) : 1UL;
    }
}
