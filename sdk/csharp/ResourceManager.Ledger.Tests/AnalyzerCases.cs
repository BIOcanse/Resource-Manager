using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Analyzers;

/// <summary>Source-compilation cases; no Native library, owner instance or activity marker is required.</summary>
internal static class AnalyzerCases
{
    private const string Imports = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Concurrent;
        using System.Threading.Tasks;
        using ResourceManager.Ledger;
        using ResourceManager.Ledger.Identity;
        using L = ResourceManager.Ledger.LedgerLease<ResourceManager.Ledger.Identity.LedgerUid128>;

        """;
    private const string SyncOwner = """
        [LedgerLeaseScope] class Owner : IDisposable {
            private L lease;
            public Owner(L lease) { this.lease = lease; }
            public void Dispose() => lease.Dispose();
        }
        """;

    private sealed record Case(string Name, string Source, int Storage = 0, int InvalidScope = 0,
        string AssemblyName = "BusinessCaller", string? Operation = null, string? PositionalProperty = null);

    /// <summary>The primary gate adds the returned case count to its own count.</summary>
    internal static async Task<int> RunAsync()
    {
        Case[] cases =
        [
            Body("local using", "void Run(L lease) { using (lease) { lease.Span[0] = 1; } }"),
            Body("async run local across await", "async Task Run(LedgerOwner<LedgerUid128> owner) { await using var lease = owner.Use(default).Lease; await Task.Yield(); _ = lease!.Length; }"),
            Body("async finally", "async Task Run(L lease) { try { await Task.Yield(); _ = lease.Length; } finally { await lease.DisposeAsync(); } }"),
            Body("explicit field", "L value = null!;", 1),
            Body("static field", "static L? value;", 1),
            Body("nullable field", "LedgerLease<LedgerUid128>? value;", 1),
            Body("auto property", "public L Value { get; set; } = null!;", 1),
            Body("get-only auto property", "public L Value { get; } = null!;", 1),
            Body("private auto property", "private L Value { get; set; } = null!;", 1),
            Body("generic nested container", "Dictionary<string, List<L[]>> value = [];", 1),
            Body("array field", "L[] value = [];", 1),
            Body("tuple field", "(int, L?) value;", 1),
            Body("lazy value field", "Lazy<L>? value;", 1),
            Body("use result", "LedgerUseResult<LedgerUid128> value;", 1),
            Body("push result", "LedgerPushResult<LedgerUid128> value;", 1),
            Body("identity and workspace", "LedgerUid128 uid; LedgerWorkspace? workspace; Memory<byte> view;"),
            Body("factory signatures", "Func<L>? factory; Action<L>? callback; Func<Task<L>>? asyncFactory;"),
            Body("computed return property", "public L Value => throw new Exception();"),
            Body("ordinary parameter and return", "L Identity(L lease) => lease;"),
            new("interface get signature", "interface Sample { L Value { get; } }"),
            new("interface get set signature", "interface Sample { L Value { get; set; } }"),
            new("abstract signature", "abstract class Sample { public abstract L Value { get; set; } }"),
            new("implemented abstract auto property", "abstract class Base { public abstract L Value { get; set; } } class Sample : Base { public override L Value { get; set; } = null!; }", 1),
            new("default interface computed body", "interface Sample { L Value => throw new Exception(); }"),
            new("record storage", "record Sample(L Value);", 1, PositionalProperty: "Value"),
            new("record struct storage", "record struct Sample(L Value);", 1, PositionalProperty: "Value"),
            new("readonly record struct storage", "readonly record struct Sample(L Value);", 1, PositionalProperty: "Value"),
            new("record array storage", "record Sample(L[] Value);", 1, PositionalProperty: "Value"),
            new("record explicit computed property", "record Sample(L Value) { public L Value => throw new Exception(); }"),
            new("class primary capture", "class Sample(L lease) { public ulong Length => lease.Length; public void Close() => lease.Dispose(); }", 1, Operation: "parameter"),
            new("struct primary capture", "struct Sample(L lease) { public ulong Length => lease.Length; }", 1, Operation: "parameter"),
            new("primary capture through method local initializer", "class Sample(L lease) { public ulong Length() { var length = lease.Length; return length; } }", 1, Operation: "parameter"),
            new("primary metadata copy", "class Sample(L lease) { public ulong Length { get; } = lease.Length; }", Operation: "parameter"),
            new("primary readonly metadata field", "class Sample(L lease) { private readonly ulong length = lease.Length; }"),
            new("primary base metadata", "class Base(ulong length) { } class Sample(L lease) : Base(lease.Length) { }"),
            new("primary nameof is not capture", "class Sample(L lease) { public string Name => nameof(lease); }"),
            new("unannotated disposable primary capture", "class Sample(L lease) : IDisposable { public void Dispose() => lease.Dispose(); }", 1),
            new("unannotated disposable field", "class Sample : IDisposable { private L lease = null!; public void Dispose() => lease.Dispose(); }", 1),
            new("owning private field", SyncOwner),
            new("owning private primary capture", "[LedgerLeaseScope] class Sample(L lease) : IDisposable { public ulong Length => lease.Length; public void Dispose() => lease.Dispose(); }"),
            new("owning async primary capture", "[LedgerLeaseScope] class Sample(L lease) : IAsyncDisposable { public ValueTask DisposeAsync() => lease.DisposeAsync(); }"),
            new("owning async private field", "[LedgerLeaseScope] class Sample : IAsyncDisposable { private L lease = null!; public ValueTask DisposeAsync() => lease.DisposeAsync(); }"),
            new("owning explicit disposal interface", "[LedgerLeaseScope] class Sample(L lease) : IDisposable { void IDisposable.Dispose() => lease.Dispose(); }"),
            new("owning struct primary capture", "[LedgerLeaseScope] struct Sample(L lease) : IDisposable { public void Dispose() => lease.Dispose(); }"),
            new("owning both interfaces", "[LedgerLeaseScope] class Sample(L lease) : IDisposable, IAsyncDisposable { public void Dispose() => lease.Dispose(); public ValueTask DisposeAsync() => lease.DisposeAsync(); }"),
            new("owner missing disposal", "[LedgerLeaseScope] class Sample { }", InvalidScope: 1),
            new("method name alone is not interface", "[LedgerLeaseScope] class Sample { public void Dispose() { } }", InvalidScope: 1),
            new("lookalike disposal interface", "namespace Other { interface IDisposable { void Dispose(); } [LedgerLeaseScope] class Sample : IDisposable { public void Dispose() { } } }", InvalidScope: 1),
            new("invalid owner no private exemption", "[LedgerLeaseScope] class Sample { private L lease = null!; }", 1, 1),
            new("invalid owner primary capture", "[LedgerLeaseScope] class Sample(L lease) { public ulong Length => lease.Length; }", 1, 1),
            new("lookalike scope attribute no exemption", "namespace Other { class LedgerLeaseScopeAttribute : Attribute { } } [Other.LedgerLeaseScope] class Sample : IDisposable { private L lease = null!; public void Dispose() => lease.Dispose(); }", 1),
            new("owning private collection not credential", "[LedgerLeaseScope] class Sample : IDisposable { private List<L> leases = []; public void Dispose() { } }", 1),
            new("owning public field forbidden", "[LedgerLeaseScope] class Sample : IDisposable { public L lease = null!; public void Dispose() => lease.Dispose(); }", 1),
            new("owning protected field forbidden", "[LedgerLeaseScope] class Sample : IDisposable { protected L lease = null!; public void Dispose() => lease.Dispose(); }", 1),
            new("owning static field forbidden", "[LedgerLeaseScope] class Sample : IDisposable { private static L lease = null!; public void Dispose() => lease.Dispose(); }", 1),
            new("owning public auto property forbidden", "[LedgerLeaseScope] class Sample : IDisposable { public L Lease { get; set; } = null!; public void Dispose() => Lease.Dispose(); }", 1),
            new("owning private auto property forbidden", "[LedgerLeaseScope] class Sample : IDisposable { private L Lease { get; set; } = null!; public void Dispose() => Lease.Dispose(); }", 1),
            new("owning record still public storage", "[LedgerLeaseScope] record Sample(L Lease) : IDisposable { public void Dispose() => Lease.Dispose(); }", 1),
            new("owning adapter business field", SyncOwner + "class Sample { Owner? value; }", 1),
            new("owning adapter business property", SyncOwner + "class Sample { Owner? Value { get; set; } }", 1),
            new("owning adapter container", SyncOwner + "class Sample { List<Owner> values = []; }", 1),
            new("owning adapter local", SyncOwner + "class Sample { void Run(L lease) { using var wrapped = new Owner(lease); } }"),
            new("inherited attribute private storage", SyncOwner + "class Derived(L lease) : Owner(lease) { private L extra = lease; }"),
            new("inherited attribute business field", SyncOwner + "class Derived(L lease) : Owner(lease) { } class Sample { Derived? value; }", 1),
            Body("erased assignment", "object? value; void Run(L lease) { value = lease; }", 1),
            Body("converted erased assignment", "object? value; void Run(L lease) { value = (object)(IDisposable)lease; }", 1),
            Body("erased initializer", "object value = Create(); static L Create() => throw new Exception();", 1),
            new("primary erased field initializer", "class Sample(L lease) { object value = lease; }", 1),
            new("primary closure field initializer", "class Sample(L lease) { Action saved = () => { _ = lease.Length; }; }", 1),
            new("primary closure property initializer", "class Sample(L lease) { object Saved { get; } = (Action)(() => { _ = lease.Length; }); }", 1),
            new("primary collection field initializer", "class Sample(L lease) { List<object> saved = new() { lease }; }", 1),
            Body("erased local alias", "object? value; void Run(L lease) { object alias = lease; object copy = alias; value = copy; }", 1),
            Body("metadata is not lease", "object? value; void Run(L lease) { object length = lease.Length; value = length; }"),
            Body("lambda field escape", "Action? saved; void Run(L lease) { saved = () => { _ = lease.Length; }; }", 1, "lambda"),
            Body("lambda property escape", "Action? Saved { get; set; } void Run(L lease) { Saved = () => { _ = lease.Length; }; }", 1),
            Body("lambda object escape", "object? saved; void Run(L lease) { saved = (object)(Action)(() => { _ = lease.Length; }); }", 1),
            Body("lambda alias escape", "object? saved; void Run(L lease) { Action action = () => { _ = lease.Length; }; var alias = action; saved = alias; }", 1),
            Body("erased captured local", "Action? saved; void Run(L lease) { object alias = lease; saved = () => { _ = alias; }; }", 1),
            Body("local function escape", "Action? saved; void Run(L lease) { void Use() { _ = lease.Length; } saved = Use; }", 1, "local function"),
            Body("local function alias escape", "object? saved; void Run(L lease) { void Use() { _ = lease.Length; } Action action = Use; saved = action; }", 1),
            Body("indirect local function escape", "Action? saved; void Run(L lease) { void Use() { _ = lease.Length; } void Relay() => Use(); saved = Relay; }", 1),
            Body("lambda calling local function escape", "Action? saved; void Run(L lease) { void Use() { _ = lease.Length; } saved = () => Use(); }", 1),
            Body("local lambda allowed", "void Run(L lease) { Action action = () => { _ = lease.Length; }; action(); }"),
            Body("local function allowed", "void Run(L lease) { void Use() { _ = lease.Length; } Use(); }"),
            Body("parameter-only callback allowed", "Action<L>? saved; void Run() { saved = lease => { _ = lease.Length; }; }"),
            Body("factory with local lease allowed", "Action<L>? saved; void Run() { saved = lease => { void Use() { _ = lease.Length; } Use(); }; }"),
            Body("lease created inside callback allowed", "Func<L>? saved; void Run() { saved = () => Create(); } static L Create() => throw new Exception();"),
            Body("retained list Add", "readonly List<object> values = []; void Run(L lease) { values.Add(lease); }", 1, "invocation"),
            Body("retained list Add conversion", "readonly List<object> values = []; void Run(L lease) { values.Add((object)lease); }", 1),
            Body("retained property list", "List<object> Values { get; } = []; void Run(L lease) { Values.Add(lease); }", 1),
            Body("field container alias", "readonly List<object> values = []; void Run(L lease) { var first = values; var second = first; second.Add(lease); }", 1),
            Body("assigned container alias", "readonly List<object> values = []; void Run(L lease) { List<object> alias; alias = values; alias.Add(lease); }", 1),
            Body("retained array index", "readonly object[] values = new object[1]; void Run(L lease) { values[0] = lease; }", 1),
            Body("array alias index", "readonly object[] values = new object[1]; void Run(L lease) { var alias = values; alias[0] = (object)lease; }", 1),
            Body("retained list index", "readonly List<object> values = [new object()]; void Run(L lease) { values[0] = lease; }", 1),
            Body("retained dictionary index", "readonly Dictionary<string, object> values = []; void Run(L lease) { values[\"x\"] = lease; }", 1),
            Body("retained queue", "readonly Queue<object> values = new(); void Run(L lease) { values.Enqueue(lease); }", 1),
            Body("retained stack", "readonly Stack<object> values = new(); void Run(L lease) { values.Push(lease); }", 1),
            Body("retained TryAdd", "readonly ConcurrentDictionary<int, object> values = new(); void Run(L lease) { _ = values.TryAdd(0, lease); }", 1),
            Body("retained collection closure", "readonly List<object> values = []; void Run(L lease) { values.Add((Action)(() => { _ = lease.Length; })); }", 1),
            Body("local container allowed", "void Run(L lease) { var values = new List<object>(); values.Add(lease); }"),
            Body("local array allowed", "void Run(L lease) { object[] values = new object[1]; values[0] = lease; }"),
            Body("local indexer allowed", "void Run(L lease) { var values = new Dictionary<string, object>(); values[\"x\"] = lease; }"),
            Body("local container then escapes", "object? saved; void Run(L lease) { var values = new List<object>(); values.Add(lease); saved = values; }", 1),
            Body("array initializer escapes", "object? saved; void Run(L lease) { saved = new object[] { lease }; }", 1),
            Body("collection initializer escapes", "object? saved; void Run(L lease) { saved = new List<object> { lease }; }", 1),
            Body("delegate compound subscription escape", "Action? saved; void Run(L lease) { saved += () => { _ = lease.Length; }; }", 1),
            Body("local delegate compound allowed", "void Run(L lease) { Action? action = null; action += () => { _ = lease.Length; }; action(); }"),
            new("event subscription escape", "class Source { public event EventHandler? Changed; } class Sample { void Run(L lease, Source source) { source.Changed += (_, _) => { _ = lease.Length; }; } }", 1),
            new("event subscription metadata allowed", "class Source { public event EventHandler? Changed; } class Sample { void Run(L lease, Source source) { ulong length = lease.Length; source.Changed += (_, _) => { _ = length; }; } }"),
            Body("coalesce assignment escape", "object? saved; void Run(L lease) { saved ??= lease; }", 1),
            Body("local coalesce assignment allowed", "void Run(L lease) { object? local = null; local ??= lease; _ = local; }"),
            Body("deconstruction assignment escape", "object? saved; void Run(L lease) { int count; (saved, count) = (lease, 1); }", 1),
            Body("deconstruction pairs elements", "object? saved; void Run(L lease) { object local; (local, saved) = (lease, lease.Length); _ = local; }"),
            Body("collection expression escape", "List<object> saved = []; void Run(L lease) { saved = [lease]; }", 1),
            Body("local collection expression allowed", "void Run(L lease) { List<object> local = [lease]; _ = local.Count; }"),
            Body("retained GetOrAdd", "readonly ConcurrentDictionary<int, object> values = new(); void Run(L lease) { _ = values.GetOrAdd(0, lease); }", 1),
            Body("local GetOrAdd allowed", "void Run(L lease) { var values = new ConcurrentDictionary<int, object>(); _ = values.GetOrAdd(0, lease); }"),
            Body("retained AddOrUpdate", "readonly ConcurrentDictionary<int, object> values = new(); void Run(L lease) { _ = values.AddOrUpdate(0, lease, (_, _) => lease); }", 1),
            Body("local AddOrUpdate allowed", "void Run(L lease) { var values = new ConcurrentDictionary<int, object>(); _ = values.AddOrUpdate(0, lease, (_, _) => lease); }"),
            Body("Interlocked exchange escape", "object? saved; void Run(L lease) { _ = System.Threading.Interlocked.Exchange(ref saved, lease); }", 1),
            Body("local Interlocked exchange allowed", "void Run(L lease) { object? local = null; _ = System.Threading.Interlocked.Exchange(ref local, lease); _ = local; }"),
            Body("Interlocked compare exchange escape", "object? saved; void Run(L lease) { _ = System.Threading.Interlocked.CompareExchange(ref saved, lease, null); }", 1),
            Body("Interlocked comparand is not stored", "object? saved; void Run(L lease) { _ = System.Threading.Interlocked.CompareExchange(ref saved, null, lease); }"),
            new("owning wrapper closure escape", SyncOwner + "class Sample { Action? saved; void Run(Owner owner) { saved = () => owner.Dispose(); } }", 1),
            new("owning wrapper closed delegate escape", SyncOwner + "class Sample { Action? saved; void Run(Owner owner) { saved = owner.Dispose; } }", 1),
            new("owning wrapper this capture", "[LedgerLeaseScope] class Sample(L lease) : IDisposable { Action? saved; public void Run() { saved = () => lease.Dispose(); } public void Dispose() => lease.Dispose(); }", 1),
            new("owning wrapper erased collection", SyncOwner + "class Sample { List<object> saved = []; void Run(Owner owner) { saved.Add(owner); } }", 1),
            new("scoped interface field", "[LedgerLeaseScope] interface IReader : IDisposable { } class Sample { IReader? held; }", 1),
            new("scoped interface factory signature", "[LedgerLeaseScope] interface IReader : IDisposable { } class Sample { IReader Acquire() => throw new Exception(); }"),
            new("interface inherited implementation private credential", "[LedgerLeaseScope] interface IReader : IDisposable { } interface IDerived : IReader { } sealed class Owner : IDerived { private L lease = null!; public void Dispose() => lease.Dispose(); } class Sample { IDerived? held; }", 1),
            new("scoped interface property signature is not storage", "[LedgerLeaseScope] interface IReader : IDisposable { L Lease { get; } }"),
            new("scoped interface must declare disposal", "[LedgerLeaseScope] interface IReader { }", InvalidScope: 1),
            Body("field keyword backing storage", "public L? Value { get => field; set => field=value; }", 1),
            new("static interface automatic backing storage", "interface IReader { static L? Value { get; set; } }", 1),
            new("static interface computed property allowed", "interface IReader { static L Value => throw new Exception(); }"),
            new("SDK assembly exemption", "class Sample { L? value; }", AssemblyName: "ResourceManager.Ledger"),
            new("SDK namespace grants no exemption", "namespace ResourceManager.Ledger { class Sample { L? value; } }", 1),
        ];

        ImmutableArray<MetadataReference> references = RuntimeReferences();
        foreach (Case item in cases)
        {
            var tree = CSharpSyntaxTree.ParseText(Imports + item.Source,
                new CSharpParseOptions(LanguageVersion.Preview), path: item.Name + ".cs");
            var compilation = CSharpCompilation.Create(item.AssemblyName, [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            Diagnostic[] errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(item.Name + ": compiler errors: " + string.Join("; ", errors.Select(d => d.ToString())));
            VerifyRoslynFacts(item, compilation, tree);
            ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new ScopedLeaseAnalyzer()]).GetAnalyzerDiagnosticsAsync();
            if (diagnostics.Any(d => d.Id is not (ScopedLeaseAnalyzer.DiagnosticId or ScopedLeaseAnalyzer.InvalidScopeDiagnosticId)) ||
                diagnostics.Count(d => d.Id == ScopedLeaseAnalyzer.DiagnosticId) != item.Storage ||
                diagnostics.Count(d => d.Id == ScopedLeaseAnalyzer.InvalidScopeDiagnosticId) != item.InvalidScope ||
                diagnostics.Any(d => !d.Location.IsInSource))
                throw new InvalidOperationException(item.Name + $": expected storage={item.Storage}, invalid scope={item.InvalidScope}; actual: " +
                    string.Join("; ", diagnostics.Select(d => d.ToString())));
            string? storageParameter = item.PositionalProperty ?? (item.Operation == "parameter" && item.Storage > 0 ? "lease" : null);
            if (storageParameter is not null)
            {
                INamedTypeSymbol sample = compilation.GetTypeByMetadataName("Sample")!;
                IParameterSymbol parameter = sample.InstanceConstructors.SelectMany(constructor => constructor.Parameters)
                    .Single(parameter => parameter.Name == storageParameter);
                SyntaxNode parameterSyntax = parameter.DeclaringSyntaxReferences.Single().GetSyntax();
                if (!diagnostics.Any(d => d.Id == ScopedLeaseAnalyzer.DiagnosticId && d.Location.SourceSpan == parameterSyntax.Span))
                    throw new InvalidOperationException(item.Name + ": hidden storage was not diagnosed at its source parameter");
            }
            Console.WriteLine("PASS analyzer " + item.Name);
        }
        return cases.Length;
    }

    private static Case Body(string name, string body, int storage = 0, string? operation = null) =>
        new(name, "class Sample { " + body + " }", storage, Operation: operation);

    private static ImmutableArray<MetadataReference> RuntimeReferences()
    {
        string[] platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ??
            throw new InvalidOperationException("TPA metadata is unavailable")).Split(Path.PathSeparator);
        return platform.Append(typeof(LedgerOwner<>).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().ToImmutableArray();
    }

    private static void VerifyRoslynFacts(Case item, CSharpCompilation compilation, SyntaxTree tree)
    {
        // Assert real runtime metadata identity, rather than compiling lookalike lease stubs.
        INamedTypeSymbol lease = compilation.GetTypeByMetadataName("ResourceManager.Ledger.LedgerLease`1") ??
            throw new InvalidOperationException("SDK lease metadata missing");
        if (lease.ContainingAssembly.Identity.Name != typeof(LedgerOwner<>).Assembly.GetName().Name)
            throw new InvalidOperationException("Lease did not resolve to the SDK runtime");
        SemanticModel model = compilation.GetSemanticModel(tree);
        var pending = new Stack<SyntaxNode>();
        pending.Push(tree.GetRoot());
        bool operationFound = item.Operation is null;
        bool propertyFound = item.PositionalProperty is null;
        while (pending.Count != 0)
        {
            SyntaxNode syntax = pending.Pop();
            if (item.Operation is not null)
            {
                IOperation? operation = model.GetOperation(syntax);
                operationFound |= item.Operation switch
                {
                    "lambda" => operation is IAnonymousFunctionOperation,
                    "parameter" => operation is IParameterReferenceOperation,
                    "local function" => operation is ILocalFunctionOperation,
                    "invocation" => operation is IInvocationOperation,
                    _ => throw new InvalidOperationException("Unknown operation assertion"),
                };
            }
            if (syntax is RecordDeclarationSyntax record && model.GetDeclaredSymbol(record) is INamedTypeSymbol symbol)
                propertyFound |= symbol.GetMembers(item.PositionalProperty ?? "").OfType<IPropertySymbol>().Any(property =>
                    property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is ParameterSyntax));
            foreach (SyntaxNode child in syntax.ChildNodes()) pending.Push(child);
        }
        if (!operationFound || !propertyFound) throw new InvalidOperationException(item.Name + ": expected Roslyn symbol/operation was absent");
    }
}
