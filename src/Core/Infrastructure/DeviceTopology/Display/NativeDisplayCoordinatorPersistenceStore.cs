using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Disk.ReadWrite;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal enum DisplayCoordinatorCheckpointStage : byte
{
    BeforeWrite,
    BeforeRegister,
    Registered,
    Closing
}

// Host checkpointGate serializes these operations with publication permission.
internal sealed class NativeDisplayCoordinatorPersistenceStore : IDisplayCoordinatorCheckpointStore
{
    private const ulong Magic = 0x33445349444D5248;
    private const uint EnvelopeVersion = 1;
    private const string SlotA = "checkpoint-a.bin";
    private const string SlotB = "checkpoint-b.bin";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> Uid => "rm/display/checkpoint/v3"u8;
    private readonly string legacyPath;
    private readonly string nativeManifestPath;
    private readonly string rootPath;
    private readonly Action<DisplayCoordinatorCheckpointStage, DiskStore>? observer;
    private DiskStore? native;
    private bool requiresRecovery;
    private bool allowLegacy = true;
    private bool disposed;

    internal NativeDisplayCoordinatorPersistenceStore(string legacyPath)
        : this(legacyPath, Path.Combine(AppContext.BaseDirectory, "Native", "DiskCheckpoint", "native.json")) { }

    internal NativeDisplayCoordinatorPersistenceStore(string legacyPath, string nativeManifestPath)
        : this(legacyPath, nativeManifestPath, null) { }

    // A synchronous operation observer is only supplied by deterministic product tests.
    internal NativeDisplayCoordinatorPersistenceStore(string legacyPath, string nativeManifestPath,
        Action<DisplayCoordinatorCheckpointStage, DiskStore>? observer)
    {
        this.legacyPath = Path.GetFullPath(legacyPath);
        this.nativeManifestPath = Path.GetFullPath(nativeManifestPath);
        this.observer = observer;
        rootPath = ResolveRootPath(this.legacyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(rootPath)!);
        // Keep this the last fallible constructor action: Open owns its failure cleanup.
        native = DiskStore.Open(this.nativeManifestPath, rootPath);
    }

    internal static string ResolveRootPath(string legacyPath) => Path.GetFullPath(legacyPath) + ".disk";

    public NativeDisplayCoordinatorPersistenceImage? Load(ulong expectedConfigurationGeneration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            var disk = Ready();
            var query = QueryBinding(disk);
            if (query.Result.Kind == DiskResultKind.Missing)
            {
                if (!allowLegacy) return null;
                byte[] legacy;
                try { legacy = File.ReadAllBytes(legacyPath); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                return Decode(legacy, expectedConfigurationGeneration);
            }
            allowLegacy = false;
            return Decode(ReadBinding(disk, query), expectedConfigurationGeneration);
        }
        catch (Exception error) when (IsUncertain(error))
        {
            Freeze();
            throw;
        }
    }

    public void Save(ulong configurationGeneration, NativeDisplayCoordinatorPersistenceImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(disposed, this);
        ulong? appliedSequence = null;
        try
        {
            var disk = Ready();
            var query = QueryBinding(disk);
            var sequence = query.Result.Kind == DiskResultKind.Missing ? 0 : query.Result.Sequence;
            var nextSlot = query.Result.Kind == DiskResultKind.Missing || SlotIndex(query.Segments[0]) == 1 ? 0U : 1U;
            var image = Encode(configurationGeneration, source);
            var path = disk.GetPath(nextSlot);
            var directory = Path.GetDirectoryName(path)!;
            var temporaryPath = Path.Combine(directory,
                $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            Exception? operationError = null;
            try
            {
                observer?.Invoke(DisplayCoordinatorCheckpointStage.BeforeWrite, disk);
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    file.Write(image);
                    file.Flush(flushToDisk: true);
                }
                WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
                disk.SyncDataDirectory();
                observer?.Invoke(DisplayCoordinatorCheckpointStage.BeforeRegister, disk);
                var result = disk.Register(Uid, nextSlot, replace: true, expectedSequence: sequence);
                if (result.Kind == DiskResultKind.Conflict)
                    throw new IOException($"The display checkpoint registration conflicted: expected sequence {sequence}, actual {result.Sequence}.");
                if (result.Kind != DiskResultKind.Applied)
                    throw new InvalidDataException($"Unexpected display checkpoint registration result: {result.Kind}.");
                appliedSequence = result.Sequence;
                allowLegacy = false;
                observer?.Invoke(DisplayCoordinatorCheckpointStage.Registered, disk);
                EnsureWholeFileLength(disk, nextSlot, (ulong)image.Length);
                var committed = new byte[image.Length];
                var read = disk.ReadUidWhole(Uid, committed);
                EnsureWholeFileLength(disk, nextSlot, (ulong)image.Length);
                if (read.Kind != DiskResultKind.Data || read.Sequence != result.Sequence
                    || read.Value != (ulong)image.Length || !committed.AsSpan().SequenceEqual(image))
                    throw new IOException("The committed display checkpoint differs from its canonical image or registered sequence.");
            }
            catch (Exception error)
            {
                operationError = error;
                throw;
            }
            finally
            {
                try { _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
                catch (Exception cleanup) when (operationError is not null)
                {
                    throw new AggregateException("Checkpoint operation and temporary-file cleanup failed.", operationError, cleanup);
                }
            }
        }
        catch (Exception error)
        {
            var primary = PrimaryError(error);
            if (primary is DiskSinkException { NativeResult.Kind: DiskResultKind.Applied } sinkError)
                appliedSequence = sinkError.NativeResult.Sequence;
            if (appliedSequence is { } sequence)
            {
                Freeze();
                throw new IOException($"The display checkpoint registration was applied at sequence {sequence}, but post-commit verification or cleanup did not complete; the image must not be replayed.", error);
            }
            if (IsUncertain(error)) Freeze();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        CloseNative();
        disposed = true;
    }

    private DiskStore Ready()
    {
        if (requiresRecovery)
        {
            // No original Save is retried: a later real operation queries recovered authority.
            CloseNative();
            native = DiskStore.Open(nativeManifestPath, rootPath);
            _ = QueryBinding(native);
            requiresRecovery = false;
        }
        return native ?? throw new InvalidOperationException("The display checkpoint store is not open.");
    }

    private void CloseNative()
    {
        if (native is not { } disk) return;
        observer?.Invoke(DisplayCoordinatorCheckpointStage.Closing, disk);
        disk.Dispose();
        native = null;
    }

    private void Freeze()
    {
        requiresRecovery = true;
        allowLegacy = false;
    }

    private static Exception PrimaryError(Exception error)
        => error is AggregateException aggregate ? aggregate.InnerExceptions[0] : error;

    private static bool IsUncertain(Exception error) => PrimaryError(error) is DiskNativeException
        { ErrorName: "CatalogEffectUncertain" or "CatalogStopped" };

    private static DiskQuery QueryBinding(DiskStore disk)
    {
        var query = disk.QueryUid(Uid);
        if (query.Result.Kind == DiskResultKind.Missing) return query;
        if (query.Result.Kind != DiskResultKind.Item || query.Result.Sequence == 0
            || !query.Uid.AsSpan().SequenceEqual(Uid) || query.Segments.Count != 1)
            throw new InvalidDataException("The display checkpoint UID binding is invalid.");
        _ = SlotIndex(query.Segments[0]);
        return query;
    }

    private static uint SlotIndex(DiskSegment segment)
    {
        if (segment.Offset == 0)
        {
            if (segment.Path == SlotA && segment.Template == SlotA) return 0;
            if (segment.Path == SlotB && segment.Template == SlotB) return 1;
        }
        throw new InvalidDataException("The display checkpoint must bind one complete known slot.");
    }

    private static byte[] ReadBinding(DiskStore disk, DiskQuery query)
    {
        var length = query.Segments[0].Length;
        if (length > (ulong)Array.MaxLength)
            throw new InvalidDataException("The complete display checkpoint exceeds the managed array address space.");
        var slot = SlotIndex(query.Segments[0]);
        EnsureWholeFileLength(disk, slot, length);
        var image = new byte[checked((int)length)];
        var read = disk.ReadUidWhole(Uid, image);
        EnsureWholeFileLength(disk, slot, length);
        if (read.Kind != DiskResultKind.Data || read.Value != length || read.Sequence != query.Result.Sequence)
            throw new InvalidDataException($"The display checkpoint failed its registered read: {read.Kind}, detail {read.Detail}.");
        return image;
    }

    private static void EnsureWholeFileLength(DiskStore disk, uint slot, ulong expectedLength)
    {
        long length;
        try { length = new FileInfo(disk.GetPath(slot)).Length; }
        catch (FileNotFoundException error)
        { throw new InvalidDataException("The registered display checkpoint file is missing.", error); }
        catch (DirectoryNotFoundException error)
        { throw new InvalidDataException("The registered display checkpoint directory is missing.", error); }
        if ((ulong)length != expectedLength)
            throw new InvalidDataException("The display checkpoint file length differs from its complete registered segment.");
    }

    private static NativeDisplayCoordinatorPersistenceImage? Decode(byte[] image, ulong expectedConfigurationGeneration)
    {
        if (image.Length < SHA256.HashSizeInBytes + 64)
        {
            throw new InvalidDataException(
                "The display-coordinator persistence envelope is truncated.");
        }
        var body = image.AsSpan(0, image.Length - SHA256.HashSizeInBytes);
        var expectedDigest = image.AsSpan(body.Length, SHA256.HashSizeInBytes);
        Span<byte> actualDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, actualDigest);
        if (!actualDigest.SequenceEqual(expectedDigest))
        {
            throw new InvalidDataException(
                "The display-coordinator persistence envelope checksum is invalid.");
        }

        using var stream = new MemoryStream(image, 0, body.Length, writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (reader.ReadUInt64() != Magic)
        {
            throw new InvalidDataException(
                "The display-coordinator persistence envelope contract does not match.");
        }
        var envelopeVersion = reader.ReadUInt32();
        var abiVersion = reader.ReadUInt32();
        var configurationGeneration = reader.ReadUInt64();
        if (envelopeVersion != EnvelopeVersion
            || abiVersion != NativeDisplayCoordinatorAbi.Version
            || configurationGeneration != expectedConfigurationGeneration)
        {
            return null;
        }

        var currentMachine = DeviceTopologyCacheIdentity.ReadCurrentDescriptor();
        var machineSource = ReadString(reader, 64);
        var machineSha256 = ReadString(reader, 128);
        if (!string.Equals(machineSource, currentMachine.Source, StringComparison.Ordinal)
            || !string.Equals(machineSha256, currentMachine.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The display-coordinator persistence envelope belongs to another machine identity.");
        }

        var header = ReadStruct<NativeDisplayPersistenceHeader>(reader);
        var facts = ReadStructArray<NativeDisplayFact>(reader, header.FactCount);
        var texts = ReadStructArray<NativeDisplayTextInput>(reader, header.TextCount);
        var textBytes = ReadBytes(reader, header.TextByteCount);
        var payloadCount = reader.ReadUInt32();
        var payloads = new NativeDisplayPayloadEntry[checked((int)payloadCount)];
        for (var index = 0; index < payloads.Length; index++)
        {
            var handle = new NativeDisplayHandle128
            {
                High = reader.ReadUInt64(),
                Low = reader.ReadUInt64()
            };
            var kind = (NativeDisplayPayloadKind)reader.ReadUInt32();
            var payload = ReadBytes(reader, reader.ReadUInt32());
            payloads[index] = new NativeDisplayPayloadEntry(handle, kind, payload);
        }
        if (stream.Position != body.Length)
        {
            throw new InvalidDataException(
                "The display-coordinator persistence envelope contains trailing data.");
        }
        return new NativeDisplayCoordinatorPersistenceImage(
            header,
            facts,
            texts,
            textBytes,
            payloads);
    }

    private static byte[] Encode(ulong configurationGeneration, NativeDisplayCoordinatorPersistenceImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var machine = DeviceTopologyCacheIdentity.ReadCurrentDescriptor();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(EnvelopeVersion);
            writer.Write(NativeDisplayCoordinatorAbi.Version);
            writer.Write(configurationGeneration);
            WriteString(writer, machine.Source);
            WriteString(writer, machine.Sha256);
            WriteStruct(writer, source.Header);
            WriteStructArray(writer, source.Facts);
            WriteStructArray(writer, source.Texts);
            writer.Write(source.TextBytes);
            writer.Write(checked((uint)source.Payloads.Count));
            foreach (var payload in source.Payloads)
            {
                writer.Write(payload.Handle.High);
                writer.Write(payload.Handle.Low);
                writer.Write((uint)payload.Kind);
                writer.Write(checked((uint)payload.Payload.Length));
                writer.Write(payload.Payload);
            }
        }
        var body = stream.ToArray();
        var image = new byte[checked(body.Length + SHA256.HashSizeInBytes)];
        body.CopyTo(image, 0);
        SHA256.HashData(body).CopyTo(image, body.Length);

        return image;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maximumByteCount)
    {
        var bytes = ReadBytes(reader, reader.ReadUInt32(), maximumByteCount);
        return StrictUtf8.GetString(bytes);
    }

    private static byte[] ReadBytes(
        BinaryReader reader,
        uint byteCount,
        int maximumByteCount = int.MaxValue)
    {
        if (byteCount > maximumByteCount)
        {
            throw new InvalidDataException(
                "The display-coordinator persistence field exceeds its configured bound.");
        }
        var result = reader.ReadBytes(checked((int)byteCount));
        if (result.Length != byteCount)
        {
            throw new EndOfStreamException();
        }
        return result;
    }

    private static T ReadStruct<T>(BinaryReader reader) where T : unmanaged
    {
        var bytes = ReadBytes(reader, checked((uint)Unsafe.SizeOf<T>()));
        return MemoryMarshal.Read<T>(bytes);
    }

    private static T[] ReadStructArray<T>(BinaryReader reader, uint count)
        where T : unmanaged
    {
        var result = new T[checked((int)count)];
        var bytes = ReadBytes(
            reader,
            checked(count * (uint)Unsafe.SizeOf<T>()));
        MemoryMarshal.Cast<byte, T>(bytes).CopyTo(result);
        return result;
    }

    private static void WriteStruct<T>(BinaryWriter writer, T value)
        where T : unmanaged
    {
        Span<T> source = stackalloc T[1];
        source[0] = value;
        writer.Write(MemoryMarshal.AsBytes(source));
    }

    private static void WriteStructArray<T>(BinaryWriter writer, T[] values)
        where T : unmanaged
        => writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
}


