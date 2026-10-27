using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using ResourceManager.Development.RuntimeReadCheck;

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
var declaration = """
namespace ResourceManager.ReadWrite {
public enum ReadVolatilityKind { Fixed, EventDriven, Sampled }
[System.AttributeUsage(System.AttributeTargets.All)]
public sealed class RuntimeReadAttribute(ReadVolatilityKind kind, string? changeEvent = null) : System.Attribute { }
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class RuntimeReadTransportAttribute : System.Attribute { }
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class RuntimeReadCacheAttribute : System.Attribute { }
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class RuntimeReadSourceAttribute(System.Type sourceOwner, string sourceField) : System.Attribute { }
}
""";
var assertions = 0;
if (args.Length != 0 && args is not ["--raw-sites", _, _] && args is not ["--native-sites", _])
{
    Console.Error.WriteLine("Usage: RuntimeReadCheck.Tests [--raw-sites <repository> <System.Management package root> | --native-sites <repository>]");
    Environment.ExitCode = 2;
    return;
}
if (args is ["--raw-sites", var root, var managementRoot])
{
    // Inventory aid only: a product compiler build remains the authoritative gate.
    var files = new[] { "src/Core", "src/ReadWriteCore" }.SelectMany(directory =>
        Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))).ToArray();
    var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
    Console.Error.WriteLine($"Inventory management reference root: {managementRoot}");
    var management = Directory.EnumerateFiles(managementRoot, "System.Management.dll", SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains("net10.0", StringComparison.Ordinal) && !path.Contains("runtimes", StringComparison.Ordinal));
    var inventoryReferences = management is null ? references : references.Append(MetadataReference.CreateFromFile(management)).ToArray();
    var compilation = CSharpCompilation.Create("Inventory", trees, inventoryReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    foreach (var tree in trees)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var group in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(node => (Node: node, Symbol: model.GetSymbolInfo(node).Symbol as IMethodSymbol))
            .Where(item => item.Symbol is not null && RuntimeReadAnalyzer.RawFamily(item.Symbol) is not null)
            .GroupBy(item => item.Node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(member => member is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax)))
        {
            var member = group.Key;
            var name = member is MethodDeclarationSyntax method ? method.Identifier.Text : member?.Kind().ToString();
            var marked = member?.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Contains("RuntimeRead", StringComparison.Ordinal)) == true;
            Console.WriteLine($"{Path.GetRelativePath(root, tree.FilePath)}:{(member?.GetLocation().GetLineSpan().StartLinePosition.Line ?? 0) + 1}\t{name}\t{(marked ? "marked" : "UNMARKED")}\t{string.Join(',', group.Select(item => RuntimeReadAnalyzer.RawFamily(item.Symbol!)).Distinct())}");
        }
    }
    return;
}
if (args is ["--native-sites", var nativeRoot])
{
    foreach (var file in new[] { "src/Core/Native", "src/Core/NativeCore" }.SelectMany(directory =>
        Directory.EnumerateFiles(Path.Combine(nativeRoot, directory), "*", SearchOption.AllDirectories))
        .Where(path => Path.GetExtension(path) is ".c" or ".cpp" or ".zig" or ".h" or ".hpp" or ".inc")
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin" or "zig-out" or ".zig-cache" or "third_party" or "third-party" or "vendor")))
    {
        var source = Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(file));
        foreach (var group in NativeReadCheck.Sites(source, file).GroupBy(site => site.Owner))
        {
            var site = group.First();
            Console.WriteLine($"{Path.GetRelativePath(nativeRoot, file)}:{source.Lines.GetLineFromPosition(site.Span.Start).LineNumber + 1}\t{site.Owner}\t{string.Join(',', site.Declarations)}\t{string.Join(',', group.Select(item => item.Family).Distinct())}\t{group.Count()}");
        }
    }
    return;
}

await Check("unmarked aliased registry", """
using R = Microsoft.Win32.Registry;
class C { object? Read() => R.GetValue("path", "value", null); }
""", "RMR006");
await Check("comments and strings are not calls", """
class C { string Read() => "Registry.GetValue(abc)"; /* X86Base.CpuId(0,0); */ }
""", "");
await Check("class mark cannot exempt new method", """
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
class C { object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR006");
await Check("marked fixed read", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "");
await Check("event needs event", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven)] void Read() {} }
""", "RMR002");
await Check("invalid enum", """
class C { [ResourceManager.ReadWrite.RuntimeRead((ResourceManager.ReadWrite.ReadVolatilityKind)9)] void Read() {} }
""", "RMR004");
await Check("native source declaration needs event", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, " ")] public const string Source = "source"; }
""", "RMR002");
await Check("native source declaration invalid enum", """
class C { [ResourceManager.ReadWrite.RuntimeRead((ResourceManager.ReadWrite.ReadVolatilityKind)9)] public const string Source = "source"; }
""", "RMR004");
await Check("capture through helper to fixed", """
class C { void Capture() => Helper(); void Helper() => Fixed();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "RMR003");
await Check("capture interface dispatch reaches fixed implementation", """
interface I { void Read(); }
class R : I { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] public void Read() {} }
class C { void Capture(I reader) => reader.Read(); }
""", "RMR003");
await Check("capture recreates fixed owner", """
class R { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] public R() {} }
class C { object Capture() => new R(); }
""", "RMR003");
await Check("binding a generic transport delegate does not execute it", """
class C { System.Action Create() => Query;
[ResourceManager.ReadWrite.RuntimeReadTransport] void Query() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "");
await Check("stored callback ownership starts at invocation", """
class C { System.Action read; public C() { read = Query; }
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Read() => read();
[ResourceManager.ReadWrite.RuntimeReadTransport] void Query() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "");
await Check("unclassified owner cannot hide behind stored transport callback", """
class C { System.Action read; public C() { read = Query; } void Read() => read();
[ResourceManager.ReadWrite.RuntimeReadTransport] void Query() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR001");
await Check("stored fixed callback is reached from capture", """
class C { System.Action read; public C() { read = Fixed; } void Capture() => read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "RMR003");
await Check("local fixed callback is reached from capture", """
class C { void Capture() { System.Action read = () => Fixed(); read(); }
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "RMR003");
await Check("property fixed callback is reached from capture", """
class R { public System.Action Read { get; init; } = () => {}; }
class C { R r = new() { Read = Fixed }; void Capture() => r.Read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] static void Fixed() {} }
""", "RMR003");
await Check("cache owner cannot be recreated per capture", """
class R { System.Lazy<object> x = new(() => Fixed());
[ResourceManager.ReadWrite.RuntimeReadCache] public object Read() => x.Value;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] static object Fixed() => new(); }
class C { object Capture() => new R().Read(); }
""", "RMR005");
await Check("persistent original cache owner is accepted", """
class R { System.Lazy<object> x = new(() => Fixed());
[ResourceManager.ReadWrite.RuntimeReadCache] public object Read() => x.Value;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] static object Fixed() => new(); }
class C { R? reader; object Capture() => (reader ??= new R()).Read(); }
""", "");
foreach (var kind in new[] { "Sampled", "Fixed" })
{
    await Check($"existing source declaration controls cadence {kind}", $$"""
class Sources { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.{{kind}})] public const string Device = "device"; }
class C { void Capture() => Read();
[ResourceManager.ReadWrite.RuntimeReadSource(typeof(Sources), nameof(Sources.Device))]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", kind == "Sampled" ? "" : "RMR007");
}
await Check("source link cannot reference absent declaration", """
class Sources { public const string Device = "device"; }
class C { [ResourceManager.ReadWrite.RuntimeReadSource(typeof(Sources), nameof(Sources.Device))] void Read() {} }
""", "RMR004");
await Check("source link cannot reference missing field", """
class Sources { }
class C { [ResourceManager.ReadWrite.RuntimeReadSource(typeof(Sources), "Missing")] void Read() {} }
""", "RMR004");
await Check("source link must reference existing source string declaration", """
class Sources { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] public const int Device = 3; }
class C { [ResourceManager.ReadWrite.RuntimeReadSource(typeof(Sources), nameof(Sources.Device))] void Read() {} }
""", "RMR004");
await Check("local kind cannot override existing source volatility", """
class Sources { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] public const string Device = "device"; }
class C { [ResourceManager.ReadWrite.RuntimeReadSource(typeof(Sources), nameof(Sources.Device))]
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)] void Read() {} }
""", "RMR004");
await Check("transport cannot shadow a volatility declaration", """
class C { [ResourceManager.ReadWrite.RuntimeReadTransport]
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)] void Read() {} }
""", "RMR004");
await Check("COM reference release is not a source read", """
unsafe class C { private static nint Slot(nint instance,int index) => 0;
void Release(nint instance) => _ = ((delegate* unmanaged[Stdcall]<nint,uint>)Slot(instance,2))(instance); }
""", "");
await Check("other raw pointer in release method remains checked", """
unsafe class C { int Release(delegate* unmanaged<int> query) => query(); }
""", "RMR006");
await Check("explicit event capture name is not a periodic root", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "device.arrival")]
void CaptureOnArrival() => Fixed();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "");
await Check("neutral method description owns raw read", """
class C { [System.ComponentModel.Description("RuntimeRead: Fixed")]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "");
await Check("neutral class description cannot exempt raw method", """
[System.ComponentModel.Description("RuntimeRead: Fixed")]
class C { object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR006");
await Check("neutral event needs name", """
class C { [System.ComponentModel.Description("RuntimeRead: EventDriven(\"\")")] void Read() {} }
""", "RMR002");
await Check("neutral invalid marker", """
class C { [System.ComponentModel.Description("RuntimeRead: Poll")] void Read() {} }
""", "RMR004");
await Check("neutral duplicate cannot shadow typed marker", """
class C { [System.ComponentModel.Description("RuntimeRead: Sampled")]
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Read() {} }
""", "RMR004");
var neutral = CSharpCompilation.Create("Neutral", [CSharpSyntaxTree.ParseText("""
public static class NeutralReader { [System.ComponentModel.Description("RuntimeRead: Fixed")] public static object Read() => new(); }
""")], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using (var buffer = new MemoryStream())
{
    var emitted = neutral.Emit(buffer);
    if (!emitted.Success) throw new InvalidOperationException("Neutral metadata fixture failed to emit.");
    await Check("neutral imported metadata preserves fixed cadence", """
class C { void Capture() => NeutralReader.Read(); }
""", "RMR003", [MetadataReference.CreateFromImage(buffer.ToArray())]);
}
await Check("capture through lambda to fixed", """
class C { void Capture() => System.Threading.Tasks.Task.Run(() => Fixed());
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "RMR003");
await Check("capture through method group to fixed", """
class C { void Capture() => System.Threading.Tasks.Task.Run(Fixed);
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "RMR003");
await Check("named event cannot be polled", """
class C { void Sample() => Changed();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "device.changed")] void Changed() {} }
""", "RMR003");
await Check("fixed owner lazy factory runs once", """
class C { System.Lazy<object>? value; object Capture() => (value ??= new System.Lazy<object>(() => Fixed())).Value;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] static object Fixed() => new(); }
""", "");
await Check("new lazy per capture does not exempt fixed read", """
class C { object Capture() => new System.Lazy<object>(() => Fixed()).Value;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] object Fixed() => new(); }
""", "RMR003");
await Check("property call cannot hide fixed read", """
class C { object Capture() => Value;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] object Value => new(); }
""", "RMR003");
await Check("transport cannot hide missing owner", """
class C { void Owner() => Query();
[ResourceManager.ReadWrite.RuntimeReadTransport] void Query() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR001");
await Check("classified owner calls transport", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Owner() => Query();
[ResourceManager.ReadWrite.RuntimeReadTransport] void Query() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "");
await Check("tested cache boundary blocks only cadence traversal", """
class C { void Capture() => Cached();
[ResourceManager.ReadWrite.RuntimeReadCache] void Cached() => Fixed();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)] void Fixed() {} }
""", "");
await Check("cache marker never exempts raw reads", """
class C { [ResourceManager.ReadWrite.RuntimeReadCache] object? Cached() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR006");
await Check("cache marker cannot exempt a declared physical read", """
class C { void Capture() => Cached();
[ResourceManager.ReadWrite.RuntimeReadCache]
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
object? Cached() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR007");
foreach (var (entry, category) in new[] { ("SetupDiGetDeviceRegistryPropertyW", "SetupAPI"), ("nvmlDeviceGetCount_v2", "NVML"),
    ("GetSystemFirmwareTable", "ACPI"), ("ResourceManagerAdlxRead", "ADLX"), ("CreateDXGIFactory1", "DXGI"), ("read_cpuid", "CPUID"),
    ("CM_Get_DevNode_Registry_PropertyW", "ConfigMgr"), ("CM_Locate_DevNodeW", "ConfigMgr"), ("IsProcessorFeaturePresent", "CPU OS"), ("RegGetValueW", "registry"),
    ("GetLogicalProcessorInformationEx", "CPU topology"), ("GetSystemCpuSetInformation", "CPU sets") })
{
    await Check($"native alias {category}", $$"""
class C { void Read() => Renamed();
[System.Runtime.InteropServices.DllImport("{{(category == "ADLX" ? "ResourceManager.AdlxBridge.dll" : "native.dll")}}", EntryPoint="{{entry}}")] static extern void Renamed(); }
""", "RMR006");
}
await Check("CPUID intrinsic", """
class C { void Read() => System.Runtime.Intrinsics.X86.X86Base.CpuId(0,0); }
""", "RMR006");
await Check("unmanaged function pointer", """
unsafe class C { int Read(delegate* unmanaged<int> api) => api(); }
""", "RMR006");
Console.WriteLine($"Passed {assertions} semantic analyzer fixtures.");
Native("unmarked native API", "void Read() { RegQueryValueExW(key); }", "RMR101");
Native("native declaration", "// RuntimeRead: EventDriven(\"settings.changed\")\nvoid Read() { RegQueryValueExW(key); }", "");
Native("native comment/string handling", "void Read() { /* RegGetValueW(key); */ const char* x = \"SetupDiGetClassDevsW(0)\"; }", "");
Native("native raw string handling", "void Read() { const char* x = R\"raw(RegGetValueW(0); \"quoted\")raw\"; }", "");
Native("native class mark is not function declaration", "// RuntimeRead: Fixed\nclass C { void Read() { RegGetValueW(0); } };", "RMR101");
Native("new native method does not inherit sibling mark", "// RuntimeRead: Fixed\nvoid First() { RegGetValueW(0); }\nvoid Second() { RegGetValueW(0); }", "RMR101");
Native("native empty event", "// RuntimeRead: EventDriven(\" \")\nvoid Read() { RegGetValueW(0); }", "RMR102");
Native("native invalid declaration", "// RuntimeRead: Unknown\nvoid Read() { RegGetValueW(0); }", "RMR104");
Native("native duplicate declaration", "// RuntimeRead: Fixed\n// RuntimeRead: Sampled\nvoid Read() { RegGetValueW(0); }", "RMR104");
Native("native transport never silently passes", "// RuntimeRead: Transport\nvoid Read() { RegGetValueW(0); RegQueryValueExW(0); }", "RMR103,RMR103");
Native("native fake marker in string", "const char* marker = \"// RuntimeRead: Fixed\";\nvoid Read() { RegGetValueW(0); }", "RMR101");
Native("native extern declaration is not a call", "extern int RegGetValueW(int);", "");
Native("native WMI call", "void Read() { service->ExecQuery(query); }", "RMR101");
Native("native UTF8 raw string", "void Read() { const char* x = u8R\"raw(RegGetValueW(0); \"quoted\")raw\"; }", "");
Native("ADLX named vtable getter", "void Read() { gpu->pVtbl->GPUUsage(gpu, out); }", "RMR101", "/repo/AdlxBridge/source.inc");
Native("ADLX cast function pointer", "typedef int (*RmGetInt)(void*, int*); void Read() { ((RmGetInt)object->slots[4])(object, out); }", "RMR101", "/repo/AdlxBridge/source.inc");
Native("ADLX typed pointer alias", "typedef int (*ReadFn)(void*); void Read(ReadFn query) { query(object); }", "RMR101", "/repo/AdlxBridge/source.inc");
Native("ADLX release is not source acquisition", "void Release() { gpu->pVtbl->Release(gpu); }", "", "/repo/AdlxBridge/source.inc");
Native("Zig function and multiline string", "// RuntimeRead: Fixed\npub fn Read() void {\n\\\\ RegGetValueW(0);\n _ = IsProcessorFeaturePresent(0); }", "");
Native("Zig inline error return set within struct", """
const ClockSample = struct {
    // RuntimeRead: Sampled
    pub fn read() error{ClockSamplingFailed}!ClockSample {
        _ = QueryPerformanceCounter(0);
        _ = GetSystemTimePreciseAsFileTime(0);
    }
};
""", "");
Native("Zig error set cannot mark sibling function", """
const ClockSample = struct {
    // RuntimeRead: Sampled
    pub fn read() error{ClockSamplingFailed}!ClockSample { _ = QueryPerformanceCounter(0); }
    pub fn other() error{ClockSamplingFailed}!ClockSample { _ = QueryPerformanceCounter(0); }
};
""", "RMR101");
Console.WriteLine($"Passed {assertions} total semantic/native source fixtures.");

foreach (var rootName in new[] { "Capture", "Collect", "Sample", "Request" })
    foreach (var kind in new[] { "Fixed", "EventDriven" })
        await Check($"proven {kind} physical read per {rootName}", $$"""
class C { object? {{rootName}}() => Helper(); object? Helper() => Read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.{{kind}}{{(kind == "EventDriven" ? ", \"device.changed\"" : "")}})]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR007");
await Check("conditional owner remains unresolved", """
class C { object? Capture(bool requested) => requested ? Read() : null;
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR003");
await Check("read once guard needs composition proof", """
class C { object? cached; object? Capture() => Read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
object? Read() { if (cached is null) cached = Microsoft.Win32.Registry.GetValue("path", "value", null); return cached; } }
""", "RMR003");
await Check("iterator invocation does not execute physical acquisition", """
class C { System.Collections.Generic.IEnumerable<object?> Capture() => Read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
System.Collections.Generic.IEnumerable<object?> Read() { yield return Microsoft.Win32.Registry.GetValue("path", "value", null); } }
""", "RMR003");
await Check("mixed transport route remains unresolved", """
class C { object? Capture() => Mixed();
[ResourceManager.ReadWrite.RuntimeReadTransport] object? Mixed() => Read();
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
""", "RMR001,RMR003");
await Check("interface route is possible rather than proved execution", """
interface I { object? Read(); }
class R : I { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
public object? Read() => Microsoft.Win32.Registry.GetValue("path", "value", null); }
class C { object? Capture(I reader) => reader.Read(); }
""", "RMR003");
await Check("fixed raw pointer repeats", """
unsafe class C { int Capture(delegate* unmanaged<int> api) => Read(api);
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
int Read(delegate* unmanaged<int> api) => api(); }
""", "RMR007");
await Check("explicit sampling round repeats fixed raw property", """
class C { [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
int Round(Microsoft.Win32.RegistryKey key) => Read(key);
[ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
int Read(Microsoft.Win32.RegistryKey key) => key.ValueCount; }
""", "RMR007");
foreach (var api in new[] { "QueryPerformanceCounter", "QueryPerformanceFrequency", "GetSystemTimeAsFileTime", "GetSystemTimePreciseAsFileTime",
    "OpenTraceW", "ProcessTrace", "GetLogicalDrives", "QueryDosDeviceW", "GetTickCount64", "GetEnvironmentVariableW" })
{
    Native($"new ETW primary API {api}", $"void Read() {{ {api}(0); }}", "RMR101");
    Native($"marked ETW primary API {api}", $"// RuntimeRead: Sampled\nvoid Read() {{ {api}(0); }}", "");
}
await CheckNativeModes("unmarked native always error", "void Read() { RegGetValueW(0); }", "RMR101");
await CheckNativeModes("native mixed transport changes severity", "// RuntimeRead: Transport\nvoid Read() { RegGetValueW(0); }", "RMR103");
Console.WriteLine($"Passed {assertions} fixtures including normal/strict severity, unmarked primary and proven repetition gates.");

void Native(string name, string source, string expected, string path = "fixture.cpp")
{
    var actual = string.Join(',', NativeReadCheck.Analyze(Microsoft.CodeAnalysis.Text.SourceText.From(source), path).Select(diagnostic => diagnostic.Id).Order());
    if (actual != expected) throw new InvalidOperationException($"{name}: expected [{expected}], got [{actual}]");
    assertions++;
}

async Task Check(string name, string source, string expected, MetadataReference[]? extraReferences = null)
{
    var compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(declaration)], references.Cast<MetadataReference>().Concat(extraReferences ?? []),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
    var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0) throw new InvalidOperationException($"{name}: fixture compiler errors: {string.Join("; ", errors.Select(value => value.ToString()))}");
    await CheckModes(name, compilation, expected, []);
}

async Task CheckNativeModes(string name, string source, string expected)
{
    var compilation = CSharpCompilation.Create("NativeFixture", [CSharpSyntaxTree.ParseText("class C {}")], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    await CheckModes(name, compilation, expected, [new NativeFixture(source)]);
}

async Task CheckModes(string name, CSharpCompilation compilation, string expected, ImmutableArray<AdditionalText> native)
{
    foreach (var strict in new[] { false, true })
    foreach (var promoteOtherWarnings in new[] { false, true })
    {
        // Mirrors WarningsNotAsErrors/WarningsAsErrors in Directory.Build.targets,
        // including a project which promotes all its other warnings to errors.
        var severities = new[] { "RMR001", "RMR003", "RMR103" }.ToImmutableDictionary(
            id => id, _ => strict ? ReportDiagnostic.Error : ReportDiagnostic.Warn).Add("RMR000", ReportDiagnostic.Warn);
        var options = compilation.Options.WithGeneralDiagnosticOption(promoteOtherWarnings ? ReportDiagnostic.Error : ReportDiagnostic.Default)
            .WithSpecificDiagnosticOptions(severities);
        var diagnostics = await compilation.WithOptions(options).WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new RuntimeReadAnalyzer()), new AnalyzerOptions(native)).GetAnalyzerDiagnosticsAsync();
        var findings = diagnostics.Where(diagnostic => diagnostic.Id != "RMR000").ToArray();
        var actual = string.Join(',', findings.Select(diagnostic => diagnostic.Id).Order());
        if (actual != expected) throw new InvalidOperationException($"{name} strict={strict}: expected [{expected}], got [{actual}]: {string.Join("; ", diagnostics)}");
        foreach (var finding in findings)
        {
            var unresolved = finding.Id is "RMR001" or "RMR003" or "RMR103";
            var severity = !strict && unresolved ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
            if (finding.Severity != severity) throw new InvalidOperationException($"{name} strict={strict}: {finding.Id} should be {severity}, got {finding.Severity}");
        }
        var summary = diagnostics.Single(diagnostic => diagnostic.Id == "RMR000");
        var count = findings.Count(diagnostic => diagnostic.Id is "RMR001" or "RMR003" or "RMR103");
        if (!summary.GetMessage().Contains($"unresolved={count} ", StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: wrong visible count: {summary}");
        assertions++;
    }
}

sealed class NativeFixture(string source) : AdditionalText
{
    public override string Path => "fixture.cpp";
    public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default)
        => Microsoft.CodeAnalysis.Text.SourceText.From(source);
}
