using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

internal sealed class BackendFreedomPointRegistry
{
    internal static JsonSerializerOptions CreateDeclarationSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly CompiledFreedomPointTree declarations;
    internal FreedomPointSdkAdapter Sdk { get; }
    internal CompiledFreedomPointTree Declarations => declarations;

    private BackendFreedomPointRegistry(CompiledFreedomPointTree declarations,
        Func<CompiledFreedomPoint, ImmutableArray<global::FreedomPoint.GenerationTarget>>? generationTargets)
    {
        Sdk = new(declarations, generationTargets);
        this.declarations = Sdk.Declarations;
    }

    public static BackendFreedomPointRegistry Parse(ReadOnlySpan<byte> bytes,
        Func<CompiledFreedomPoint, ImmutableArray<global::FreedomPoint.GenerationTarget>>? generationTargets = null)
    {
        try
        {
            ValidateNoDuplicateProperties(bytes);
            var document = JsonSerializer.Deserialize<FreedomPointRegistryDeclaration>(bytes, CreateDeclarationSerializerOptions())
                ?? throw new JsonException("Freedom point root cannot be null.");
            RequireIdentifier(document.Namespace);
            RequireChildren(document.Children);

            var tables = new Dictionary<FreedomPointTableDeclaration, CompiledFreedomPointTable>();
            var pending = new Stack<(FreedomPointTableDeclaration Table, string Parent, bool ChildrenVisited)>();
            foreach (var root in document.Children.Reverse()) pending.Push((root, document.Namespace, false));
            while (pending.TryPop(out var item))
            {
                var table = item.Table;
                RequireIdentifier(table.Id);
                RequireText(table.Description, "table description");
                RequireChildren(table.Children);
                var path = $"{item.Parent}/{table.Id}";
                if (!item.ChildrenVisited)
                {
                    pending.Push((table, item.Parent, true));
                    foreach (var child in table.Children.Reverse()) pending.Push((child, path, false));
                    continue;
                }

                var build = CompileFamily(table.Build, path, "build");
                var runtime = CompileFamily(table.Runtime, path, "runtime");
                if (table.Children.Length == 0 && build.Simple.IsEmpty && build.Complex.IsEmpty
                    && runtime.Simple.IsEmpty && runtime.Complex.IsEmpty)
                {
                    throw new InvalidDataException($"Freedom point table '{path}' is empty.");
                }
                tables.Add(table, new CompiledFreedomPointTable(
                    table.Id, table.Description, build, runtime,
                    table.Children.Select(child => tables[child]).ToImmutableArray()));
            }

            return new BackendFreedomPointRegistry(new CompiledFreedomPointTree(
                document.Namespace, Convert.ToHexString(SHA256.HashData(bytes)),
                document.Children.Select(child => tables[child]).ToImmutableArray()), generationTargets);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid freedom point declaration: {exception.Message}", exception);
        }
    }

    private static CompiledFreedomPointFamily CompileFamily(
        FreedomPointFamilyDeclaration? family, string ownerPath, string familyName)
    {
        return family is null ? CompiledFreedomPointFamily.Empty : new(
            CompilePoints(family.Simple, $"{ownerPath}/{familyName}/simple", familyName, simple: true),
            CompilePoints(family.Complex, $"{ownerPath}/{familyName}/complex", familyName, simple: false));
    }

    private static ImmutableArray<CompiledFreedomPoint> CompilePoints(
        FreedomPointDeclaration[] points, string path, string family, bool simple)
    {
        if (points is null) throw new InvalidDataException($"'{path}' must be an array.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<CompiledFreedomPoint>(points.Length);
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index] ?? throw new InvalidDataException($"'{path}' contains a null point.");
            RequireIdentifier(point.Id);
            RequireText(point.Description, "point description");
            if (point.Index != index || !ids.Add(point.Id))
            {
                throw new InvalidDataException($"'{path}' needs unique ids and dense explicit local indices.");
            }
            var address = $"{path}/{index}";
            if (point.ValueType is not ("positive_int32" or "nonnegative_int32" or "number" or "boolean" or "object" or "array")
                || simple != (point.ValueType is "positive_int32" or "nonnegative_int32" or "number" or "boolean"))
            {
                throw new InvalidDataException($"'{address}' has the wrong value type for its table.");
            }
            if ((family == "build" && point.UpdateClass != "rebuild_backend")
                || (family == "runtime" && point.UpdateClass is not ("publish_plan" or "recreate_host")))
            {
                throw new InvalidDataException($"'{address}' has an invalid update class.");
            }
            if (point.Consumers is null || point.Consumers.Any(string.IsNullOrWhiteSpace)
                || point.Consumers.Distinct(StringComparer.Ordinal).Count() != point.Consumers.Length)
            {
                throw new InvalidDataException($"'{address}' needs distinct named consumers.");
            }
            if (point.Status == "pending")
            {
                RequireText(point.PendingReason, $"{address} pending reason");
                if (point.Consumers.Length != 0 || point.ValueSource is not null
                    || point.SourcePath is not null || point.Value.HasValue)
                {
                    throw new InvalidDataException($"Pending point '{address}' cannot emit a value or declare consumers.");
                }
            }
            else if (point.Status == "active")
            {
                if (point.PendingReason is not null || point.Consumers.Length == 0)
                {
                    throw new InvalidDataException($"Active point '{address}' needs consumers and cannot have a pending reason.");
                }
                switch (point.ValueSource)
                {
                    case "registry" when point.Value.HasValue && point.SourcePath is null:
                        ValidateValue(point.Value.Value, point.ValueType, address);
                        break;
                    case "host_manager_profile" when !point.Value.HasValue:
                    case "cpu_configuration" when !point.Value.HasValue:
                        if (point.SourcePath is null || !point.SourcePath.StartsWith('/')
                            || point.SourcePath.Split('/').Skip(1).Any(segment => !IsIdentifier(segment)))
                        {
                            throw new InvalidDataException($"'{address}' needs an explicit profile field path.");
                        }
                        break;
                    default:
                        throw new InvalidDataException($"'{address}' must have exactly one supported value source.");
                }
            }
            else
            {
                throw new InvalidDataException($"'{address}' has an unknown status.");
            }
            result.Add(new CompiledFreedomPoint(address, index, point.Id, point.Description,
                point.ValueType, point.UpdateClass, point.Status, point.PendingReason,
                point.ValueSource, point.SourcePath, point.Value?.Clone(), point.Consumers.ToImmutableArray()));
        }
        return result.MoveToImmutable();
    }

    internal static void ValidateValue(JsonElement value, string type, string address)
    {
        var valid = type switch
        {
            "positive_int32" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0,
            "nonnegative_int32" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => false
        };
        if (!valid) throw new InvalidDataException($"'{address}' does not contain a {type} value.");
    }

    private static void RequireChildren(FreedomPointTableDeclaration[] children)
    {
        if (children is null || children.Any(static child => child is null)
            || children.Select(static child => child.Id).Distinct(StringComparer.Ordinal).Count() != children.Length)
        {
            throw new InvalidDataException("Freedom point children must have distinct ids and cannot be null.");
        }
    }

    private static bool IsIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static void RequireIdentifier(string? value)
    {
        if (!IsIdentifier(value)) throw new InvalidDataException("Freedom point ids must be nonempty ASCII path segments.");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Freedom point {field} is required.");
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
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
