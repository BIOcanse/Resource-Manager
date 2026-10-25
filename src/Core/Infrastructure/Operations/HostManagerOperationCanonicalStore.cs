using System.Security.Cryptography;
using System.Buffers.Binary;
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
    private readonly ILogger? logger;
    private byte[]? originalBankSha256;
    private byte[]? originalManifestSha256;
    private bool bankIdentityCaptured;
    private bool manifestIdentityCaptured;
    private bool pendingLegacyMigration;
    private bool canonicalTransitionStarted;
    internal bool HasUnresolvedLoad { get; private set; }

    internal HostManagerOperationCanonicalStore(
        string envelopePath,
        CompiledHostManagerOperationCoordinatorCapacityPlan capacity,
        ulong maximumConfigurationGeneration,
        ILogger? logger = null)
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
        this.logger = logger;
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
        try
        {
            byte[] image;
            using (var file = WindowsNativeAtomicFileCommitter.OpenCommittedRead(envelopePath))
            {
                if (file.Length < 352 || (ulong)file.Length > capacity.MaximumEnvelopeByteCount || file.Length > int.MaxValue)
                    throw new InvalidDataException("The operation canonical file length is outside its explicit capacity.");
                image = new byte[(int)file.Length];
                file.ReadExactly(image);
            }
            originalBankSha256 = SHA256.HashData(image);
            bankIdentityCaptured = true;
            CaptureManifestIdentity();
            var legacy = image.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8)) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16)) == 0x0001_0000;
            var envelope = legacy
                ? HostManagerOperationCanonicalEnvelopeCodec.DecodeLegacyV1(image, capacity, maximumConfigurationGeneration)
                : HostManagerOperationCanonicalEnvelopeCodec.Decode(image, capacity, maximumConfigurationGeneration);
            if (legacy)
            {
                if (originalManifestSha256 is not null) _ = HostManagerDurableRootManifest.ValidateForTransfer(envelopePath);
                pendingLegacyMigration = true;
                var converted = HostManagerOperationLegacyMigration.Convert(envelope);
                _ = HostManagerOperationCanonicalEnvelopeCodec.Encode(converted, capacity);
                // The initial owner commit still binds the original durable image until migration has been atomically accepted.
                return converted with { FullEnvelopeSha256 = envelope.FullEnvelopeSha256.ToArray() };
            }
            if (originalManifestSha256 is not null) _ = HostManagerDurableRootManifest.ValidateForTransfer(envelopePath);
            return envelope;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (bankIdentityCaptured || File.Exists(envelopePath))
            {
                RejectLoadedImage(error);
                return null;
            }
            try
            {
                rootManifest.RequireVirginOrInitializing();
                return null;
            }
            catch (Exception manifestError) when (IsUnreadableSource(manifestError))
            {
                RejectLoadedImage(manifestError);
                return null;
            }
        }
        catch (Exception error) when (IsUnreadableSource(error))
        {
            RejectLoadedImage(error);
            return null;
        }
    }

    private static bool IsUnreadableSource(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException;

    private void CaptureManifestIdentity()
    {
        var path = envelopePath + ".root.manifest";
        originalManifestSha256 = File.Exists(path) ? HashFile(path) : null;
        manifestIdentityCaptured = true;
    }

    internal void RejectLoadedImage(Exception error)
    {
        HasUnresolvedLoad = true;
        pendingLegacyMigration = false;
        logger?.LogError(error,
            "Operation bank {Path} could not be converted or validated. Original bank and manifest remain untouched; current operation state is empty. The first explicit new operation will preserve both originals before resetting this owner.", envelopePath);
    }

    internal bool TryDeferUncommittedLegacyMigration(Exception error)
    {
        if (!pendingLegacyMigration || canonicalTransitionStarted || error is not (IOException or UnauthorizedAccessException)) return false;
        try
        {
            if (originalBankSha256 is null || !HashFile(envelopePath).AsSpan().SequenceEqual(originalBankSha256)) return false;
            var manifest = envelopePath + ".root.manifest";
            if (originalManifestSha256 is null ? File.Exists(manifest)
                : !File.Exists(manifest) || !HashFile(manifest).AsSpan().SequenceEqual(originalManifestSha256)) return false;
        }
        catch (Exception readError) when (readError is IOException or UnauthorizedAccessException) { return false; }
        RejectLoadedImage(error);
        return true;
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
        // Startup/planner publications of the documented empty state are reads, not permission to replace unresolved recovery evidence.
        if (HasUnresolvedLoad && candidate.NativeImage.Records.Length == 0)
            return candidate with { FullEnvelopeSha256 = HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest() };
        var image = HostManagerOperationCanonicalEnvelopeCodec.Encode(candidate, candidateCapacity);
        if (HasUnresolvedLoad || pendingLegacyMigration)
        {
            if (bankIdentityCaptured && (originalBankSha256 is null ? File.Exists(envelopePath)
                : !File.Exists(envelopePath) || !HashFile(envelopePath).AsSpan().SequenceEqual(originalBankSha256)))
                throw new IOException("The preserved operation bank changed outside the active owner.");
            var backup = PreserveOriginal(HasUnresolvedLoad ? "unconverted" : "legacy-v1");
            if (HasUnresolvedLoad)
                rootManifest.InitializeAfterPreservedReset(backup.Bank, backup.Manifest);
            else rootManifest.ValidateOrAdoptCanonical(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(envelopePath));
            logger?.LogWarning("Operation bank {Path} {Action}; byte-exact original backup: {Backup}. Recovery evidence is retained there.",
                envelopePath, HasUnresolvedLoad ? "is reset for an explicit new operation after an unresolved load" : "is migrated from envelope-v1/ABI1 to facts-only envelope-v2/ABI2", backup.Bank ?? backup.Manifest ?? "(no source files)");
        }
        else if (originalBankSha256 is not null)
            rootManifest.ValidateOrAdoptCanonical(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(envelopePath));
        if (!HasUnresolvedLoad) VerifyDestinationIdentity(expectedPriorEnvelopeSha256);
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
            canonicalTransitionStarted = true;
            var transition = rootManifest.BeginCanonicalTransition(image);
            WindowsNativeAtomicFileCommitter.CommitReplace(
                temporaryPath,
                envelopePath);
            var readback = WindowsNativeAtomicFileCommitter.ReadCommittedBytes(envelopePath);
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
            HasUnresolvedLoad = false;
            pendingLegacyMigration = false;
            canonicalTransitionStarted = false;
            originalBankSha256 = null;
            originalManifestSha256 = null;
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

    private (string? Bank, string? Manifest) PreserveOriginal(string reason)
    {
        if (!bankIdentityCaptured)
        {
            originalBankSha256 = File.Exists(envelopePath) ? HashFile(envelopePath) : null;
            bankIdentityCaptured = true;
        }
        if (!manifestIdentityCaptured) CaptureManifestIdentity();
        var source = originalBankSha256;
        var manifestSource = envelopePath + ".root.manifest";
        if (originalManifestSha256 is null ? File.Exists(manifestSource)
            : !File.Exists(manifestSource) || !HashFile(manifestSource).AsSpan().SequenceEqual(originalManifestSha256))
            throw new IOException("The original operation root manifest changed outside the active owner.");
        var stem = envelopePath + "." + reason + ".byte-exact." + Convert.ToHexString(source ?? originalManifestSha256 ?? SHA256.HashData([])).ToLowerInvariant() + ".backup";
        var bank = stem;
        var collisions = 0;
        while ((source is not null && File.Exists(bank) && !HashFile(bank).AsSpan().SequenceEqual(source))
            || (originalManifestSha256 is not null && File.Exists(bank + ".root.manifest")
                && !HashFile(bank + ".root.manifest").AsSpan().SequenceEqual(originalManifestSha256)))
        {
            if (++collisions > 32) throw new IOException("No unique operation bank backup path could be reserved.");
            bank = stem[..^7] + "." + Guid.NewGuid().ToString("N") + ".backup";
        }
        if (source is not null) WritePreserved(envelopePath, bank, source);
        var manifest = originalManifestSha256 is null ? null : bank + ".root.manifest";
        if (manifest is not null) WritePreserved(manifestSource, manifest, originalManifestSha256!);
        return (source is null ? null : bank, manifest);
    }

    private static void WritePreserved(string sourcePath, string path, byte[] expectedSha256)
    {
        if (!File.Exists(path))
        {
            File.Copy(sourcePath, path, overwrite: false);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            file.Flush(true);
        }
        if (!HashFile(path).AsSpan().SequenceEqual(expectedSha256))
            throw new IOException("The preserved operation bank backup could not be verified.");
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
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
            current = WindowsNativeAtomicFileCommitter.ReadCommittedBytes(envelopePath);
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
