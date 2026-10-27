using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ResourceManager.Development.RuntimeReadCheck;

/// <summary>Bounded lexical native boundary check. Does not claim C++/Zig type or alias resolution.</summary>
public static partial class NativeReadCheck
{
    private static readonly DiagnosticDescriptor Unavailable = new("RMR100", "Native source cannot be checked",
        "Native source '{0}' could not be read within the 8 MiB source bound", "RuntimeRead", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Missing = new("RMR101", "Native raw read needs volatility",
        "Native {0} read '{1}' requires RuntimeRead at owning function '{2}'", "RuntimeRead", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Event = new("RMR102", "Native event read needs event",
        "Native EventDriven read '{0}' requires a nonempty change event", "RuntimeRead", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Invalid = new("RMR104", "Invalid native read declaration",
        "Native read '{0}' requires exactly one Fixed, EventDriven(\"event\"), Sampled or Transport declaration", "RuntimeRead", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Transport = new("RMR103", "Native mixed transport needs an owning source",
        "Native transport '{0}' has unverified {1} read '{2}'; bind its owning field/source or separate the reads", "RuntimeRead", DiagnosticSeverity.Warning, true);
    public static ImmutableArray<DiagnosticDescriptor> Rules => [Unavailable, Missing, Event, Transport, Invalid];
    public static Diagnostic CannotRead(string path) => Diagnostic.Create(Unavailable, Location.None, path);
    public static (int Kind, string? ChangeEvent) ParseDeclaration(string value)
    {
        if (value == "Fixed") return (0, null);
        if (value == "Sampled") return (2, null);
        if (value == "Transport") return (3, null);
        var match = EventDeclaration().Match(value);
        return match.Success ? (1, match.Groups[1].Value) : (-1, null);
    }

    public static IReadOnlyList<NativeReadSite> Sites(SourceText text, string path)
    {
        var source = text.ToString();
        if (source.Length > 8 * 1024 * 1024) throw new InvalidDataException($"Native source exceeds 8 MiB bound: {path}");
        var (tokens, marks) = Tokenize(source);
        var pointerNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 3 < tokens.Count; index++)
            if (tokens[index].Value == "*" && tokens[index + 1].Identifier && tokens[index + 2].Value == ")" && tokens[index + 3].Value == "(")
                pointerNames.Add(tokens[index + 1].Value);
        for (var index = 0; index + 2 < tokens.Count; index++)
            if (pointerNames.Contains(tokens[index].Value) && tokens[index + 1].Identifier
                && tokens[index + 2].Value is "=" or "," or ")" or ";")
                pointerNames.Add(tokens[index + 1].Value);
        var parentheses = new Dictionary<int, int>();
        var openings = new Stack<int>();
        var braces = new Dictionary<int, int>();
        var braceOpenings = new Stack<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Value == "(") openings.Push(index);
            if (tokens[index].Value == ")" && openings.TryPop(out var open)) parentheses[index] = open;
            if (tokens[index].Value == "{") braceOpenings.Push(index);
            if (tokens[index].Value == "}" && braceOpenings.TryPop(out var brace)) braces[index] = brace;
        }
        var functions = new Dictionary<int, Function>();
        var definitions = new HashSet<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Value != "{") continue;
            // Zig's inline return error set is a type, not the function body.
            if (index > 0 && tokens[index - 1].Value == "error") continue;
            var nameIndex = FunctionName(tokens, parentheses, braces, index);
            if (nameIndex < 0) continue;
            var header = nameIndex;
            while (header > 0 && tokens[header - 1].Value is not (";" or "{" or "}")) header--;
            var previousEnd = header == 0 ? 0 : tokens[header - 1].End;
            var declarations = marks.Where(mark => mark.Start >= previousEnd && mark.Start < tokens[nameIndex].Start).ToArray();
            functions[index] = new(tokens[nameIndex].Value, tokens[nameIndex].Start, declarations);
            definitions.Add(nameIndex);
        }
        var scope = new Stack<Function?>();
        Function? current = null;
        var sites = new List<NativeReadSite>();
        var adlx = path.Replace('\\', '/').Contains("/AdlxBridge/", StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Value == "{")
            {
                scope.Push(current);
                if (functions.TryGetValue(index, out var function)) current = function;
                continue;
            }
            if (token.Value == "}") { if (scope.TryPop(out var previous)) current = previous; continue; }
            if (!token.Identifier || definitions.Contains(index) || index + 1 >= tokens.Count) continue;
            var pointer = adlx && pointerNames.Contains(token.Value) && !NonReadPointer(token.Value);
            var call = tokens[index + 1].Value == "(";
            var cast = pointer && index > 0 && tokens[index - 1].Value == "(" && tokens[index + 1].Value == ")";
            if (!call && !cast) continue;
            if (IsExternDeclaration(tokens, index)) continue;
            var member = index > 0 && tokens[index - 1].Value is "->" or ".";
            var family = Family(token.Value, adlx, member) ?? (pointer ? "ADLX function-pointer" : null);
            if (family is null) continue;
            sites.Add(new(current?.Name ?? "<no function declaration>", family, token.Value,
                new TextSpan(token.Start, token.End - token.Start), current?.Start ?? -1, current?.Marks.Select(static mark => mark.Value).ToArray() ?? []));
        }
        return sites;
    }

    public static IReadOnlyList<Diagnostic> Analyze(SourceText text, string path)
    {
        if (text.Length > 8 * 1024 * 1024) return [CannotRead(path)];
        var diagnostics = new List<Diagnostic>();
        var checkedDeclarations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in Sites(text, path))
        {
            var location = Location.Create(path, site.Span, text.Lines.GetLinePositionSpan(site.Span));
            if (site.Declarations.Count == 0)
            {
                diagnostics.Add(Diagnostic.Create(Missing, location, site.Family, site.Api, site.Owner));
                continue;
            }
            if (site.Declarations is ["Transport"])
            {
                diagnostics.Add(Diagnostic.Create(Transport, location, site.Owner, site.Family, site.Api));
                continue;
            }
            var key = site.OwnerStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!checkedDeclarations.Add(key)) continue;
            if (site.Declarations.Count != 1)
            {
                diagnostics.Add(Diagnostic.Create(Invalid, location, site.Owner));
                continue;
            }
            var declaration = site.Declarations[0];
            if (declaration is "Fixed" or "Sampled") continue;
            var match = EventDeclaration().Match(declaration);
            if (!match.Success) diagnostics.Add(Diagnostic.Create(Invalid, location, site.Owner));
            else if (string.IsNullOrWhiteSpace(match.Groups[1].Value)) diagnostics.Add(Diagnostic.Create(Event, location, site.Owner));
        }
        return diagnostics;
    }

    private static int FunctionName(IReadOnlyList<Token> tokens, IReadOnlyDictionary<int, int> parentheses,
        IReadOnlyDictionary<int, int> braces, int body)
    {
        var lower = Math.Max(0, body - 512);
        for (var index = body - 1; index >= lower; index--)
        {
            if (tokens[index].Value == "}" && braces.TryGetValue(index, out var errorOpen)
                && errorOpen > lower && tokens[errorOpen - 1].Value == "error")
            {
                index = errorOpen - 1;
                continue;
            }
            if (tokens[index].Value is ";" or "{" or "}") break;
            if (tokens[index].Value == "fn" && index + 1 < body && tokens[index + 1].Identifier) return index + 1;
            if (tokens[index].Value != ")" || !parentheses.TryGetValue(index, out var open) || open == 0) continue;
            var name = open - 1;
            if (!tokens[name].Identifier) return -1; // lambda or parenthesized expression
            if (tokens[name].Value is "if" or "for" or "while" or "switch" or "catch" or "__except" or "__try" or "sizeof" or "decltype" or "alignas") return -1;
            if (tokens[name].Value is "noexcept" or "requires") { index = name; continue; }
            return name;
        }
        return -1;
    }

    private static bool IsExternDeclaration(IReadOnlyList<Token> tokens, int name)
    {
        if (name > 0 && tokens[name - 1].Value == "fn") return true;
        for (var index = name - 1; index >= Math.Max(0, name - 64); index--)
        {
            if (tokens[index].Value is ";" or "{" or "}" or "=" or "return" or "(") return false;
            if (tokens[index].Value == "extern") return true;
        }
        return false;
    }

    private static string? Family(string name, bool adlx, bool member)
    {
        if (name.StartsWith("__cpuid", StringComparison.Ordinal) || name.StartsWith("__get_cpuid", StringComparison.Ordinal)) return "CPUID";
        if (member && name is "ExecQuery" or "ExecNotificationQuery" or "CreateInstanceEnum" or "ExecMethod" or "GetObject") return "COM/WMI";
        if (name.StartsWith("SetupDi", StringComparison.Ordinal) && (name.Contains("Get", StringComparison.Ordinal) || name.Contains("Enum", StringComparison.Ordinal))
            || name.StartsWith("CM_Get_", StringComparison.Ordinal) || name.StartsWith("CM_Locate_DevNode", StringComparison.Ordinal)) return "SetupAPI/ConfigMgr";
        if (name.StartsWith("RegQuery", StringComparison.Ordinal) || name.StartsWith("RegEnum", StringComparison.Ordinal)
            || name.StartsWith("RegGetValue", StringComparison.Ordinal) || name.StartsWith("RegOpenKey", StringComparison.Ordinal)) return "registry";
        if (name.StartsWith("nvmlDeviceGet", StringComparison.Ordinal) || name.StartsWith("nvmlSystemGet", StringComparison.Ordinal)) return "NVML";
        if (name is "GetSystemFirmwareTable" or "EnumSystemFirmwareTables") return "ACPI/firmware";
        if (name.StartsWith("CreateDXGIFactory", StringComparison.Ordinal) || name.StartsWith("EnumAdapters", StringComparison.Ordinal)
            || name is "EnumOutputs" or "GetDesc" or "GetDesc1" or "GetDesc2" or "QueryVideoMemoryInfo") return "DXGI";
        if (name is "GetEnabledXStateFeatures" or "IsProcessorFeaturePresent") return "CPU OS capabilities";
        if (name is "GetLogicalProcessorInformationEx" or "GetSystemCpuSetInformation") return "CPU OS topology";
        if (name is "QueryPerformanceCounter" or "QueryPerformanceFrequency" or "GetSystemTimeAsFileTime"
            or "GetSystemTimePreciseAsFileTime" or "GetTickCount64") return "clock";
        if (name is "OpenTraceW" or "OpenTraceA" or "ProcessTrace") return "ETW";
        if (name is "GetLogicalDrives" or "QueryDosDeviceW" or "QueryDosDeviceA") return "volume mapping";
        if (name is "GetEnvironmentVariableW" or "GetEnvironmentVariableA") return "environment";
        if (adlx && member && name is not ("Release" or "AddRef" or "Acquire") && !name.StartsWith("Set", StringComparison.Ordinal)) return "ADLX";
        return null;
    }

    private static bool NonReadPointer(string name)
        => name is "Release" or "Acquire" or "AddRef" || name.StartsWith("RmSet", StringComparison.Ordinal)
            || name.StartsWith("Set", StringComparison.Ordinal);

    private static (List<Token> Tokens, List<Mark> Marks) Tokenize(string source)
    {
        var tokens = new List<Token>();
        var marks = new List<Mark>();
        var index = 0;
        while (index < source.Length)
        {
            var start = index;
            var value = source[index];
            if (char.IsWhiteSpace(value)) { index++; continue; }
            if (value == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                var end = source.IndexOf('\n', index);
                if (end < 0) end = source.Length;
                var comment = source[(index + 2)..end].Trim();
                if (comment.StartsWith("RuntimeRead:", StringComparison.Ordinal)) marks.Add(new(index, comment[12..].Trim()));
                index = end;
                continue;
            }
            if (value == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 2;
                continue;
            }
            if (value == '\\' && index + 1 < source.Length && source[index + 1] == '\\')
            {
                var end = source.IndexOf('\n', index);
                index = end < 0 ? source.Length : end; // Zig multiline string line
                continue;
            }
            var rawPrefix = source.AsSpan(index).StartsWith("R\"", StringComparison.Ordinal) ? 1
                : source.AsSpan(index).StartsWith("u8R\"", StringComparison.Ordinal) ? 3
                : source.AsSpan(index).StartsWith("uR\"", StringComparison.Ordinal)
                    || source.AsSpan(index).StartsWith("UR\"", StringComparison.Ordinal)
                    || source.AsSpan(index).StartsWith("LR\"", StringComparison.Ordinal) ? 2 : 0;
            if (rawPrefix > 0)
            {
                var open = source.IndexOf('(', index + rawPrefix + 1);
                if (open >= 0 && open - index - rawPrefix <= 17)
                {
                    var closeText = ")" + source[(index + rawPrefix + 1)..open] + "\"";
                    var close = source.IndexOf(closeText, open + 1, StringComparison.Ordinal);
                    index = close < 0 ? source.Length : close + closeText.Length;
                    continue;
                }
            }
            if (value is '\'' or '"')
            {
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\') { index = Math.Min(source.Length, index + 2); continue; }
                    if (source[index++] == value) break;
                }
                continue;
            }
            if (char.IsLetter(value) || value == '_')
            {
                index++;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
                tokens.Add(new(source[start..index], start, index, true));
                continue;
            }
            index++;
            if (value == '-' && index < source.Length && source[index] == '>') index++;
            tokens.Add(new(source[start..index], start, index, false));
        }
        return (tokens, marks);
    }

    [GeneratedRegex("^EventDriven\\(\\\"([^\\\"]*)\\\"\\)$", RegexOptions.CultureInvariant)]
    private static partial Regex EventDeclaration();
    private sealed record Token(string Value, int Start, int End, bool Identifier);
    private sealed record Mark(int Start, string Value);
    private sealed record Function(string Name, int Start, IReadOnlyList<Mark> Marks);
    public sealed record NativeReadSite(string Owner, string Family, string Api, TextSpan Span, int OwnerStart, IReadOnlyList<string> Declarations);
}
