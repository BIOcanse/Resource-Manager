using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResourceManager.Ledger.Build;

internal static partial class Program
{
    private const string ZigFileName = "LedgerOwnerShape.zig";
    private const string CSharpFileName = "LedgerOwnerShape.g.cs";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "native") return RunNative(args);
        if (args.Length > 0 && args[0] == "verify") return VerifyNative(args);
        if (args.Length != 4 || args[0] != "shape" ||
            !args[3].StartsWith("--uid-width=", StringComparison.Ordinal))
        {
            PrintUsage();
            return 2;
        }

        var uidWidth = args[3]["--uid-width=".Length..] switch
        {
            "128" => (ushort)128,
            "256" => (ushort)256,
            _ => throw new ArgumentException("--uid-width must be 128 or 256."),
        };

        var sourcePath = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[2]);
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var shape = ParseShape(sourceBytes, uidWidth);
        var zig = GenerateZig(shape, uidWidth, sourceHash);
        var csharp = GenerateCSharp(shape, uidWidth, sourceHash);

        Directory.CreateDirectory(outputDirectory);
        WriteIfChanged(Path.Combine(outputDirectory, ZigFileName), zig);
        WriteIfChanged(Path.Combine(outputDirectory, CSharpFileName), csharp);
        Console.WriteLine($"Generated {ZigFileName} and {CSharpFileName}.");
        Console.WriteLine($"Source SHA-256: {sourceHash}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  ResourceManager.Ledger.Build shape <owner.json> <output-directory> --uid-width=128|256");
        Console.Error.WriteLine("  ResourceManager.Ledger.Build native <owner.json> <ledger_values.zig> <output-directory> --uid-width=128|256");
        Console.Error.WriteLine("  ResourceManager.Ledger.Build verify <ledger-native-build.json>");
    }

    private static OwnerShape ParseShape(byte[] source, ushort uidWidth)
    {
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 16 });
        var root = ReadObject(document.RootElement, "owner", "schemaVersion", "indexBucketCount", "tables", "fixedResources");
        var schemaVersion = ReadUInt32(root["schemaVersion"], "schemaVersion");
        if (schemaVersion != 1)
            throw new FormatException("schemaVersion must be 1.");

        var tables = ReadTables(root["tables"]);
        var fixedResources = ReadFixedResources(root["fixedResources"], uidWidth);
        return new OwnerShape(ReadUInt32(root["indexBucketCount"], "indexBucketCount"), tables, fixedResources);
    }

    private static TableDeclaration[] ReadTables(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException("tables must be an array.");

        var tables = new List<TableDeclaration>();
        foreach (var item in value.EnumerateArray())
        {
            var fields = ReadObject(item, "table", "capacity", "partitionCapacity", "reservedStart", "reservedCount", "attributes");
            tables.Add(new TableDeclaration(
                ReadUInt32(fields["capacity"], "table.capacity"),
                ReadUInt32(fields["partitionCapacity"], "table.partitionCapacity"),
                ReadUInt32(fields["reservedStart"], "table.reservedStart"),
                ReadUInt32(fields["reservedCount"], "table.reservedCount"),
                ReadAttributes(fields["attributes"], "table.attributes")));
        }

        return tables.ToArray();
    }

    private static FixedResourceDeclaration[] ReadFixedResources(JsonElement value, ushort uidWidth)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException("fixedResources must be an array.");

        var fixedResources = new List<FixedResourceDeclaration>();
        foreach (var item in value.EnumerateArray())
        {
            var fields = ReadObject(item, "fixed resource", "table", "parentUid", "ordinal", "uid", "allocationBytes", "attributes");
            var parentUid = fields["parentUid"].ValueKind == JsonValueKind.Null
                ? null
                : ReadUid(fields["parentUid"], "fixedResource.parentUid", uidWidth);
            var attributes = fields["attributes"].ValueKind == JsonValueKind.Null
                ? null
                : ReadAttributes(fields["attributes"], "fixedResource.attributes");
            fixedResources.Add(new FixedResourceDeclaration(
                ReadUInt32(fields["table"], "fixedResource.table"),
                parentUid,
                ReadUInt32(fields["ordinal"], "fixedResource.ordinal"),
                ReadUid(fields["uid"], "fixedResource.uid", uidWidth),
                ReadUInt64(fields["allocationBytes"], "fixedResource.allocationBytes"),
                attributes));
        }

        return fixedResources.ToArray();
    }

    private static AttributesDeclaration ReadAttributes(JsonElement value, string context)
    {
        var fields = ReadObject(value, context, "recoverability", "recoveryCostCoefficient", "accessLossImpact");
        var recoverability = ReadString(fields["recoverability"], context + ".recoverability");
        if (recoverability is not ("not_recoverable" or "recoverable"))
            throw new FormatException($"{context}.recoverability has an unknown value.");

        var accessLossImpact = ReadString(fields["accessLossImpact"], context + ".accessLossImpact");
        if (accessLossImpact is not ("fatal" or "observable_now" or "unobservable_now"))
            throw new FormatException($"{context}.accessLossImpact has an unknown value.");

        return new AttributesDeclaration(
            recoverability,
            ReadUInt32(fields["recoveryCostCoefficient"], context + ".recoveryCostCoefficient"),
            accessLossImpact);
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement value, string context, params string[] expectedFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{context} must be an object.");

        var allowed = new HashSet<string>(expectedFields, StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new FormatException($"{context} has unknown field '{property.Name}'.");
            if (!fields.TryAdd(property.Name, property.Value))
                throw new FormatException($"{context} repeats field '{property.Name}'.");
        }

        foreach (var field in expectedFields)
        {
            if (!fields.ContainsKey(field))
                throw new FormatException($"{context} is missing field '{field}'.");
        }

        return fields;
    }

    private static uint ReadUInt32(JsonElement value, string context)
    {
        var parsed = ReadUnsignedInteger(value, context);
        if (parsed > uint.MaxValue)
            throw new FormatException($"{context} is outside the UInt32 range.");
        return (uint)parsed;
    }

    private static ulong ReadUInt64(JsonElement value, string context) => ReadUnsignedInteger(value, context);

    private static ulong ReadUnsignedInteger(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw new FormatException($"{context} must be an unsigned integer.");

        var token = value.GetRawText();
        if (token.Length == 0 || token.Any(static character => character is < '0' or > '9') ||
            !ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"{context} must be an exact unsigned integer.");
        return parsed;
    }

    private static string ReadString(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException($"{context} must be a string.");
        return value.GetString() ?? throw new FormatException($"{context} must not be null.");
    }

    private static byte[] ReadUid(JsonElement value, string context, ushort uidWidth)
    {
        var hex = ReadString(value, context);
        var expectedLength = uidWidth / 4;
        if (hex.Length != expectedLength || hex.Any(static character => !Uri.IsHexDigit(character)))
            throw new FormatException($"{context} must contain exactly {expectedLength} hexadecimal characters for a {uidWidth}-bit UID.");
        return Convert.FromHexString(hex);
    }

    private static string GenerateZig(OwnerShape shape, ushort uidWidth, string sourceHash)
    {
        var output = new StringBuilder();
        output.AppendLine("// Generated from the consumer owner declaration. Do not edit.");
        output.AppendLine($"// Source SHA-256: {sourceHash}");
        output.AppendLine("const std = @import(\"std\");");
        output.AppendLine("const ledger_sdk = @import(\"ledger_sdk\");");
        output.AppendLine("const ledger_values = @import(\"ledger_values\").Bound;");
        output.AppendLine("const types = ledger_sdk.types;");
        output.AppendLine("const uid_module = ledger_sdk.uid;");
        output.AppendLine();
        output.AppendLine($"pub const source_sha256 = \"{sourceHash}\";");
        output.AppendLine($"pub const uid_width_bits: u16 = {uidWidth};");
        output.AppendLine("pub const Uid = uid_module.Identifier(uid_width_bits);");
        output.AppendLine("pub const Fixed = types.For(Uid).Fixed;");
        output.AppendLine("pub const config: types.Config = .{");
        output.AppendLine("    .uid_width = uid_width_bits,");
        output.AppendLine("    .activity_width = @intCast(ledger_values.activity_bitmap_width),");
        output.AppendLine("    .development_allocation_statistics = ledger_values.development_allocation_statistics != 0,");
        output.AppendLine("    .development_lease_analysis = ledger_values.development_lease_analysis != 0,");
        output.AppendLine("    .development_lease_analysis_capacity = @intCast(ledger_values.development_lease_analysis_capacity),");
        output.AppendLine("    .development_lease_analysis_write_watch_pages = @intCast(ledger_values.development_lease_analysis_write_watch_pages),");
        output.AppendLine("    .use_window_cache = ledger_values.use_window_cache != 0,");
        output.AppendLine("    .use_window_cache_slots = @intCast(ledger_values.use_window_cache_slots),");
        output.AppendLine($"    .index_buckets = {shape.IndexBucketCount},");
        output.AppendLine("};");
        output.AppendLine("comptime {");
        output.AppendLine("    if (ledger_values.uid_width_bits != uid_width_bits)");
        output.AppendLine("        @compileError(\"owner UID width must match the selected FreedomPoint values\");");
        output.AppendLine("}");
        output.AppendLine();
        output.AppendLine("pub const tables = [_]types.Table{");
        for (var index = 0; index < shape.Tables.Length; index++)
        {
            var table = shape.Tables[index];
            output.AppendLine("    .{");
            output.AppendLine($"        .capacity = {table.Capacity},");
            output.AppendLine($"        .partition_capacity = {table.PartitionCapacity},");
            output.AppendLine($"        .reserved_start = {table.ReservedStart},");
            output.AppendLine($"        .reserved_count = {table.ReservedCount},");
            output.AppendLine($"        .attributes = {ZigAttributes(table.Attributes)},");
            AppendZigPolicy(output, index);
            output.AppendLine("    },");
        }
        output.AppendLine("};");
        output.AppendLine();
        output.AppendLine("pub const fixed = [_]Fixed{");
        foreach (var item in shape.FixedResources)
        {
            output.AppendLine("    .{");
            output.AppendLine($"        .table = {item.Table},");
            output.AppendLine($"        .parent = {ZigUid(item.ParentUid)},");
            output.AppendLine($"        .ordinal = {item.Ordinal},");
            output.AppendLine($"        .uid = {ZigUid(item.Uid)},");
            output.AppendLine($"        .allocation_bytes = {item.AllocationBytes},");
            output.AppendLine($"        .attributes = {(item.Attributes is null ? "null" : ZigAttributes(item.Attributes))},");
            output.AppendLine("    },");
        }
        output.AppendLine("};");
        return output.ToString();
    }

    private static void AppendZigPolicy(StringBuilder output, int table)
    {
        string prefix = $"ledger_values.table_{table}_";
        output.AppendLine("        .policy = .{");
        output.AppendLine($"            .score_epochs = @intCast({prefix}activity_score_epochs),");
        output.AppendLine($"            .idle_epochs = @intCast({prefix}idle_epochs),");
        output.AppendLine($"            .protection = switch ({prefix}capacity_protection_method) {{");
        output.AppendLine("                1 => .concentrated,");
        output.AppendLine("                2 => .smooth,");
        output.AppendLine("                else => @compileError(\"selected capacity protection method is unsupported\"),");
        output.AppendLine("            },");
        output.AppendLine($"            .concentrated_trigger_percent = @intCast({prefix}concentrated_trigger_percent),");
        output.AppendLine($"            .concentrated_target_percent = @intCast({prefix}concentrated_target_percent),");
        output.AppendLine($"            .smooth_trigger_percent = @intCast({prefix}smooth_trigger_percent),");
        output.AppendLine($"            .smooth_emergency_percent = @intCast({prefix}smooth_emergency_percent),");
        output.AppendLine($"            .smooth_interval_epochs = @intCast({prefix}smooth_interval_epochs),");
        output.AppendLine($"            .smooth_release_count = @intCast({prefix}smooth_release_count),");
        output.AppendLine($"            .tick_maximum_actions = @intCast({prefix}tick_maximum_actions),");
        output.AppendLine("            .release_check = .{");
        output.AppendLine($"                .enabled = {prefix}release_check_enabled == 2,");
        output.AppendLine($"                .tolerance_parts_per_million = @intCast({prefix}release_tolerance_parts_per_million),");
        output.AppendLine($"                .tolerance_bytes = {prefix}release_tolerance_bytes,");
        output.AppendLine("            },");
        output.AppendLine("        },");
    }

    private static string GenerateCSharp(OwnerShape shape, ushort uidWidth, string sourceHash)
    {
        var uidType = uidWidth == 128 ? "LedgerUid128" : "LedgerUid256";
        var output = new StringBuilder();
        output.AppendLine("// <auto-generated />");
        output.AppendLine($"// Source SHA-256: {sourceHash}");
        output.AppendLine("namespace ResourceManager.Ledger.Generated;");
        output.AppendLine();
        output.AppendLine("/// <summary>Static identifiers and fixed UIDs from one consumer owner declaration.</summary>");
        output.AppendLine("public static class LedgerOwnerShape");
        output.AppendLine("{");
        output.AppendLine($"    public const string SourceSha256 = \"{sourceHash}\";");
        output.AppendLine($"    public const ushort UidWidthBits = {uidWidth};");
        output.AppendLine($"    public const int TableCount = {shape.Tables.Length};");
        output.AppendLine($"    public const int FixedResourceCount = {shape.FixedResources.Length};");
        for (var index = 0; index < shape.Tables.Length; index++)
            output.AppendLine($"    public const uint Table_{index} = {index};");
        for (var index = 0; index < shape.FixedResources.Length; index++)
        {
            var item = shape.FixedResources[index];
            output.AppendLine($"    public const uint FixedResource_{index} = {index};");
            output.AppendLine($"    public const uint FixedResource_{index}_Table = {item.Table};");
            output.AppendLine($"    public const uint FixedResource_{index}_Ordinal = {item.Ordinal};");
            output.AppendLine($"    public static readonly global::ResourceManager.Ledger.Identity.{uidType} FixedResource_{index}_Uid = new global::ResourceManager.Ledger.Identity.{uidType}(new byte[] {{ {CSharpBytes(item.Uid)} }});");
            output.AppendLine(item.ParentUid is null
                ? $"    public static readonly global::ResourceManager.Ledger.Identity.{uidType}? FixedResource_{index}_ParentUid = null;"
                : $"    public static readonly global::ResourceManager.Ledger.Identity.{uidType}? FixedResource_{index}_ParentUid = new global::ResourceManager.Ledger.Identity.{uidType}(new byte[] {{ {CSharpBytes(item.ParentUid)} }});");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    private static string ZigAttributes(AttributesDeclaration attributes) =>
        $"types.Attributes{{ .recoverability = .{attributes.Recoverability}, .recovery_cost_coefficient = {attributes.RecoveryCostCoefficient}, .access_loss_impact = .{attributes.AccessLossImpact} }}";

    private static string ZigUid(byte[]? bytes) => bytes is null
        ? "null"
        : $"Uid{{ .bytes = .{{ {ZigBytes(bytes)} }} }}";

    private static string ZigBytes(byte[] bytes) => string.Join(", ", bytes.Select(static value => $"0x{value:x2}"));

    private static string CSharpBytes(byte[] bytes) => string.Join(", ", bytes.Select(static value => $"0x{value:x2}"));

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        File.WriteAllText(path, content, Utf8WithoutBom);
    }

    private sealed record OwnerShape(uint IndexBucketCount, TableDeclaration[] Tables, FixedResourceDeclaration[] FixedResources);
    private sealed record TableDeclaration(uint Capacity, uint PartitionCapacity, uint ReservedStart, uint ReservedCount, AttributesDeclaration Attributes);
    private sealed record FixedResourceDeclaration(uint Table, byte[]? ParentUid, uint Ordinal, byte[] Uid, ulong AllocationBytes, AttributesDeclaration? Attributes);
    private sealed record AttributesDeclaration(string Recoverability, uint RecoveryCostCoefficient, string AccessLossImpact);
}
