using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed record NativeMetricSnapshotPersistenceImage(
    NativeMetricSnapshotCatalogPersistenceState Catalog,
    NativeMetricSnapshotPersistenceHeader Header,
    NativeMetricSnapshotSourcePersistenceOutput[] Sources,
    NativeMetricSnapshotRuleStateOutput[] Rules,
    NativeMetricSnapshotGpuInventoryOutput[] GpuInventory);

internal sealed class NativeMetricSnapshotPersistenceStore(string path)
{
    private const ulong Magic = 0x32504E534D4D5248;
    private const uint EnvelopeVersion = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string path = Path.GetFullPath(path);

    internal NativeMetricSnapshotPersistenceImage? Load(
        ulong expectedConfigurationGeneration,
        string expectedManifestSha256,
        NativeMetricSnapshotCapacity capacity)
    {
        if (!IsSha256(expectedManifestSha256))
        {
            throw new ArgumentException(
                "The expected metric-snapshot manifest SHA is invalid.",
                nameof(expectedManifestSha256));
        }

        byte[] image;
        try
        {
            image = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (image.Length < SHA256.HashSizeInBytes + 64)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence envelope is truncated.");
        }
        var body = image.AsSpan(0, image.Length - SHA256.HashSizeInBytes);
        var expectedDigest = image.AsSpan(body.Length, SHA256.HashSizeInBytes);
        Span<byte> actualDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, actualDigest);
        if (!actualDigest.SequenceEqual(expectedDigest))
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence envelope checksum is invalid.");
        }

        using var stream = new MemoryStream(
            image,
            0,
            body.Length,
            writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (reader.ReadUInt64() != Magic)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence envelope contract does not match.");
        }
        var envelopeVersion = reader.ReadUInt32();
        var abiVersion = reader.ReadUInt32();
        var configurationGeneration = reader.ReadUInt64();
        if (envelopeVersion != EnvelopeVersion
            || abiVersion != NativeMetricSnapshotAbi.Version
            || configurationGeneration != expectedConfigurationGeneration)
        {
            return null;
        }

        var currentMachine = DeviceTopologyCacheIdentity.ReadCurrentDescriptor();
        var machineSource = ReadString(reader, 64);
        var machineSha256 = ReadString(reader, 128);
        if (!string.Equals(
                machineSource,
                currentMachine.Source,
                StringComparison.Ordinal)
            || !string.Equals(
                machineSha256,
                currentMachine.Sha256,
                StringComparison.Ordinal))
        {
            return null;
        }

        var manifestSha256 = ReadString(reader, 64);
        var catalogIdentitySha256 = ReadString(reader, 64);
        var catalogGeneration = reader.ReadUInt64();
        var nativeRowFingerprint = reader.ReadUInt64();
        if (!string.Equals(
                manifestSha256,
                expectedManifestSha256,
                StringComparison.Ordinal)
            || !IsSha256(catalogIdentitySha256)
            || catalogGeneration == 0
            || nativeRowFingerprint == 0)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence catalog identity does not match.");
        }
        var handleCount = reader.ReadUInt32();
        var maximumHandleCount = checked(
            capacity.MetricCapacity
            + capacity.RuleCapacity
            + capacity.MetricCapacity);
        if (handleCount > maximumHandleCount)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence handle map exceeds the compiled capacity.");
        }
        var handleEntries =
            new NativeMetricSnapshotCatalogHandleEntry[
                checked((int)handleCount)];
        for (var index = 0; index < handleEntries.Length; index++)
        {
            handleEntries[index] = new NativeMetricSnapshotCatalogHandleEntry(
                (NativeMetricSnapshotCatalogHandleKind)reader.ReadUInt32(),
                reader.ReadUInt64(),
                ReadString(
                    reader,
                    NativeMetricSnapshotCatalogHandleMap
                        .MaximumExactKeyByteCount));
        }
        var handleMap =
            new NativeMetricSnapshotCatalogHandleMap(handleEntries);
        var metricIdCount = reader.ReadUInt32();
        if (metricIdCount > capacity.MetricCapacity)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence metric-ID map exceeds the compiled capacity.");
        }
        var metricIds = ImmutableDictionary.CreateBuilder<ulong, string>();
        for (uint index = 0; index < metricIdCount; index++)
        {
            if (!metricIds.TryAdd(
                    reader.ReadUInt64(),
                    ReadString(reader, 4096)))
            {
                throw new InvalidDataException(
                    "The metric-snapshot persistence metric-ID map contains a duplicate handle.");
            }
        }

        var header = ReadStruct<NativeMetricSnapshotPersistenceHeader>(reader);
        if (header.CatalogGeneration != catalogGeneration
            || header.CatalogFingerprint != nativeRowFingerprint)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence native state and catalog identity disagree.");
        }
        if (header.SourceStateCount > capacity.PersistenceSourceCapacity
            || header.RuleStateCount > capacity.PersistenceRuleCapacity
            || header.GpuAdapterCount > capacity.PersistenceGpuCapacity)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence envelope exceeds the compiled capacity.");
        }
        var sources =
            ReadStructArray<NativeMetricSnapshotSourcePersistenceOutput>(
                reader,
                header.SourceStateCount);
        var rules = ReadStructArray<NativeMetricSnapshotRuleStateOutput>(
            reader,
            header.RuleStateCount);
        var gpuInventory =
            ReadStructArray<NativeMetricSnapshotGpuInventoryOutput>(
                reader,
                header.GpuAdapterCount);
        if (stream.Position != body.Length)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence envelope contains trailing data.");
        }
        return new NativeMetricSnapshotPersistenceImage(
            new NativeMetricSnapshotCatalogPersistenceState(
                catalogGeneration,
                nativeRowFingerprint,
                catalogIdentitySha256,
                manifestSha256,
                handleMap,
                metricIds.ToImmutable()),
            header,
            sources,
            rules,
            gpuInventory);
    }

    internal void Save(
        ulong configurationGeneration,
        NativeMetricSnapshotPersistenceImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var machine = DeviceTopologyCacheIdentity.ReadCurrentDescriptor();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   StrictUtf8,
                   leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(EnvelopeVersion);
            writer.Write(NativeMetricSnapshotAbi.Version);
            writer.Write(configurationGeneration);
            WriteString(writer, machine.Source);
            WriteString(writer, machine.Sha256);
            WriteString(writer, source.Catalog.ManifestSha256);
            WriteString(writer, source.Catalog.CatalogIdentitySha256);
            writer.Write(source.Catalog.CatalogGeneration);
            writer.Write(source.Catalog.NativeRowFingerprint);
            writer.Write(checked(
                (uint)source.Catalog.HandleMap.Entries.Length));
            foreach (var entry in source.Catalog.HandleMap.Entries)
            {
                writer.Write((uint)entry.Kind);
                writer.Write(entry.Handle);
                WriteString(writer, entry.ExactKey);
            }
            var metricIds = source.Catalog.MetricIds
                .OrderBy(static pair => pair.Key)
                .ToArray();
            writer.Write(checked((uint)metricIds.Length));
            foreach (var pair in metricIds)
            {
                writer.Write(pair.Key);
                WriteString(writer, pair.Value);
            }
            WriteStruct(writer, source.Header);
            WriteStructArray(writer, source.Sources);
            WriteStructArray(writer, source.Rules);
            WriteStructArray(writer, source.GpuInventory);
        }
        var body = stream.ToArray();
        var image = new byte[checked(body.Length + SHA256.HashSizeInBytes)];
        body.CopyTo(image, 0);
        SHA256.HashData(body).CopyTo(image, body.Length);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "The metric-snapshot persistence path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
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
            WindowsNativeAtomicFileCommitter.CommitReplace(
                temporaryPath,
                path);
            var committed = File.ReadAllBytes(path);
            if (!committed.AsSpan().SequenceEqual(image))
            {
                throw new IOException(
                    "The committed metric-snapshot persistence envelope differs from its canonical image.");
            }
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    internal void Discard()
        => _ = WindowsNativeAtomicFileCommitter.DeleteExact(path);

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));

    private static string ReadString(BinaryReader reader, int maximumByteCount)
    {
        var count = reader.ReadUInt32();
        if (count > maximumByteCount)
        {
            throw new InvalidDataException(
                "The metric-snapshot persistence text exceeds its configured bound.");
        }
        var bytes = reader.ReadBytes(checked((int)count));
        if (bytes.Length != count)
        {
            throw new EndOfStreamException();
        }
        return StrictUtf8.GetString(bytes);
    }

    private static T ReadStruct<T>(BinaryReader reader)
        where T : unmanaged
    {
        var size = checked((uint)Unsafe.SizeOf<T>());
        var bytes = reader.ReadBytes(checked((int)size));
        if (bytes.Length != size)
        {
            throw new EndOfStreamException();
        }
        return MemoryMarshal.Read<T>(bytes);
    }

    private static T[] ReadStructArray<T>(BinaryReader reader, uint count)
        where T : unmanaged
    {
        var result = new T[checked((int)count)];
        var byteCount = checked(count * (uint)Unsafe.SizeOf<T>());
        var bytes = reader.ReadBytes(checked((int)byteCount));
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException();
        }
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

    private static void WriteStructArray<T>(
        BinaryWriter writer,
        T[] values)
        where T : unmanaged
        => writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
}
