using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal sealed class HostManagerOperationCanonicalStore : IDisposable
{
    private readonly string envelopePath;
    private readonly HostManagerDurableRootManifest rootManifest;
    private readonly string leasePath;
    private CompiledHostManagerOperationCoordinatorCapacityPlan capacity;
    private ulong maximumConfigurationGeneration;
    private FileStream? ownerLease;
    private bool disposed;

    internal HostManagerOperationCanonicalStore(
        string envelopePath,
        CompiledHostManagerOperationCoordinatorCapacityPlan capacity,
        ulong maximumConfigurationGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopePath);
        ArgumentNullException.ThrowIfNull(capacity);
        if (!capacity.IsPublished || maximumConfigurationGeneration == 0)
        {
            throw new ArgumentException(
                "The operation canonical store configuration is not published.");
        }
        this.envelopePath = Path.GetFullPath(envelopePath);
        rootManifest = new HostManagerDurableRootManifest(
            this.envelopePath,
            "operation-coordinator");
        leasePath = this.envelopePath + ".lock";
        this.capacity = capacity;
        this.maximumConfigurationGeneration = maximumConfigurationGeneration;
    }

    internal void AcquireOwnerLease()
    {
        ThrowIfDisposed();
        if (ownerLease is not null)
        {
            throw new InvalidOperationException(
                "The operation coordinator owner lease is already held.");
        }
        var directory = Path.GetDirectoryName(envelopePath)
            ?? throw new InvalidOperationException(
                "The operation canonical envelope path has no parent directory.");
        Directory.CreateDirectory(directory);
        try
        {
            ownerLease = new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another operation coordinator owner already holds the canonical store lease.",
                error);
        }
    }

    internal HostManagerOperationCanonicalEnvelope? LoadValidated()
    {
        RequireOwnerLease();
        byte[] image;
        try
        {
            image = File.ReadAllBytes(envelopePath);
        }
        catch (FileNotFoundException)
        {
            rootManifest.RequireVirginOrInitializing();
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            rootManifest.RequireVirginOrInitializing();
            return null;
        }
        var envelope = HostManagerOperationCanonicalEnvelopeCodec.Decode(
            image,
            capacity,
            maximumConfigurationGeneration);
        rootManifest.ValidateOrAdoptCanonical(image);
        return envelope;
    }

    internal HostManagerOperationCanonicalEnvelope Commit(
        HostManagerOperationCanonicalEnvelope candidate,
        ReadOnlySpan<byte> expectedPriorEnvelopeSha256,
        CompiledHostManagerOperationCoordinatorCapacityPlan candidateCapacity,
        ulong candidateMaximumConfigurationGeneration)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidateCapacity);
        RequireOwnerLease();
        if (!candidateCapacity.IsPublished
            || candidateMaximumConfigurationGeneration
                < maximumConfigurationGeneration
            || candidate.ConfigurationGeneration
                > candidateMaximumConfigurationGeneration
            || expectedPriorEnvelopeSha256.Length != SHA256.HashSizeInBytes
            || !CryptographicOperations.FixedTimeEquals(
                candidate.PriorEnvelopeSha256,
                expectedPriorEnvelopeSha256))
        {
            throw new InvalidDataException(
                "The candidate operation envelope does not bind the expected prior image.");
        }
        VerifyDestinationIdentity(expectedPriorEnvelopeSha256);
        var image = HostManagerOperationCanonicalEnvelopeCodec.Encode(
            candidate,
            candidateCapacity);
        var directory = Path.GetDirectoryName(envelopePath)
            ?? throw new InvalidOperationException(
                "The operation canonical envelope path has no parent directory.");
        Directory.CreateDirectory(directory);
        rootManifest.EnsureInitializing();
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(envelopePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                file.Write(image);
                file.Flush(flushToDisk: true);
            }
            var transition = rootManifest.BeginCanonicalTransition(image);
            WindowsNativeAtomicFileCommitter.CommitReplace(
                temporaryPath,
                envelopePath);
            var readback = File.ReadAllBytes(envelopePath);
            if (!readback.AsSpan().SequenceEqual(image))
            {
                throw new IOException(
                    "The committed operation envelope differs from its canonical image.");
            }
            var committed = HostManagerOperationCanonicalEnvelopeCodec.Decode(
                readback,
                candidateCapacity,
                candidateMaximumConfigurationGeneration);
            rootManifest.FinalizeCanonicalTransition(transition);
            capacity = candidateCapacity;
            maximumConfigurationGeneration =
                candidateMaximumConfigurationGeneration;
            return committed;
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ownerLease?.Dispose();
        ownerLease = null;
    }

    private void VerifyDestinationIdentity(
        ReadOnlySpan<byte> expectedPriorEnvelopeSha256)
    {
        byte[] current;
        try
        {
            current = File.ReadAllBytes(envelopePath);
        }
        catch (FileNotFoundException)
        {
            if (!AllZero(expectedPriorEnvelopeSha256))
            {
                throw new IOException(
                    "The expected prior operation envelope is absent.");
            }
            return;
        }
        catch (DirectoryNotFoundException)
        {
            if (!AllZero(expectedPriorEnvelopeSha256))
            {
                throw new IOException(
                    "The expected prior operation envelope directory is absent.");
            }
            return;
        }
        if (current.Length < SHA256.HashSizeInBytes)
        {
            throw new IOException(
                "The operation envelope changed outside the active owner.");
        }
        var actual = current.AsSpan()[
            checked(current.Length - SHA256.HashSizeInBytes)..];
        if (!CryptographicOperations.FixedTimeEquals(
                actual,
                expectedPriorEnvelopeSha256))
        {
            throw new IOException(
                "The operation envelope changed outside the active owner.");
        }
    }

    private void RequireOwnerLease()
    {
        ThrowIfDisposed();
        if (ownerLease is null)
        {
            throw new InvalidOperationException(
                "The operation canonical store owner lease is not held.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private static bool AllZero(ReadOnlySpan<byte> source)
    {
        foreach (var value in source)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}
