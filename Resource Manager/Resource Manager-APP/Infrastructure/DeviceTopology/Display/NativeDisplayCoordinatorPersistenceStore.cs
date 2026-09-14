using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal sealed record NativeDisplayCoordinatorPersistenceImage(
    NativeDisplayPersistenceHeader Header,
    NativeDisplayFact[] Facts,
    NativeDisplayTextInput[] Texts,
    byte[] TextBytes,
    IReadOnlyList<NativeDisplayPayloadEntry> Payloads);

internal sealed class NativeDisplayCoordinatorPersistenceStore(string path)
{
    private const ulong Magic = 0x33445349444D5248;
    private const uint EnvelopeVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string path = Path.GetFullPath(path);

    internal NativeDisplayCoordinatorPersistenceImage? Load(
        ulong expectedConfigurationGeneration)
    {
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

    internal void Save(
        ulong configurationGeneration,
        NativeDisplayCoordinatorPersistenceImage source)
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

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "The display-coordinator persistence path has no parent directory.");
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
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
            var committed = File.ReadAllBytes(path);
            if (!committed.AsSpan().SequenceEqual(image))
            {
                throw new IOException(
                    "The committed display-coordinator persistence envelope differs from its canonical image.");
            }
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
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
