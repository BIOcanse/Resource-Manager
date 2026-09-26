using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

internal sealed class NativePublicServiceModelProjection
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly Dictionary<string, ulong> modelHandles =
        new(StringComparer.Ordinal);
    private NativePublicServiceModelPayloadSnapshot current =
        NativePublicServiceModelPayloadSnapshot.Empty;
    private ulong nextModelHandle;

    internal NativePublicServiceModelProjectionBatch Project(
        IReadOnlyList<AiModelDescriptor> descriptors,
        ulong providerHandle)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (providerHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(providerHandle));
        }

        var text = new ArrayBufferWriter<byte>();
        var models = new NativePublicServiceModelInput[descriptors.Count];
        var aliases = new List<NativePublicServiceModelAliasInput>();
        var payloads = new Dictionary<ulong, AiModelDescriptor>();
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index]
                ?? throw new InvalidDataException(
                    $"AI model catalog row {index} is null.");
            if (string.IsNullOrEmpty(descriptor.Key))
            {
                throw new InvalidDataException(
                    $"AI model catalog row {index} has no provider key.");
            }

            var handle = GetOrCreateModelHandle(descriptor.Key);
            if (!payloads.TryAdd(handle, descriptor))
            {
                throw new InvalidDataException(
                    $"AI model catalog contains duplicate provider key '{descriptor.Key}'.");
            }
            var flags = NativePublicServiceModelFlags.Available;
            if (descriptor.LoadedInstances.Count != 0)
            {
                flags |= NativePublicServiceModelFlags.LoadedInstance;
            }
            models[index] = new NativePublicServiceModelInput
            {
                StructSize = SizeOf<NativePublicServiceModelInput>(),
                Flags = (uint)flags,
                ModelHandle = handle,
                ProviderHandle = providerHandle,
                PayloadHandle = handle
            };
            aliases.Add(CreateAlias(handle, descriptor.Key, text));
            foreach (var instance in descriptor.LoadedInstances)
            {
                if (string.IsNullOrEmpty(instance.InstanceId))
                {
                    throw new InvalidDataException(
                        $"AI model '{descriptor.Key}' contains an empty loaded instance identity.");
                }
                aliases.Add(CreateAlias(handle, instance.InstanceId, text));
            }
        }

        return new NativePublicServiceModelProjectionBatch(
            models,
            aliases.ToArray(),
            text.WrittenMemory.ToArray(),
            new NativePublicServiceModelPayloadSnapshot(
                descriptors.ToArray(),
                new ReadOnlyDictionary<ulong, AiModelDescriptor>(payloads)));
    }

    internal void Commit(NativePublicServiceModelPayloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref current, snapshot);
    }

    internal NativePublicServiceModelPayloadSnapshot Capture()
        => Volatile.Read(ref current);

    private ulong GetOrCreateModelHandle(string providerKey)
    {
        if (modelHandles.TryGetValue(providerKey, out var handle))
        {
            return handle;
        }
        if (nextModelHandle == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The public-service model handle space is exhausted.");
        }
        handle = ++nextModelHandle;
        modelHandles.Add(providerKey, handle);
        return handle;
    }

    private static NativePublicServiceModelAliasInput CreateAlias(
        ulong modelHandle,
        string value,
        ArrayBufferWriter<byte> text)
    {
        var offset = text.WrittenCount;
        var byteCount = StrictUtf8.GetByteCount(value);
        var destination = text.GetSpan(byteCount);
        var written = StrictUtf8.GetBytes(value, destination);
        text.Advance(written);
        return new NativePublicServiceModelAliasInput
        {
            StructSize = SizeOf<NativePublicServiceModelAliasInput>(),
            ModelHandle = modelHandle,
            Alias = new NativePublicServiceTextSpan
            {
                Offset = checked((uint)offset),
                Length = checked((uint)written)
            }
        };
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
}

internal sealed record NativePublicServiceModelPayloadSnapshot(
    IReadOnlyList<AiModelDescriptor> Descriptors,
    IReadOnlyDictionary<ulong, AiModelDescriptor> ByPayloadHandle)
{
    internal static NativePublicServiceModelPayloadSnapshot Empty { get; } =
        new([], new ReadOnlyDictionary<ulong, AiModelDescriptor>(
            new Dictionary<ulong, AiModelDescriptor>()));
}

internal sealed record NativePublicServiceModelProjectionBatch(
    NativePublicServiceModelInput[] Models,
    NativePublicServiceModelAliasInput[] Aliases,
    byte[] Text,
    NativePublicServiceModelPayloadSnapshot Payloads);
