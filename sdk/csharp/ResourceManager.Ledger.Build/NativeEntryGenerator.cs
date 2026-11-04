using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResourceManager.Ledger.Build;

internal static partial class Program
{
    private const string NativeEntryFileName = "LedgerNativeEntry.zig";
    private const string BuildIdentityFileName = "LedgerOwnerBuild.g.cs";
    private const string BuildPropsFileName = "LedgerOwnerBuild.props";

    private static int RunNative(string[] args)
    {
        if (args.Length != 5 || !args[4].StartsWith("--uid-width=", StringComparison.Ordinal))
        {
            PrintUsage();
            return 2;
        }
        ushort width = args[4]["--uid-width=".Length..] switch
        {
            "128" => 128,
            "256" => 256,
            _ => throw new ArgumentException("--uid-width must be 128 or 256."),
        };
        string ownerPath = Path.GetFullPath(args[1]);
        string valuesPath = Path.GetFullPath(args[2]);
        string outputPath = Path.GetFullPath(args[3]);
        byte[] ownerBytes = File.ReadAllBytes(ownerPath);
        byte[] valuesBytes = File.ReadAllBytes(valuesPath);
        byte[] structureHash = SHA256.HashData(ownerBytes);
        byte[] policyHash = SHA256.HashData(valuesBytes);
        string structureHex = Convert.ToHexString(structureHash).ToLowerInvariant();
        string policyHex = Convert.ToHexString(policyHash).ToLowerInvariant();
        OwnerShape shape = ParseShape(ownerBytes, width);
        bool developmentLeaseAnalysis = ReadDevelopmentLeaseAnalysis(valuesBytes);

        // Represent counts exactly; the selected owner checks structural legality.
        ulong resources = 0;
        ulong partitions = 0;
        foreach (TableDeclaration table in shape.Tables)
        {
            resources = checked(resources + table.Capacity);
            partitions = checked(partitions + table.PartitionCapacity);
        }
        uint resourceCount = checked((uint)resources);
        uint partitionCount = checked((uint)partitions);
        uint nodeCount = checked((uint)(resources + partitions));

        string native = GenerateNativeEntry(structureHash, policyHash);
        string managed = GenerateBuildIdentity(shape, width, resourceCount, partitionCount, nodeCount, structureHex, policyHex,
            developmentLeaseAnalysis);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ZigFileName] = GenerateZig(shape, width, structureHex),
            [CSharpFileName] = GenerateCSharp(shape, width, structureHex),
            [NativeEntryFileName] = native,
            [BuildIdentityFileName] = managed,
            [BuildPropsFileName] = GenerateBuildProps(developmentLeaseAnalysis),
        };
        Directory.CreateDirectory(outputPath);
        foreach ((string file, string content) in outputs) WriteIfChanged(Path.Combine(outputPath, file), content);

        // Write the complete manifest last so consumers can reject a partial generation.
        var outputRows = outputs.Select(pair => new
        {
            file = pair.Key,
            sha256 = Convert.ToHexString(SHA256.HashData(Utf8WithoutBom.GetBytes(pair.Value))).ToLowerInvariant(),
        }).ToArray();
        string manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            abiVersion = 1,
            candidate = "c2_chain",
            uidWidthBits = width,
            tableCount = shape.Tables.Length,
            fixedCount = shape.FixedResources.Length,
            resourceCapacity = resourceCount,
            partitionCapacity = partitionCount,
            indexNodeCount = nodeCount,
            indexHomeCount = shape.IndexBucketCount,
            ownerPath,
            valuesPath,
            structureSha256 = structureHex,
            policySha256 = policyHex,
            outputs = outputRows,
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteIfChanged(Path.Combine(outputPath, "ledger-native-build.json"), manifest + "\n");
        Console.WriteLine("Generated the paired C2 Native entry, managed identity, owner shape and build manifest.");
        Console.WriteLine($"Structure SHA-256: {structureHex}");
        Console.WriteLine($"Policy SHA-256: {policyHex}");
        return 0;
    }

    private static string GenerateNativeEntry(byte[] structureHash, byte[] policyHash)
    {
        var output = new StringBuilder();
        output.AppendLine("// Generated paired Native entry. Do not edit.");
        output.AppendLine("const ledger_sdk = @import(\"ledger_sdk\");");
        output.AppendLine("const shape = @import(\"ledger_owner_shape\");");
        output.AppendLine("pub const LedgerBinding = ledger_sdk.native.Binding(shape.config, &shape.tables, &shape.fixed, .{");
        output.AppendLine($"    .structure_hash = .{{ {ZigBytes(structureHash)} }},");
        output.AppendLine($"    .policy_hash = .{{ {ZigBytes(policyHash)} }},");
        output.AppendLine("});");
        output.AppendLine("comptime { LedgerBinding.exportAll(); }");
        return output.ToString();
    }

    private static int VerifyNative(string[] args)
    {
        if (args.Length != 2)
        {
            PrintUsage();
            return 2;
        }
        string manifestPath = Path.GetFullPath(args[1]);
        string directory = Path.GetDirectoryName(manifestPath)!;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { MaxDepth = 8 });
        Dictionary<string, JsonElement> fields = ReadObject(document.RootElement, "Native build manifest",
            "schemaVersion", "abiVersion", "candidate", "uidWidthBits", "tableCount", "fixedCount", "resourceCapacity",
            "partitionCapacity", "indexNodeCount", "indexHomeCount", "ownerPath", "valuesPath", "structureSha256", "policySha256", "outputs");
        uint width = ReadUInt32(fields["uidWidthBits"], "uidWidthBits");
        if (ReadUInt32(fields["schemaVersion"], "schemaVersion") != 1 ||
            ReadUInt32(fields["abiVersion"], "abiVersion") != 1 ||
            ReadString(fields["candidate"], "candidate") != "c2_chain" || width is not (128 or 256))
            throw new FormatException("Unsupported Native build manifest identity.");

        VerifyFileHash(ReadString(fields["ownerPath"], "ownerPath"), ReadString(fields["structureSha256"], "structureSha256"));
        VerifyFileHash(ReadString(fields["valuesPath"], "valuesPath"), ReadString(fields["policySha256"], "policySha256"));
        var remaining = new HashSet<string>([ZigFileName, CSharpFileName, NativeEntryFileName, BuildIdentityFileName, BuildPropsFileName], StringComparer.Ordinal);
        if (fields["outputs"].ValueKind != JsonValueKind.Array) throw new FormatException("outputs must be an array.");
        foreach (JsonElement item in fields["outputs"].EnumerateArray())
        {
            Dictionary<string, JsonElement> row = ReadObject(item, "output", "file", "sha256");
            string name = ReadString(row["file"], "output.file");
            if (!remaining.Remove(name)) throw new FormatException("Unknown or repeated generated output: " + name);
            VerifyFileHash(Path.Combine(directory, name), ReadString(row["sha256"], "output.sha256"));
        }
        if (remaining.Count != 0) throw new FormatException("Native build manifest omits generated source files.");
        // Bind selected-value outputs to the original input as well as their manifest hashes.
        // Rehashing a stale props/companion in the manifest must not bless a different switch.
        bool developmentLeaseAnalysis = ReadDevelopmentLeaseAnalysis(
            File.ReadAllBytes(ReadString(fields["valuesPath"], "valuesPath")));
        RequireGeneratedContent(directory, BuildPropsFileName, GenerateBuildProps(developmentLeaseAnalysis));
        OwnerShape shape = ParseShape(File.ReadAllBytes(ReadString(fields["ownerPath"], "ownerPath")), checked((ushort)width));
        RequireGeneratedContent(directory, BuildIdentityFileName, GenerateBuildIdentity(shape, checked((ushort)width),
            ReadUInt32(fields["resourceCapacity"], "resourceCapacity"),
            ReadUInt32(fields["partitionCapacity"], "partitionCapacity"),
            ReadUInt32(fields["indexNodeCount"], "indexNodeCount"),
            ReadString(fields["structureSha256"], "structureSha256"),
            ReadString(fields["policySha256"], "policySha256"), developmentLeaseAnalysis));
        Console.WriteLine("Native build inputs and generated source hashes match.");
        return 0;
    }

    private static void VerifyFileHash(string path, string expected)
    {
        if (!Path.IsPathFullyQualified(path)) throw new FormatException("Native build input paths must be absolute.");
        if (expected.Length != 64 || expected.Any(static character => !Uri.IsHexDigit(character)))
            throw new FormatException("A SHA-256 fingerprint must have exactly 64 hexadecimal characters.");
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native build input changed; generate again: " + path);
    }

    private static bool ReadDevelopmentLeaseAnalysis(byte[] valuesBytes)
    {
        // Read the scalar Bound initializer emitted by FreedomPoint, not arbitrary Zig.
        // Reject other expressions rather than guessing or selecting a fallback value.
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(valuesBytes));
        string structureImport = string.Concat(Encoding.UTF8.GetBytes("ledger_structure")
            .Select(value => "\\x" + value.ToString("X2", CultureInfo.InvariantCulture)));
        if (reader.ReadLine() != "// Generated selected values." ||
            reader.ReadLine() != $"const __fpStructure = @import(\"{structureImport}\");" ||
            reader.ReadLine() != "pub const Bound = __fpStructure.Bind(.{")
            throw new FormatException("Expected the FreedomPoint-generated ledger_values.zig Bound initializer.");

        var selected = new Dictionary<string, ulong>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null && line != "});")
        {
            const string prefix = "    .@\"";
            const string separator = "\" = @as(u64, @as(u64, ";
            const string suffix = ")),";
            int split = line.IndexOf(separator, StringComparison.Ordinal);
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || split <= prefix.Length ||
                !line.EndsWith(suffix, StringComparison.Ordinal))
                throw new FormatException("Expected an unsigned scalar selected value in ledger_values.zig.");
            string name = line[prefix.Length..split];
            string number = line[(split + separator.Length)..^suffix.Length];
            if (name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
                number.Length == 0 || number.Any(static character => character is < '0' or > '9') ||
                !ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ||
                !selected.TryAdd(name, value))
                throw new FormatException("Invalid or repeated selected value in ledger_values.zig: " + name);
        }
        if (line != "});" || reader.ReadLine() is not null)
            throw new FormatException("Expected the end of the generated ledger_values.zig Bound initializer.");
        if (!selected.TryGetValue("development_lease_analysis", out ulong enabled) || enabled > 1)
            throw new FormatException("development_lease_analysis must be a selected unsigned 0 or 1.");
        if (!selected.TryGetValue("development_lease_analysis_capacity", out ulong capacity) || capacity is < 1 or > 65536)
            throw new FormatException("development_lease_analysis_capacity must be a selected unsigned integer from 1 through 65536.");
        return enabled != 0;
    }

    private static string GenerateBuildProps(bool developmentLeaseAnalysis)
    {
        // Fixed LF bytes make this selected-values output independent of the host OS.
        return developmentLeaseAnalysis
            ? "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);LEDGER_DEVELOPMENT_LEASE_ANALYSIS</DefineConstants>\n  </PropertyGroup>\n</Project>\n"
            : "<Project>\n</Project>\n";
    }

    private static void RequireGeneratedContent(string directory, string name, string expected)
    {
        if (!File.ReadAllBytes(Path.Combine(directory, name)).AsSpan().SequenceEqual(Utf8WithoutBom.GetBytes(expected)))
            throw new InvalidOperationException("Generated output differs from selected build inputs; generate again: " + name);
    }

    private static string GenerateBuildIdentity(OwnerShape shape, ushort width, uint resources, uint partitions,
        uint nodes, string structureHash, string policyHash, bool developmentLeaseAnalysis)
    {
        // The generated metadata is immutable and contains no live resource state.
        string Number(uint value) => value.ToString(CultureInfo.InvariantCulture) + "u";
        var output = new StringBuilder();
        // Fixed LF: verify regenerates this file and compares bytes, so the bytes cannot depend on the host newline.
        void Line(string text) => output.Append(text).Append('\n');
        Line("// <auto-generated />");
        Line("namespace ResourceManager.Ledger.Generated;");
        Line("public static class LedgerOwnerBuild");
        Line("{");
        Line($"    public const ushort UidWidthBits = {width};");
        Line($"    public const bool DevelopmentLeaseAnalysis = {(developmentLeaseAnalysis ? "true" : "false")};");
        Line($"    public const string StructureSha256 = \"{structureHash}\";");
        Line($"    public const string PolicySha256 = \"{policyHash}\";");
        Line("    public static readonly global::ResourceManager.Ledger.LedgerBuildIdentity Identity = new(");
        Line($"        {Number(checked((uint)shape.Tables.Length))}, {Number(checked((uint)shape.FixedResources.Length))},");
        Line($"        {Number(resources)}, {Number(partitions)}, {Number(nodes)}, {Number(shape.IndexBucketCount)},");
        Line("        global::System.Convert.FromHexString(StructureSha256),");
        Line("        global::System.Convert.FromHexString(PolicySha256));");
        Line("}");
        return output.ToString();
    }
}
