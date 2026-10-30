using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ResourceManager.Ledger.Analyzers;

/// <summary>Checks declared storage and intraprocedural escapes of SDK leases and owning adapters.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScopedLeaseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RML001";
    public const string InvalidScopeDiagnosticId = "RML002";
    private static readonly DiagnosticDescriptor Rule = new(DiagnosticId,
        "Keep the lease in the executing code scope",
        "Lease storage '{0}' outlives its code scope; use a local using/finally or await using",
        "Lifetime", DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Leases and owning adapters cannot be retained in application storage. An explicitly annotated disposing adapter may keep private underlying lease credentials.");
    private static readonly DiagnosticDescriptor InvalidScopeRule = new(InvalidScopeDiagnosticId,
        "An owning lease adapter must implement disposal",
        "Lease adapter '{0}' must implement System.IDisposable or System.IAsyncDisposable",
        "Lifetime", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, InvalidScopeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Only the SDK lifecycle assembly is exempt, never its namespace in a caller assembly.
            if (start.Compilation.AssemblyName == "ResourceManager.Ledger") return;
            var types = new LeaseTypeFacts(start.Compilation);
            start.RegisterSymbolAction(c => AnalyzeField(c, types), SymbolKind.Field);
            start.RegisterSymbolAction(c => AnalyzeProperty(c, types), SymbolKind.Property);
            start.RegisterSymbolAction(c => AnalyzeScope(c, types), SymbolKind.NamedType);
            start.RegisterSyntaxNodeAction(c => AnalyzeTypeStorage(c, types), SyntaxKind.ClassDeclaration,
                SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
            start.RegisterOperationBlockAction(c => new EscapeAnalysis(c, types).Analyze());
        });
    }

    private static void AnalyzeField(SymbolAnalysisContext context, LeaseTypeFacts types)
    {
        var field = (IFieldSymbol)context.Symbol;
        // Backing properties and source primary parameters are checked at their source syntax.
        // Async state-machine fields have no source field declaration and remain permitted.
        if (field.IsImplicitlyDeclared || !types.ContainsLease(field.Type) || types.IsPrivateCredential(field)) return;
        Report(context, field, Rule);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context, LeaseTypeFacts types)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (property.IsAbstract || !types.ContainsLease(property.Type)) return;
        // Compiler truth rather than accessor spelling: this also covers C#
        // field-backed properties and concrete static interface auto-properties.
        // Positional records are diagnosed once at their source parameter below.
        bool explicitProperty = property.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax(context.CancellationToken) is PropertyDeclarationSyntax);
        bool storage = explicitProperty && property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(field =>
            SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
        if (storage) Report(context, property, Rule);
    }

    private static void AnalyzeScope(SymbolAnalysisContext context, LeaseTypeFacts types)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (types.HasScopeAttribute(type) && !types.IsOwningScope(type)) Report(context, type, InvalidScopeRule);
    }

    private static void AnalyzeTypeStorage(SyntaxNodeAnalysisContext context, LeaseTypeFacts types)
    {
        var owner = (TypeDeclarationSyntax)context.Node;
        var reported = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<SyntaxNode>();
        pending.Push(owner);
        // One finite source-syntax walk per type, without entering nested type declarations.
        while (pending.Count != 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            SyntaxNode node = pending.Pop();
            if (node is TypeDeclarationSyntax && node != owner) continue;
            if (node is ParameterSyntax position && position.Parent?.Parent == owner && owner is RecordDeclarationSyntax &&
                context.SemanticModel.GetDeclaredSymbol(position, context.CancellationToken) is IParameterSymbol positional &&
                types.ContainsLease(positional.Type) && positional.ContainingType.GetMembers(positional.Name).OfType<IPropertySymbol>().Any(property =>
                    property.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == position.SyntaxTree && reference.Span == position.Span)))
            {
                if (reported.Add(positional)) context.ReportDiagnostic(Diagnostic.Create(Rule, position.GetLocation(), positional.Name));
            }
            if (node is IdentifierNameSyntax identifier &&
                context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is IParameterSymbol parameter &&
                types.ContainsLease(parameter.Type) &&
                parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is ParameterSyntax declaration &&
                declaration.Parent?.Parent == owner &&
                IsCapturedPrimaryReference(identifier, parameter, owner, context.SemanticModel, context.CancellationToken) &&
                !(types.IsOwningScope(parameter.ContainingType) && types.IsDirectLease(parameter.Type)) && reported.Add(parameter))
                context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.GetLocation(), parameter.Name));
            foreach (SyntaxNode child in node.ChildNodes()) pending.Push(child);
        }
    }

    private static bool IsCapturedPrimaryReference(IdentifierNameSyntax identifier, IParameterSymbol parameter,
        TypeDeclarationSyntax owner, SemanticModel model, CancellationToken cancellationToken)
    {
        if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameter)) return false;
        SyntaxNode? ancestor = identifier.Parent;
        bool instanceUse = false;
        while (ancestor is not null && ancestor != owner)
        {
            // Construction-time inputs do not require primary parameter instance storage.
            if (ancestor is PrimaryConstructorBaseTypeSyntax || ancestor is EqualsValueClauseSyntax
                {
                    Parent: PropertyDeclarationSyntax or VariableDeclaratorSyntax
                    {
                        Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax }
                    }
                }) return false;
            if (ancestor is InvocationExpressionSyntax call && model.GetOperation(call, cancellationToken) is INameOfOperation) return false;
            if (ancestor is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax) instanceUse = true;
            ancestor = ancestor.Parent;
        }
        return ancestor == owner && instanceUse;
    }

    private static void Report(SymbolAnalysisContext context, ISymbol symbol, DiagnosticDescriptor rule)
    {
        Location? location = symbol.Locations.FirstOrDefault(value => value.IsInSource) ??
            symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken).GetLocation();
        if (location is not null) context.ReportDiagnostic(Diagnostic.Create(rule, location, symbol.Name));
    }

    /// <summary>No member-field inference: exact SDK types, type arguments, arrays and inherited assertions.</summary>
    private sealed class LeaseTypeFacts(Compilation compilation)
    {
        private readonly INamedTypeSymbol? lease = compilation.GetTypeByMetadataName("ResourceManager.Ledger.LedgerLease`1");
        private readonly INamedTypeSymbol? use = compilation.GetTypeByMetadataName("ResourceManager.Ledger.LedgerUseResult`1");
        private readonly INamedTypeSymbol? push = compilation.GetTypeByMetadataName("ResourceManager.Ledger.LedgerPushResult`1");
        private readonly INamedTypeSymbol? scope = compilation.GetTypeByMetadataName("ResourceManager.Ledger.LedgerLeaseScopeAttribute");
        private readonly INamedTypeSymbol? disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        private readonly INamedTypeSymbol? asyncDisposable = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        private readonly INamedTypeSymbol? interlocked = compilation.GetTypeByMetadataName("System.Threading.Interlocked");

        /// <summary>Interlocked.Exchange/CompareExchange store their second argument into the ref first argument.</summary>
        public bool IsInterlockedStore(IMethodSymbol method) => method.Name is "Exchange" or "CompareExchange" &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, interlocked);

        public bool HasScopeAttribute(INamedTypeSymbol type)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var pending = new Stack<INamedTypeSymbol>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                INamedTypeSymbol current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (scope is not null && current.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, scope))) return true;
                if (current.BaseType is { } parent) pending.Push(parent);
                foreach (INamedTypeSymbol contract in current.Interfaces) pending.Push(contract);
            }
            return false;
        }

        public bool IsOwningScope(INamedTypeSymbol type) => HasScopeAttribute(type) && type.AllInterfaces.Any(contract =>
            SymbolEqualityComparer.Default.Equals(contract, disposable) || SymbolEqualityComparer.Default.Equals(contract, asyncDisposable));

        public bool IsPrivateCredential(IFieldSymbol field) => !field.IsStatic && field.DeclaredAccessibility == Accessibility.Private &&
            IsOwningScope(field.ContainingType) && IsDirectLease(field.Type);

        public bool IsDirectLease(ITypeSymbol type) => type is INamedTypeSymbol named &&
            (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, lease) || HasScopeAttribute(named));

        public bool ContainsLease(ITypeSymbol? type)
        {
            if (type is null) return false;
            var pending = new Stack<ITypeSymbol>();
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            pending.Push(type);
            // Each type symbol in the finite declared type-argument graph is visited once.
            while (pending.Count != 0)
            {
                ITypeSymbol current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (current is IArrayTypeSymbol array) { pending.Push(array.ElementType); continue; }
                if (current is not INamedTypeSymbol named) continue;
                if (IsDirectLease(named) || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, use) ||
                    SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, push)) return true;
                // A callback signature alone retains no instance of its argument/result types.
                if (named.TypeKind == TypeKind.Delegate) continue;
                foreach (ITypeSymbol argument in named.TypeArguments) pending.Push(argument);
            }
            return false;
        }
    }

    /// <summary>
    /// Flow-insensitive, per-operation-block alias propagation. No call summaries, heap graph or dynamic field map.
    /// Local containers/closures are allowed until a visible field/property/container escape is found.
    /// </summary>
    private sealed class EscapeAnalysis(OperationBlockAnalysisContext context, LeaseTypeFacts types)
    {
        private readonly List<IOperation> nodes = [];
        private readonly HashSet<ILocalSymbol> retained = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ILocalSymbol> carrying = new(SymbolEqualityComparer.Default);
        private readonly HashSet<IMethodSymbol> capturingFunctions = new(SymbolEqualityComparer.Default);
        private readonly HashSet<(SyntaxTree Tree, int Start, int Length)> reported = [];
        private readonly List<(IOperation Target, IOperation Value)> stores = [];

        public void Analyze()
        {
            var pending = new Stack<IOperation>(context.OperationBlocks);
            var seen = new HashSet<IOperation>();
            while (pending.Count != 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                IOperation node = pending.Pop();
                if (!seen.Add(node)) continue;
                nodes.Add(node);
                foreach (IOperation child in node.ChildOperations) pending.Push(child);
            }
            // Only three monotonic symbol sets grow; at most 3 * node count additions are possible.
            int remaining = checked(nodes.Count * 3 + 1);
            bool changed = true;
            while (changed && remaining-- > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                changed = false;
                foreach (IOperation node in nodes)
                {
                    if (node is ILocalFunctionOperation function && CapturesLease(function, function.Symbol))
                        changed |= capturingFunctions.Add(function.Symbol);
                    if (node is IVariableDeclaratorOperation variable && variable.Initializer is { } initializer)
                        changed |= Propagate(variable.Symbol, initializer.Value);
                    foreach ((IOperation target, IOperation value) in Stores(node))
                    {
                        if (Peel(target) is ILocalReferenceOperation local) changed |= Propagate(local.Local, value);
                        else if (HasLease(value)) changed |= MarkLocalContainer(target);
                    }
                    if (node is IInvocationOperation invocation && IsInsertion(invocation) && invocation.Arguments.Any(argument => HasLease(argument.Value)))
                        changed |= MarkLocalContainer(invocation.Instance);
                }
            }
            foreach (IOperation node in nodes)
            {
                foreach ((IOperation target, IOperation value) in Stores(node)) CheckStorage(target, value, node.Syntax);
                // An event keeps its handler in the event's owner, like a field: removal stores nothing.
                if (node is IEventAssignmentOperation { Adds: true } subscription && HasLease(subscription.HandlerValue))
                    Emit(subscription.Syntax, subscription.EventReference is IEventReferenceOperation reference ? reference.Event.Name : "event");
                if (node is IFieldInitializerOperation fieldInitializer && HasLease(fieldInitializer.Value))
                {
                    foreach (IFieldSymbol field in fieldInitializer.InitializedFields)
                        if (!types.ContainsLease(field.Type) && !types.IsPrivateCredential(field)) Emit(fieldInitializer.Syntax, field.Name);
                }
                if (node is IPropertyInitializerOperation propertyInitializer && HasLease(propertyInitializer.Value))
                {
                    foreach (IPropertySymbol property in propertyInitializer.InitializedProperties)
                        if (!types.ContainsLease(property.Type)) Emit(propertyInitializer.Syntax, property.Name);
                }
                if (node is IInvocationOperation invocation && IsInsertion(invocation) && IsRetained(invocation.Instance) &&
                    !types.ContainsLease(invocation.Instance?.Type) && invocation.Arguments.Any(argument => HasLease(argument.Value)))
                    Emit(invocation.Syntax, invocation.TargetMethod.Name);
            }
        }

        /// <summary>
        /// Every target/value pair a node writes: simple, compound (including delegate +=) and ??= assignments,
        /// deconstruction paired element by element when the value is a tuple literal (otherwise each target
        /// receives the whole value), and the ref location of Interlocked.Exchange/CompareExchange.
        /// Returns a reused buffer; each caller finishes iterating before the next call.
        /// </summary>
        private List<(IOperation Target, IOperation Value)> Stores(IOperation node)
        {
            stores.Clear();
            switch (node)
            {
                case IDeconstructionAssignmentOperation deconstruction:
                    var pending = new Stack<(IOperation Target, IOperation Value)>();
                    pending.Push((deconstruction.Target, deconstruction.Value));
                    // Finite: each step descends one level of the declared tuple syntax.
                    while (pending.Count != 0)
                    {
                        (IOperation target, IOperation value) = pending.Pop();
                        target = Peel(target)!;
                        if (target is IDeclarationExpressionOperation declaration) { pending.Push((declaration.Expression, value)); continue; }
                        if (target is not ITupleOperation targets) { stores.Add((target, value)); continue; }
                        if (Peel(value) is ITupleOperation values && values.Elements.Length == targets.Elements.Length)
                        {
                            for (int i = 0; i < targets.Elements.Length; i++) pending.Push((targets.Elements[i], values.Elements[i]));
                        }
                        else
                        {
                            foreach (IOperation element in targets.Elements) pending.Push((element, value));
                        }
                    }
                    break;
                case IAssignmentOperation assignment:
                    stores.Add((assignment.Target, assignment.Value));
                    break;
                case IInvocationOperation call when types.IsInterlockedStore(call.TargetMethod):
                    IArgumentOperation? location = call.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0 && argument.Parameter.RefKind == RefKind.Ref);
                    IArgumentOperation? stored = call.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 1);
                    if (location is not null && stored is not null) stores.Add((location.Value, stored.Value));
                    break;
            }
            return stores;
        }

        private bool Propagate(ILocalSymbol local, IOperation value)
        {
            bool changed = false;
            if (IsRetained(value)) changed |= retained.Add(local);
            if (HasLease(value)) changed |= carrying.Add(local);
            return changed;
        }

        private bool MarkLocalContainer(IOperation? target)
        {
            target = Peel(target);
            if (target is IArrayElementReferenceOperation array) target = Peel(array.ArrayReference);
            if (target is IPropertyReferenceOperation { Property.IsIndexer: true } property) target = Peel(property.Instance);
            return target is ILocalReferenceOperation local && carrying.Add(local.Local);
        }

        private void CheckStorage(IOperation target, IOperation value, SyntaxNode syntax)
        {
            target = Peel(target)!;
            if (!HasLease(value)) return;
            switch (target)
            {
                case IFieldReferenceOperation field when !types.ContainsLease(field.Field.Type) && !types.IsPrivateCredential(field.Field):
                    Emit(syntax, field.Field.Name);
                    break;
                case IPropertyReferenceOperation property when !types.ContainsLease(property.Property.Type):
                    if (!property.Property.IsIndexer || IsRetained(property.Instance)) Emit(syntax, property.Property.Name);
                    break;
                case IArrayElementReferenceOperation array when IsRetained(array.ArrayReference) && !types.ContainsLease(array.Type):
                    Emit(syntax, "array element");
                    break;
            }
        }

        private bool IsRetained(IOperation? value)
        {
            var pending = new Stack<IOperation>();
            var seen = new HashSet<IOperation>();
            if (value is not null) pending.Push(value);
            while (pending.Count != 0)
            {
                IOperation current = Peel(pending.Pop())!;
                if (!seen.Add(current)) continue;
                switch (current)
                {
                    case IFieldReferenceOperation: return true;
                    case IPropertyReferenceOperation property:
                        if (!property.Property.IsIndexer) return true;
                        if (property.Instance is not null) pending.Push(property.Instance);
                        break;
                    case IArrayElementReferenceOperation array: pending.Push(array.ArrayReference); break;
                    case ILocalReferenceOperation local when retained.Contains(local.Local): return true;
                    case IConditionalOperation conditional:
                        pending.Push(conditional.WhenTrue);
                        if (conditional.WhenFalse is not null) pending.Push(conditional.WhenFalse);
                        break;
                    case ICoalesceOperation coalesce: pending.Push(coalesce.Value); pending.Push(coalesce.WhenNull); break;
                }
            }
            return false;
        }

        private bool HasLease(IOperation value)
        {
            var pending = new Stack<IOperation>();
            var seen = new HashSet<IOperation>();
            pending.Push(value);
            while (pending.Count != 0)
            {
                IOperation current = Peel(pending.Pop())!;
                if (!seen.Add(current)) continue;
                if (types.ContainsLease(current.Type)) return true;
                switch (current)
                {
                    case ILocalReferenceOperation local when carrying.Contains(local.Local): return true;
                    case IAnonymousFunctionOperation lambda when CapturesLease(lambda, lambda.Symbol): return true;
                    case IMethodReferenceOperation method when capturingFunctions.Contains(method.Method): return true;
                    case IMethodReferenceOperation { Instance: { } instance }: pending.Push(instance); break;
                    case IDelegateCreationOperation creation: pending.Push(creation.Target); break;
                    case IConditionalOperation conditional:
                        pending.Push(conditional.WhenTrue);
                        if (conditional.WhenFalse is not null) pending.Push(conditional.WhenFalse);
                        break;
                    case ICoalesceOperation coalesce: pending.Push(coalesce.Value); pending.Push(coalesce.WhenNull); break;
                    case ITupleOperation tuple:
                        foreach (IOperation element in tuple.Elements) pending.Push(element);
                        break;
                    case IArrayCreationOperation { Initializer: { } initializer }: pending.Push(initializer); break;
                    case IArrayInitializerOperation array:
                        foreach (IOperation element in array.ElementValues) pending.Push(element);
                        break;
                    case IObjectCreationOperation { Initializer: { } initializer }: pending.Push(initializer); break;
                    case IObjectOrCollectionInitializerOperation initializer:
                        foreach (IOperation element in initializer.Initializers) pending.Push(element);
                        break;
                    case ICollectionExpressionOperation collection:
                        foreach (IOperation element in collection.Elements) pending.Push(element);
                        break;
                    case ISpreadOperation spread: pending.Push(spread.Operand); break;
                    case IInvocationOperation invocation when invocation.IsImplicit && IsInsertion(invocation):
                        foreach (IArgumentOperation argument in invocation.Arguments) pending.Push(argument.Value);
                        break;
                    case IAssignmentOperation assignment: pending.Push(assignment.Value); break;
                }
            }
            return false;
        }

        private bool CapturesLease(IOperation function, IMethodSymbol symbol)
        {
            var pending = new Stack<IOperation>();
            var seen = new HashSet<IOperation>();
            pending.Push(function);
            while (pending.Count != 0)
            {
                IOperation node = pending.Pop();
                if (!seen.Add(node)) continue;
                if (node is IInstanceReferenceOperation && types.ContainsLease(node.Type)) return true;
                ISymbol? captured = node switch
                {
                    ILocalReferenceOperation local => local.Local,
                    IParameterReferenceOperation parameter => parameter.Parameter,
                    _ => null,
                };
                if (captured is not null && !DeclaredWithin(captured, symbol))
                {
                    if (captured is ILocalSymbol local && (types.ContainsLease(local.Type) || carrying.Contains(local))) return true;
                    if (captured is IParameterSymbol parameter && types.ContainsLease(parameter.Type)) return true;
                }
                if (node is IMethodReferenceOperation method && capturingFunctions.Contains(method.Method) && !DeclaredWithin(method.Method, symbol) ||
                    node is IInvocationOperation invocation && capturingFunctions.Contains(invocation.TargetMethod) && !DeclaredWithin(invocation.TargetMethod, symbol)) return true;
                foreach (IOperation child in node.ChildOperations) pending.Push(child);
            }
            return false;
        }

        private static bool DeclaredWithin(ISymbol value, IMethodSymbol function)
        {
            ISymbol? owner = value.ContainingSymbol;
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            while (owner is not null && seen.Add(owner))
            {
                if (SymbolEqualityComparer.Default.Equals(owner, function)) return true;
                owner = owner.ContainingSymbol;
            }
            return false;
        }

        private static IOperation? Peel(IOperation? value)
        {
            while (value is IConversionOperation or IParenthesizedOperation)
                value = value is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation)value).Operand;
            return value;
        }

        private static bool IsInsertion(IInvocationOperation invocation) => invocation.Instance is not null &&
            invocation.TargetMethod.Name is "Add" or "AddRange" or "Enqueue" or "Push" or "TryAdd" or "Insert" or "GetOrAdd" or "AddOrUpdate";

        private void Emit(SyntaxNode syntax, string name)
        {
            if (reported.Add((syntax.SyntaxTree, syntax.SpanStart, syntax.Span.Length)))
                context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.GetLocation(), name));
        }
    }
}
