using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// The subscription channel wraps every pushed value in FrontendSubscriptionChannelFrame&lt;T&gt;, and the production
/// serializer only knows source-generated types. A missing frame registration failed every write of that selector and
/// closed the whole shared stream, so the frontend reconnected in a loop. This reads every T the App assembly passes to
/// the channel writer, including the T forwarded by the generic periodic compilers, and requires production metadata.
/// </summary>
public sealed class SubscriptionChannelFrameMetadataTests
{
    [Fact]
    public void EveryValueTheChannelWritesHasProductionFrameMetadata()
    {
        var written = ChannelWrittenValueTypes();
        Assert.Contains(typeof(ResourceFactsSnapshot), written);
        Assert.Contains(typeof(global::ResourceManager.App.Domain.Control.ControlActualState), written);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var missing = written
            .Select(static type => typeof(FrontendSubscriptionChannelFrame<>).MakeGenericType(type))
            .Where(frame => options.TypeInfoResolver!.GetTypeInfo(frame, options) is null)
            .Select(static frame => frame.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(missing.Length == 0, "No production JSON metadata for: " + string.Join(", ", missing));
    }

    private static IReadOnlySet<Type> ChannelWrittenValueTypes()
    {
        var assembly = typeof(FrontendSubscriptionChannelFrame<>).Assembly;
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var reader = pe.GetMetadataReader();
        var provider = new RuntimeTypeProvider(reader, assembly);
        var written = new HashSet<Type>();
        var forwarded = 0;
        var forwardedConcrete = new HashSet<Type>();
        for (var row = 1; row <= reader.GetTableRowCount(TableIndex.MethodSpec); row++)
        {
            var specification = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
            if (specification.Method.Kind != HandleKind.MethodDefinition) continue;
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)specification.Method);
            var name = reader.GetString(method.Name);
            var declaringType = reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
            var arguments = specification.DecodeSignature(provider, genericContext: null);
            if (arguments.Length != 1) continue;
            if (declaringType == "FrontendSubscriptionChannelWriter" && name == "WriteAsync")
            {
                if (arguments[0] is { } concrete) written.Add(concrete);
                else forwarded++;
            }
            else if ((name.StartsWith("CompileAlignedPeriodic", StringComparison.Ordinal)
                || name.StartsWith("PublishAlignedPeriodic", StringComparison.Ordinal)) && arguments[0] is { } aligned)
            {
                forwardedConcrete.Add(aligned);
            }
        }
        // A generic WriteAsync<T> exists only inside the periodic publishers; their concrete T comes from the compilers
        // and from compilers that call a publisher directly (the control actual state does).
        if (forwarded != 0) Assert.NotEmpty(forwardedConcrete);
        written.UnionWith(forwardedConcrete);
        return written;
    }

    // Resolves metadata signatures to runtime types; a generic parameter resolves to null (forwarded, not concrete).
    private sealed class RuntimeTypeProvider(MetadataReader reader, Assembly assembly) : ISignatureTypeProvider<Type?, object?>
    {
        public Type? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => typeof(bool), PrimitiveTypeCode.Byte => typeof(byte), PrimitiveTypeCode.SByte => typeof(sbyte),
            PrimitiveTypeCode.Char => typeof(char), PrimitiveTypeCode.Int16 => typeof(short), PrimitiveTypeCode.UInt16 => typeof(ushort),
            PrimitiveTypeCode.Int32 => typeof(int), PrimitiveTypeCode.UInt32 => typeof(uint), PrimitiveTypeCode.Int64 => typeof(long),
            PrimitiveTypeCode.UInt64 => typeof(ulong), PrimitiveTypeCode.Single => typeof(float), PrimitiveTypeCode.Double => typeof(double),
            PrimitiveTypeCode.String => typeof(string), PrimitiveTypeCode.Object => typeof(object), PrimitiveTypeCode.IntPtr => typeof(nint),
            PrimitiveTypeCode.UIntPtr => typeof(nuint), PrimitiveTypeCode.Void => typeof(void),
            PrimitiveTypeCode.TypedReference => typeof(TypedReference),
            _ => throw new NotSupportedException(typeCode.ToString())
        };

        public Type? GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle handle, byte rawTypeKind)
            => assembly.GetType(DefinitionName(handle), throwOnError: true);

        public Type? GetTypeFromReference(MetadataReader _, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var (name, scope) = ReferenceName(handle);
            return scope.Kind == HandleKind.AssemblyReference
                ? Assembly.Load(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).GetAssemblyName()).GetType(name, throwOnError: true)
                : assembly.GetType(name, throwOnError: true);
        }

        public Type? GetTypeFromSpecification(MetadataReader _, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public Type? GetGenericInstantiation(Type? genericType, ImmutableArray<Type?> typeArguments)
            => genericType is null || typeArguments.Any(static argument => argument is null)
                ? null
                : genericType.MakeGenericType(typeArguments.Select(static argument => argument!).ToArray());

        public Type? GetSZArrayType(Type? elementType) => elementType?.MakeArrayType();
        public Type? GetArrayType(Type? elementType, ArrayShape shape) => elementType?.MakeArrayType(shape.Rank);
        public Type? GetByReferenceType(Type? elementType) => elementType?.MakeByRefType();
        public Type? GetPointerType(Type? elementType) => elementType?.MakePointerType();
        public Type? GetPinnedType(Type? elementType) => elementType;
        public Type? GetModifiedType(Type? modifier, Type? unmodifiedType, bool isRequired) => unmodifiedType;
        public Type? GetFunctionPointerType(MethodSignature<Type?> signature) => typeof(nint);
        public Type? GetGenericMethodParameter(object? genericContext, int index) => null;
        public Type? GetGenericTypeParameter(object? genericContext, int index) => null;

        // Nested names are built outward with a bounded loop; metadata nesting is shallow.
        private string DefinitionName(TypeDefinitionHandle handle)
        {
            var names = new Stack<string>();
            var current = reader.GetTypeDefinition(handle);
            for (var depth = 0; depth < 32; depth++)
            {
                names.Push(reader.GetString(current.Name));
                var outer = current.GetDeclaringType();
                if (outer.IsNil)
                {
                    var space = reader.GetString(current.Namespace);
                    return (space.Length == 0 ? "" : space + ".") + string.Join('+', names);
                }
                current = reader.GetTypeDefinition(outer);
            }
            throw new InvalidOperationException("Type nesting is deeper than 32.");
        }

        private (string Name, EntityHandle Scope) ReferenceName(TypeReferenceHandle handle)
        {
            var names = new Stack<string>();
            var current = reader.GetTypeReference(handle);
            for (var depth = 0; depth < 32; depth++)
            {
                names.Push(reader.GetString(current.Name));
                if (current.ResolutionScope.Kind != HandleKind.TypeReference)
                {
                    var space = reader.GetString(current.Namespace);
                    return ((space.Length == 0 ? "" : space + ".") + string.Join('+', names), current.ResolutionScope);
                }
                current = reader.GetTypeReference((TypeReferenceHandle)current.ResolutionScope);
            }
            throw new InvalidOperationException("Type reference nesting is deeper than 32.");
        }
    }
}
