using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class StrictHostManagerProfileLoader(HostManagerProfileSource source)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    internal LoadedHostManagerProfile Load()
    {
        ValidateSource(source);
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream(source.ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Host Manager profile resource '{source.ManifestResourceName}' was not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{source.DisplayName}' is empty.");
        }

        return LoadBytes(bytes, source.DisplayName);
    }

    internal static LoadedHostManagerProfile LoadBytes(
        ReadOnlySpan<byte> bytes,
        string sourceDisplayName)
    {
        if (string.IsNullOrWhiteSpace(sourceDisplayName))
        {
            throw new InvalidDataException(
                "The Host Manager profile display name must be explicit.");
        }

        if (bytes.IsEmpty)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{sourceDisplayName}' is empty.");
        }

        try
        {
            ValidateNoDuplicateProperties(bytes);
            var profile = JsonSerializer.Deserialize<HostManagerConfigurationProfile>(
                    bytes,
                    SerializerOptions)
                ?? throw new JsonException("The profile root cannot be null.");
            return new LoadedHostManagerProfile(
                profile,
                sourceDisplayName,
                Convert.ToHexString(SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(profile, SerializerOptions))));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Host Manager profile '{sourceDisplayName}' is invalid: {exception.Message}",
                exception);
        }
    }

    private static void ValidateSource(HostManagerProfileSource value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.ManifestResourceName))
        {
            throw new InvalidOperationException(
                "The Host Manager manifest resource name must be explicit.");
        }

        if (string.IsNullOrWhiteSpace(value.DisplayName))
        {
            throw new InvalidOperationException(
                "The Host Manager profile display name must be explicit.");
        }
    }

    internal static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException("Unexpected object terminator.");
                    }

                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException("A property exists outside an object.");
                    }

                    var propertyName = reader.GetString()
                        ?? throw new JsonException("A property name cannot be null.");
                    if (!objectProperties.Peek().Add(propertyName))
                    {
                        throw new JsonException(
                            $"Duplicate property '{propertyName}' is not allowed.");
                    }

                    break;
            }
        }

        if (objectProperties.Count != 0)
        {
            throw new JsonException("The profile contains an unterminated object.");
        }
    }
}
