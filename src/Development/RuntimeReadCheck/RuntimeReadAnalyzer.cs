using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ResourceManager.Development.RuntimeReadCheck;

/// <summary>Checks actual bound raw API calls and their immediate acquisition call chains.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RuntimeReadAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeName = "ResourceManager.ReadWrite.RuntimeReadAttribute";
    private const string TransportName = "ResourceManager.ReadWrite.RuntimeReadTransportAttribute";
    private static readonly DiagnosticDescriptor Missing = Rule("RMR001", "Transport source ownership is unresolved", "Raw {0} read '{1}' requires RuntimeRead on its owning method/accessor", DiagnosticSeverity.Warning);
    private static readonly DiagnosticDescriptor Unmarked = Rule("RMR006", "Raw read needs volatility", "Raw {0} read '{1}' requires RuntimeRead on its owning method/accessor");
    private static readonly DiagnosticDescriptor Event = Rule("RMR002", "Event read needs a named event", "EventDriven read '{0}' requires a nonempty changeEvent");
    private static readonly DiagnosticDescriptor Periodic = Rule("RMR003", "Nonsampled acquisition needs owner verification", "Periodic acquisition '{0}' reaches {1} read '{2}'; verify physical acquisition and the original owner/invalidation contract", DiagnosticSeverity.Warning);
    private static readonly DiagnosticDescriptor Repeated = Rule("RMR007", "Nonsampled physical read repeats", "Periodic acquisition '{0}' directly executes {1} physical read '{2}'; preserve the original read-once owner or named event");
    private static readonly DiagnosticDescriptor Summary = Rule("RMR000", "Runtime read diagnostic counts", "RuntimeRead {0}: unresolved={1} (RMR001={2}, RMR003={3}, RMR103={4}); proven-repeat={5}; unmarked-primary={6}; invalid={7}", DiagnosticSeverity.Warning);
    private static readonly DiagnosticDescriptor Invalid = Rule("RMR004", "Invalid read volatility", "Read '{0}' requires exactly one valid RuntimeRead or RuntimeReadSource declaration, without a transport override");
    private static readonly DiagnosticDescriptor NewOwner = Rule("RMR005", "Cache owner recreated in periodic acquisition", "Periodic acquisition '{0}' creates cache owner '{1}' again; preserve the owner in backend state");
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Summary, Missing, Unmarked, Event, Periodic, Repeated, Invalid, NewOwner, .. NativeReadCheck.Rules];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var diagnostics = new ConcurrentBag<Diagnostic>();
            var physicalReads = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var edges = new ConcurrentBag<Edge>();
            var bindings = new ConcurrentBag<CallbackBinding>();
            var callbackCalls = new ConcurrentBag<CallbackCall>();
            var transports = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var methods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            start.RegisterSymbolAction(symbolContext =>
            {
                var method = (IMethodSymbol)symbolContext.Symbol;
                methods.TryAdd(method.OriginalDefinition, 0);
                if (IsTransport(method)) transports.TryAdd(method.OriginalDefinition, 0);
                var declaration = Declaration(method);
                if (declaration is null) return;
                var (kind, changeEvent) = declaration.Value;
                var location = method.Locations.FirstOrDefault(static value => value.IsInSource);
                if (location is null) return;
                if (kind is < 0 or > 2 || kind != 1 && changeEvent is not null)
                    diagnostics.Add(Diagnostic.Create(Invalid, location, method.Name));
                if (kind == 1 && string.IsNullOrWhiteSpace(changeEvent))
                    diagnostics.Add(Diagnostic.Create(Event, location, method.Name));
            }, SymbolKind.Method);
            start.RegisterSymbolAction(symbolContext =>
            {
                var field = (IFieldSymbol)symbolContext.Symbol;
                if (FieldDeclaration(field) is not { } declaration) return;
                var (kind, changeEvent) = declaration;
                var location = field.Locations.FirstOrDefault(static value => value.IsInSource);
                if (location is null) return;
                if (kind is < 0 or > 2 || kind != 1 && changeEvent is not null)
                    diagnostics.Add(Diagnostic.Create(Invalid, location, field.Name));
                if (kind == 1 && string.IsNullOrWhiteSpace(changeEvent))
                    diagnostics.Add(Diagnostic.Create(Event, location, field.Name));
            }, SymbolKind.Field);
            start.RegisterOperationAction(operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var target = invocation.TargetMethod;
                var owner = OperationOwner(invocation, operationContext.ContainingSymbol);
                var family = RawFamily(target);
                if (family is not null && (owner is null || Declaration(owner) is null && !IsTransport(owner)))
                    diagnostics.Add(Diagnostic.Create(Unmarked, invocation.Syntax.GetLocation(), family, target.Name));
                if (owner is not null && !IsLazyFactory(invocation.Syntax, operationContext.Compilation))
                {
                    edges.Add(new(owner.OriginalDefinition, target.OriginalDefinition, invocation.Syntax.GetLocation()));
                    if (target.MethodKind == MethodKind.DelegateInvoke && DelegateSlot(invocation.Instance) is { } slot)
                        callbackCalls.Add(new(owner.OriginalDefinition, slot, invocation.Syntax.GetLocation()));
                }
            }, OperationKind.Invocation);
            start.RegisterOperationAction(operationContext =>
            {
                var invocation = (IFunctionPointerInvocationOperation)operationContext.Operation;
                if (invocation.Target.Type is IFunctionPointerTypeSymbol pointer
                    && pointer.Signature.CallingConvention == System.Reflection.Metadata.SignatureCallingConvention.Default) return;
                var owner = OperationOwner(invocation, operationContext.ContainingSymbol);
                if (IsComRelease(invocation, owner, operationContext.Compilation)) return;
                // Unmanaged function pointers have no named API symbol; require the same explicit boundary.
                if (owner is null || Declaration(owner) is null && !IsTransport(owner))
                    diagnostics.Add(Diagnostic.Create(Unmarked, invocation.Syntax.GetLocation(), "unmanaged function-pointer",
                        invocation.Target.Syntax.ToString().Replace('\r', ' ').Replace('\n', ' ')));
                if (owner is not null && IsStraightLine(owner)) physicalReads.TryAdd(owner.OriginalDefinition, 0);
            }, OperationKind.FunctionPointerInvocation);
            start.RegisterOperationAction(operationContext =>
            {
                var reference = (IMethodReferenceOperation)operationContext.Operation;
                var owner = OperationOwner(reference, operationContext.ContainingSymbol);
                var family = RawFamily(reference.Method);
                if (family is not null && (owner is null || Declaration(owner) is null && !IsTransport(owner)))
                    diagnostics.Add(Diagnostic.Create(Unmarked, reference.Syntax.GetLocation(), family, reference.Method.Name));
                // A method group passed to an invoked scheduler can execute during that call.
                // A field/property assignment only stores it; that route is bound below.
                if (owner is not null && IsScheduledArgument(reference) && !IsLazyFactory(reference.Syntax, operationContext.Compilation))
                    edges.Add(new(owner.OriginalDefinition, reference.Method.OriginalDefinition, reference.Syntax.GetLocation(), IsMethodReference: true));
            }, OperationKind.MethodReference);
            start.RegisterOperationAction(operationContext =>
            {
                var reference = (IPropertyReferenceOperation)operationContext.Operation;
                var owner = OperationOwner(reference, operationContext.ContainingSymbol);
                var family = reference.Property.GetMethod is { } rawGetter ? RawFamily(rawGetter) : null;
                if (family is not null && (owner is null || Declaration(owner) is null && !IsTransport(owner)))
                    diagnostics.Add(Diagnostic.Create(Unmarked, reference.Syntax.GetLocation(), family, reference.Property.Name));
                if (owner is not null && reference.Property.GetMethod is { } getter
                    && !IsLazyFactory(reference.Syntax, operationContext.Compilation))
                    edges.Add(new(owner.OriginalDefinition, getter.OriginalDefinition, reference.Syntax.GetLocation()));
            }, OperationKind.PropertyReference);
            start.RegisterOperationAction(operationContext =>
            {
                var creation = (IObjectCreationOperation)operationContext.Operation;
                var owner = OperationOwner(creation, operationContext.ContainingSymbol);
                if (owner is not null && creation.Constructor is { } constructor)
                    edges.Add(new(owner.OriginalDefinition, constructor.OriginalDefinition, creation.Syntax.GetLocation(),
                        IsConstruction: true, IsPersistentConstruction: IsPersistentCreation(creation.Syntax, operationContext.Compilation)));
            }, OperationKind.ObjectCreation);
            start.RegisterOperationAction(operationContext =>
            {
                var lambda = (IAnonymousFunctionOperation)operationContext.Operation;
                methods.TryAdd(lambda.Symbol.OriginalDefinition, 0);
                if (IsTransport(lambda.Symbol)) transports.TryAdd(lambda.Symbol.OriginalDefinition, 0);
                var owner = OperationOwner(lambda, operationContext.ContainingSymbol);
                if (owner is not null && IsScheduledArgument(lambda) && !IsLazyFactory(lambda.Syntax, operationContext.Compilation))
                    edges.Add(new(owner.OriginalDefinition, lambda.Symbol.OriginalDefinition, lambda.Syntax.GetLocation()));
            }, OperationKind.AnonymousFunction);
            start.RegisterOperationAction(operationContext =>
            {
                var assignment = (ISimpleAssignmentOperation)operationContext.Operation;
                if (DelegateSlot(assignment.Target) is not { } slot) return;
                foreach (var callback in CallbackTargets(assignment.Value)) bindings.Add(new(slot, callback.OriginalDefinition));
            }, OperationKind.SimpleAssignment);
            start.RegisterOperationAction(operationContext =>
            {
                var initializer = (IFieldInitializerOperation)operationContext.Operation;
                foreach (var field in initializer.InitializedFields.Where(static field => field.Type.TypeKind == TypeKind.Delegate))
                    foreach (var callback in CallbackTargets(initializer.Value)) bindings.Add(new(field, callback.OriginalDefinition));
            }, OperationKind.FieldInitializer);
            start.RegisterOperationAction(operationContext =>
            {
                var variable = (IVariableDeclaratorOperation)operationContext.Operation;
                if (variable.Symbol.Type.TypeKind != TypeKind.Delegate || variable.Initializer is not { } initializer) return;
                foreach (var callback in CallbackTargets(initializer.Value)) bindings.Add(new(variable.Symbol, callback.OriginalDefinition));
            }, OperationKind.VariableDeclarator);
            start.RegisterCompilationEndAction(end =>
            {
                foreach (var file in start.Options.AdditionalFiles)
                {
                    end.CancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetExtension(file.Path) is not (".c" or ".cpp" or ".zig" or ".h" or ".hpp" or ".inc")) continue;
                    if (file.GetText(end.CancellationToken) is not { } source)
                    {
                        diagnostics.Add(NativeReadCheck.CannotRead(file.Path));
                        continue;
                    }
                    foreach (var diagnostic in NativeReadCheck.Analyze(source, file.Path)) diagnostics.Add(diagnostic);
                }
                AddCallbackEdges(bindings, callbackCalls, edges);
                AddDispatchEdges(methods.Keys, edges);
                CheckTransports(end, edges, transports.Keys, diagnostics.Add);
                CheckPeriodic(end, methods.Keys, edges, physicalReads, diagnostics.Add);
                foreach (var diagnostic in diagnostics) end.ReportDiagnostic(diagnostic);
                int Count(params string[] ids) => diagnostics.Count(diagnostic => ids.Contains(diagnostic.Id));
                var strict = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.RuntimeReadStrict", out var value)
                    && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                end.ReportDiagnostic(Diagnostic.Create(Summary, Location.None, strict ? "strict" : "normal",
                    Count("RMR001", "RMR003", "RMR103"), Count("RMR001"), Count("RMR003"), Count("RMR103"),
                    Count("RMR005", "RMR007"), Count("RMR006", "RMR101"), Count("RMR002", "RMR004", "RMR100", "RMR102", "RMR104")));
            });
        });
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error)
        => new(id, title, message, "RuntimeRead", severity, isEnabledByDefault: true);

    private static IMethodSymbol? Owner(ISymbol? symbol)
    {
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
            symbol = symbol.ContainingSymbol;
        return symbol as IMethodSymbol;
    }

    private static IMethodSymbol? OperationOwner(IOperation operation, ISymbol containingSymbol)
    {
        var parent = operation.Parent;
        while (parent is not null)
        {
            if (parent is IAnonymousFunctionOperation lambda) return lambda.Symbol;
            if (parent is ILocalFunctionOperation local) return local.Symbol;
            parent = parent.Parent;
        }
        return Owner(containingSymbol);
    }

    private static ISymbol? DelegateSlot(IOperation? operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation switch
        {
            IFieldReferenceOperation field when field.Field.Type.TypeKind == TypeKind.Delegate => field.Field.OriginalDefinition,
            IPropertyReferenceOperation property when property.Property.Type.TypeKind == TypeKind.Delegate => property.Property.OriginalDefinition,
            ILocalReferenceOperation local when local.Local.Type.TypeKind == TypeKind.Delegate => local.Local,
            _ => null
        };
    }

    private static IEnumerable<IMethodSymbol> CallbackTargets(IOperation operation)
    {
        var stack = new Stack<IOperation>();
        stack.Push(operation);
        while (stack.TryPop(out var current))
        {
            if (current is IAnonymousFunctionOperation lambda) { yield return lambda.Symbol; continue; }
            if (current is IMethodReferenceOperation method) { yield return method.Method; continue; }
            foreach (var child in current.ChildOperations) stack.Push(child);
        }
    }

    private static bool IsScheduledArgument(IOperation operation)
    {
        var parent = operation.Parent;
        while (parent is IConversionOperation or IDelegateCreationOperation) parent = parent.Parent;
        return parent is IArgumentOperation { Parent: IInvocationOperation or IObjectCreationOperation };
    }

    private static bool IsComRelease(IFunctionPointerInvocationOperation invocation, IMethodSymbol? owner, Compilation compilation)
    {
        // IUnknown slot 2 decrements a COM reference; it does not acquire source data.
        if (owner?.Name != "Release" || invocation.Target.Type is not IFunctionPointerTypeSymbol pointer
            || pointer.Signature.ReturnType.SpecialType != SpecialType.System_UInt32 || pointer.Signature.Parameters.Length != 1) return false;
        var target = invocation.Target.Syntax;
        while (target is ParenthesizedExpressionSyntax parentheses) target = parentheses.Expression;
        if (target is not CastExpressionSyntax { Expression: InvocationExpressionSyntax slot } || slot.ArgumentList.Arguments.Count != 2) return false;
        var model = compilation.GetSemanticModel(slot.SyntaxTree);
        return model.GetSymbolInfo(slot).Symbol is IMethodSymbol { Name: "Slot" }
            && model.GetConstantValue(slot.ArgumentList.Arguments[1].Expression) is { HasValue: true, Value: 2 };
    }

    private static bool IsTransport(IMethodSymbol method)
    {
        while (true)
        {
            if (method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() == TransportName)
                || DescriptionDeclaration(method)?.Kind == 3) return true;
            if (method.MethodKind != MethodKind.AnonymousFunction || method.ContainingSymbol is not IMethodSymbol enclosing) return false;
            method = enclosing;
        }
    }

    private static (int Kind, string? ChangeEvent)? Declaration(IMethodSymbol method)
    {
        AttributeData? attribute;
        while (true)
        {
            var source = method.GetAttributes().FirstOrDefault(static value =>
                value.AttributeClass?.ToDisplayString() == "ResourceManager.ReadWrite.RuntimeReadSourceAttribute");
            var description = DescriptionDeclaration(method) ?? (method.AssociatedSymbol is { } associated ? DescriptionDeclaration(associated) : null);
            attribute = method.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == AttributeName)
                ?? method.AssociatedSymbol?.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == AttributeName);
            if (attribute is not null && source is not null) return (-1, null);
            if (description is not null && (attribute is not null || source is not null
                || method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() == TransportName))) return (-1, null);
            if (description is { Kind: not 3 }) return description;
            if (description is { Kind: 3 }) return null;
            if ((attribute is not null || source is not null)
                && method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() == TransportName)) return (-1, null);
            if (source is not null)
            {
                if (source.ConstructorArguments is [{ Value: INamedTypeSymbol sourceType }, { Value: string fieldName }])
                {
                    var field = sourceType.GetMembers(fieldName).OfType<IFieldSymbol>().SingleOrDefault();
                    if (field is { IsConst: true, Type.SpecialType: SpecialType.System_String })
                        return FieldDeclaration(field) is { Kind: >= 0 and <= 2 } declaration ? declaration : (-1, null);
                }
                if (attribute is null) return (-1, null);
            }
            if (attribute is not null) break;
            if (method.MethodKind != MethodKind.AnonymousFunction || method.ContainingSymbol is not IMethodSymbol enclosing) return null;
            method = enclosing;
        }
        var kind = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int value ? value : -1;
        var changeEvent = attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string : null;
        return (kind, changeEvent);
    }

    private static (int Kind, string? ChangeEvent)? DescriptionDeclaration(ISymbol symbol)
    {
        var declarations = symbol.GetAttributes().Where(static value =>
            value.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute"
            && value.ConstructorArguments is [{ Value: string text }] && text.StartsWith("RuntimeRead:", StringComparison.Ordinal)).ToArray();
        if (declarations.Length == 0) return null;
        if (declarations.Length != 1) return (-1, null);
        return NativeReadCheck.ParseDeclaration(((string)declarations[0].ConstructorArguments[0].Value!)[12..].Trim());
    }

    private static (int Kind, string? ChangeEvent)? FieldDeclaration(IFieldSymbol field)
    {
        var attribute = field.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == AttributeName);
        var description = DescriptionDeclaration(field);
        if (attribute is not null && description is not null) return (-1, null);
        if (description is not null) return description;
        if (attribute is null) return null;
        return (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int kind ? kind : -1,
            attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string : null);
    }

    private static bool IsLazyFactory(SyntaxNode syntax, Compilation compilation)
    {
        var lambda = syntax.AncestorsAndSelf().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
        var argument = lambda?.Parent as ArgumentSyntax ?? syntax.Parent as ArgumentSyntax;
        if (argument?.Parent?.Parent is not BaseObjectCreationExpressionSyntax creation) return false;
        var model = compilation.GetSemanticModel(creation.SyntaxTree);
        if (model.GetTypeInfo(creation).Type?.OriginalDefinition.ToDisplayString() != "System.Lazy<T>") return false;
        return IsPersistentCreation(creation, compilation);
    }

    private static bool IsPersistentCreation(SyntaxNode creation, Compilation compilation)
    {
        var model = compilation.GetSemanticModel(creation.SyntaxTree);
        if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }
            && model.GetDeclaredSymbol(variable) is IFieldSymbol) return true;
        if (creation.Parent is EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }) return true;
        if (creation.Parent is AssignmentExpressionSyntax assignment
            && model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol)
            return assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)
                || assignment.Ancestors().Any(static node => node is ConstructorDeclarationSyntax);
        return false;
    }

    public static string? RawFamily(IMethodSymbol method)
    {
        var type = method.ContainingType.ToDisplayString();
        var name = method.Name;
        if (type.StartsWith("System.Management.", StringComparison.Ordinal)
            && name is "Get" or "GetInstances" or "GetRelated" or "GetRelationships" or "InvokeMethod") return "WMI";
        if (type == "Microsoft.Win32.Registry" && name == "GetValue"
            || type == "Microsoft.Win32.RegistryKey" && name is "GetValue" or "GetValueKind" or "GetValueNames" or "GetSubKeyNames" or "OpenSubKey" or "get_ValueCount" or "get_SubKeyCount") return "registry";
        if (type.StartsWith("System.Runtime.Intrinsics.X86.", StringComparison.Ordinal) && name == "CpuId") return "CPUID";
        var import = method.GetDllImportData();
        var libraryImport = method.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.LibraryImportAttribute");
        var nativeName = import?.EntryPointName ?? libraryImport?.NamedArguments
            .FirstOrDefault(static argument => argument.Key == "EntryPoint").Value.Value as string ?? name;
        var moduleName = import?.ModuleName ?? libraryImport?.ConstructorArguments.FirstOrDefault().Value as string;
        if ((import is not null || libraryImport is not null)
            && (nativeName.StartsWith("RegQuery", StringComparison.Ordinal) || nativeName.StartsWith("RegEnum", StringComparison.Ordinal)
                || nativeName.StartsWith("RegOpenKey", StringComparison.Ordinal) || nativeName.StartsWith("RegGetValue", StringComparison.Ordinal))) return "registry";
        if (nativeName.StartsWith("SetupDi", StringComparison.Ordinal)
            && (nativeName.Contains("Get", StringComparison.Ordinal) || nativeName.Contains("Enum", StringComparison.Ordinal))) return "SetupAPI";
        if (nativeName.StartsWith("CM_Get_", StringComparison.Ordinal) || nativeName.StartsWith("CM_Locate_DevNode", StringComparison.Ordinal)) return "SetupAPI/ConfigMgr";
        if (nativeName is "GetEnabledXStateFeatures" or "IsProcessorFeaturePresent") return "CPU OS capabilities";
        if (nativeName is "GetLogicalProcessorInformationEx" or "GetSystemCpuSetInformation") return "CPU OS topology";
        if (nativeName.Contains("CpuId", StringComparison.OrdinalIgnoreCase) || nativeName.Contains("cpuid", StringComparison.OrdinalIgnoreCase))
            return import is not null || libraryImport is not null ? "CPUID" : null;
        if (nativeName.StartsWith("nvmlDeviceGet", StringComparison.Ordinal)
            || nativeName.StartsWith("nvmlSystemGet", StringComparison.Ordinal)) return "NVML";
        if (nativeName.StartsWith("GetSystemFirmwareTable", StringComparison.Ordinal)
            || nativeName.StartsWith("EnumSystemFirmwareTables", StringComparison.Ordinal)) return "ACPI/firmware";
        if (nativeName.StartsWith("CreateDXGIFactory", StringComparison.Ordinal)
            || method.ContainingType.Name.StartsWith("IDXGI", StringComparison.Ordinal)
                && (name.StartsWith("Enum", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal))) return "DXGI";
        if (moduleName?.Contains("Adlx", StringComparison.OrdinalIgnoreCase) == true
            && (nativeName.Contains("Read", StringComparison.Ordinal) || nativeName.Contains("Get", StringComparison.Ordinal)
                || nativeName.Contains("Tuning", StringComparison.Ordinal))) return "ADLX";
        return null;
    }

    private static void CheckTransports(CompilationAnalysisContext context, IEnumerable<Edge> edges, IEnumerable<IMethodSymbol> transports, Action<Diagnostic> report)
    {
        var incoming = new Dictionary<IMethodSymbol, List<Edge>>(SymbolEqualityComparer.Default);
        foreach (var edge in edges)
        {
            // Binding a delegate is not a physical transport invocation. Cadence checking
            // retains callback edges below; transport ownership starts at its invocation.
            if (edge.IsMethodReference) continue;
            if (!incoming.TryGetValue(edge.Callee, out var list)) incoming.Add(edge.Callee, list = []);
            list.Add(edge);
        }
        var visited = new HashSet<IMethodSymbol>(transports, SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>(visited);
        while (queue.TryDequeue(out var current))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!incoming.TryGetValue(current, out var callers)) continue;
            foreach (var edge in callers)
            {
                if (Declaration(edge.Caller) is not null) continue;
                if (!IsTransport(edge.Caller))
                    report(Diagnostic.Create(Missing, edge.Location, "transport", current.Name));
                if (visited.Add(edge.Caller)) queue.Enqueue(edge.Caller);
            }
        }
    }

    private static bool IsPeriodic(IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.AnonymousFunction) return false;
        if (Declaration(method) is { } declaration) return declaration.Kind == 2;
        return method.MethodKind == MethodKind.Ordinary && (method.Name.StartsWith("Capture", StringComparison.Ordinal)
            || method.Name.StartsWith("Collect", StringComparison.Ordinal) || method.Name.StartsWith("Sample", StringComparison.Ordinal)
            || method.Name.StartsWith("Request", StringComparison.Ordinal));
    }

    private static bool IsCache(IMethodSymbol method)
        => method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() == "ResourceManager.ReadWrite.RuntimeReadCacheAttribute");

    private static void AddDispatchEdges(IEnumerable<IMethodSymbol> methods, ConcurrentBag<Edge> edges)
    {
        var types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in methods)
        {
            types.Add(method.ContainingType);
            if (method.OverriddenMethod is { } overridden && method.Locations.FirstOrDefault(static location => location.IsInSource) is { } location)
                edges.Add(new(overridden.OriginalDefinition, method.OriginalDefinition, location, IsIndirect: true));
        }
        foreach (var type in types)
        {
            foreach (var contract in type.AllInterfaces)
            {
                foreach (var member in contract.GetMembers())
                {
                    var target = member as IMethodSymbol ?? (member as IPropertySymbol)?.GetMethod;
                    var implementation = type.FindImplementationForInterfaceMember(member) as IMethodSymbol
                        ?? (type.FindImplementationForInterfaceMember(member) as IPropertySymbol)?.GetMethod;
                    if (target is null || implementation is null || implementation.DeclaringSyntaxReferences.Length == 0) continue;
                    var location = implementation.Locations.FirstOrDefault(static value => value.IsInSource);
                    if (location is not null) edges.Add(new(target.OriginalDefinition, implementation.OriginalDefinition, location, IsIndirect: true));
                }
            }
        }
    }

    private static void AddCallbackEdges(IEnumerable<CallbackBinding> bindings, IEnumerable<CallbackCall> calls, ConcurrentBag<Edge> edges)
    {
        var slots = new Dictionary<ISymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var binding in bindings)
        {
            if (!slots.TryGetValue(binding.Slot, out var targets)) slots.Add(binding.Slot, targets = new(SymbolEqualityComparer.Default));
            targets.Add(binding.Target);
        }
        foreach (var call in calls)
            if (slots.TryGetValue(call.Slot, out var targets))
                foreach (var target in targets) edges.Add(new(call.Caller, target, call.Location, IsIndirect: true));
    }

    private static void CheckPeriodic(CompilationAnalysisContext context, IEnumerable<IMethodSymbol> methods, IEnumerable<Edge> edges,
        ConcurrentDictionary<IMethodSymbol, byte> physicalReads, Action<Diagnostic> report)
    {
        var graph = new Dictionary<IMethodSymbol, List<Edge>>(SymbolEqualityComparer.Default);
        foreach (var edge in edges)
        {
            if (!graph.TryGetValue(edge.Caller, out var list)) graph.Add(edge.Caller, list = []);
            list.Add(edge);
        }
        foreach (var root in methods.Where(IsPeriodic))
        {
            // Retain both possible and definite routes: an indirect route must not hide
            // a separate direct route to the same method.
            var visited = new HashSet<(IMethodSymbol Method, bool Definite)>(new MethodPathComparer());
            var reported = new HashSet<(Location Location, string Id)>();
            var queue = new Queue<(IMethodSymbol Method, bool Definite)>();
            queue.Enqueue((root, IsStraightLine(root) && !IsTransport(root)));
            while (queue.TryDequeue(out var path))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(path) || !graph.TryGetValue(path.Method, out var calls)) continue;
                foreach (var edge in calls)
                {
                    if (edge.IsPersistentConstruction) continue;
                    if (edge.IsConstruction && edge.Callee.ContainingType.GetMembers().OfType<IMethodSymbol>()
                        .Any(static method => !method.IsStatic && IsCache(method)))
                        report(Diagnostic.Create(NewOwner, edge.Location,
                            root.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), edge.Callee.ContainingType.Name));
                    var definite = path.Definite && IsDirect(edge) && IsStraightLine(edge.Caller) && !IsTransport(edge.Callee);
                    if (Declaration(edge.Callee) is { Kind: 0 or 1 } declaration)
                    {
                        var rule = definite && ReachesPhysicalRead(edge.Callee, graph, physicalReads, context.CancellationToken) ? Repeated : Periodic;
                        if (reported.Add((edge.Location, rule.Id))) report(Diagnostic.Create(rule, edge.Location,
                            root.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                            declaration.Kind == 0 ? "Fixed" : "EventDriven",
                            edge.Callee.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                        continue;
                    }
                    // Only an unclassified cache-read boundary stops traversal. A marked
                    // physical Fixed/EventDriven read cannot exempt itself with this marker.
                    if (IsCache(edge.Callee)) continue;
                    queue.Enqueue((edge.Callee, definite));
                }
            }
        }
    }

    private static bool IsDirect(Edge edge)
        => !edge.IsIndirect && !edge.IsMethodReference && edge.Callee.MethodKind != MethodKind.DelegateInvoke
            && !edge.Callee.IsAbstract && (!(edge.Callee.IsVirtual || edge.Callee.IsOverride) || edge.Callee.IsSealed);

    private static bool ReachesPhysicalRead(IMethodSymbol method, Dictionary<IMethodSymbol, List<Edge>> graph,
        ConcurrentDictionary<IMethodSymbol, byte> physicalReads, CancellationToken cancellationToken)
    {
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(method);
        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current) || IsTransport(current)) continue;
            if (RawFamily(current) is not null || physicalReads.ContainsKey(current)) return true;
            if (!IsStraightLine(current) || IsCache(current) && Declaration(current) is null
                || !graph.TryGetValue(current, out var calls)) continue;
            foreach (var edge in calls)
                if (IsDirect(edge) && !edge.IsPersistentConstruction) queue.Enqueue(edge.Callee);
        }
        return false;
    }

    private static bool IsStraightLine(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Length != 1) return false;
        var declaration = method.DeclaringSyntaxReferences[0].GetSyntax();
        var body = declaration switch
        {
            BaseMethodDeclarationSyntax value => (SyntaxNode?)value.Body ?? value.ExpressionBody,
            AccessorDeclarationSyntax value => (SyntaxNode?)value.Body ?? value.ExpressionBody,
            PropertyDeclarationSyntax value => value.ExpressionBody,
            LocalFunctionStatementSyntax value => (SyntaxNode?)value.Body ?? value.ExpressionBody,
            _ => null
        };
        // Deliberately narrow proof. Branches, guards, callbacks and dispatch need
        // owner/composition evidence and remain RMR003, rather than becoming errors.
        if (body is BlockSyntax block)
            for (var index = 0; index < block.Statements.Count; index++)
                if (block.Statements[index] is not (ExpressionStatementSyntax or LocalDeclarationStatementSyntax)
                    && (block.Statements[index] is not ReturnStatementSyntax || index != block.Statements.Count - 1)) return false;
        return body is not null && !body.DescendantNodesAndSelf().Any(static node => node is
            IfStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or ConditionalExpressionSyntax
            or ConditionalAccessExpressionSyntax or ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax or TryStatementSyntax or AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax or GotoStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or YieldStatementSyntax
            || node is BinaryExpressionSyntax binary && binary.Kind() is SyntaxKind.CoalesceExpression
                or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
            || node is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression));
    }

    private sealed class MethodPathComparer : IEqualityComparer<(IMethodSymbol Method, bool Definite)>
    {
        public bool Equals((IMethodSymbol Method, bool Definite) x, (IMethodSymbol Method, bool Definite) y)
            => x.Definite == y.Definite && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);
        public int GetHashCode((IMethodSymbol Method, bool Definite) value)
            => HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(value.Method), value.Definite);
    }

    private sealed record Edge(IMethodSymbol Caller, IMethodSymbol Callee, Location Location, bool IsMethodReference = false,
        bool IsConstruction = false, bool IsPersistentConstruction = false, bool IsIndirect = false);
    private sealed record CallbackBinding(ISymbol Slot, IMethodSymbol Target);
    private sealed record CallbackCall(IMethodSymbol Caller, ISymbol Slot, Location Location);
}
