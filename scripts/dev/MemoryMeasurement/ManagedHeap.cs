using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MemoryMeasurement;

/// <summary>A half-open committed GC segment range, observed during a passive read.</summary>
public sealed record HeapRange(ulong Start, ulong End);
public sealed record ManagedAssembly(string Assembly, string Part, ulong Count, ulong Bytes);
public sealed record ManagedTypeCount(string Name, string Assembly, string Part, ulong Count, ulong Bytes);
public sealed record ManagedModule(string Name, string Assembly);

public sealed class ManagedReport
{
    public string Status { get; init; } = "unavailable";
    public string? Reason { get; init; }
    public string Consistency { get; init; } = ManagedHeap.LiveConsistency;
    // Complete describes the enumeration, never the stability of the running heap.
    public bool Complete { get; init; }
    public bool RangesComplete { get; init; }
    public int RuntimeCount { get; init; }
    public List<string> RuntimeVersions { get; init; } = [];
    public List<ManagedModule> LoadedModules { get; init; } = [];
    public ulong? ObjectCount { get; init; }
    public ulong? ObjectBytes { get; init; }
    public List<HeapRange> Ranges { get; init; } = [];
    public List<ManagedAssembly> Assemblies { get; init; } = [];
    public List<ManagedTypeCount> TopTypes { get; init; } = [];
    public List<ManagedTypeCount> TypeCounts { get; init; } = [];

    /// <summary>Apply a classifier after the caller has observed the actual loaded module paths.</summary>
    public void Reclassify(Func<string, string> classify)
    {
        ArgumentNullException.ThrowIfNull(classify);
        var identities = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string path)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!identities.TryAdd(key, path) && !string.Equals(identities[key], path, StringComparison.OrdinalIgnoreCase))
                identities[key] = null; // An ambiguous simple assembly name must not guess a path.
        }
        int index = 0;
        while (index < LoadedModules.Count)
        {
            var module = LoadedModules[index++];
            if (string.IsNullOrWhiteSpace(module.Name)) continue;
            Add(module.Name, module.Name);
            Add(module.Assembly, module.Name);
            Add(Path.GetFileName(module.Name), module.Name);
            Add(Path.GetFileNameWithoutExtension(module.Name), module.Name);
            Add(module.Assembly.Split(',', 2)[0].Trim(), module.Name);
        }
        string Part(string assembly)
        {
            if (Path.IsPathFullyQualified(assembly)) return classify(assembly);
            if (identities.TryGetValue(assembly, out string? path) && path is not null) return classify(path);
            string simple = Path.GetFileNameWithoutExtension(assembly.Split(',', 2)[0].Trim());
            return classify(identities.TryGetValue(simple, out path) && path is not null ? path : assembly);
        }
        index = 0;
        while (index < Assemblies.Count)
        {
            var item = Assemblies[index];
            Assemblies[index++] = item with { Part = Part(item.Assembly) };
        }
        index = 0;
        while (index < TypeCounts.Count)
        {
            var item = TypeCounts[index];
            TypeCounts[index++] = item with { Part = Part(item.Assembly) };
        }
        index = 0;
        while (index < TopTypes.Count)
        {
            var item = TopTypes[index];
            TopTypes[index++] = item with { Part = Part(item.Assembly) };
        }
    }
}

/// <summary>Optional cache-only ClrMD integration. Never suspends, dumps, or downloads.</summary>
internal static class ManagedHeap
{
    internal const string LiveConsistency =
        "Passive live read; no stable snapshot. ClrMD explicitly does not support inspecting a running " +
        "process and documents undefined behavior. Counts are exact aggregates of the objects enumerated " +
        "during this read, excluding free objects; concurrent allocation/GC may change or invalidate them. " +
        "Complete means enumeration finished without a detected error or limit, not snapshot consistency.";

    private const int MaxRuntimes = 8;
    private const int MaxRanges = 65_536;
    private const int MaxTypes = 100_000;
    private const ulong MaxObjects = 10_000_000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120);
    private static readonly object LoaderLock = new();

    internal static ManagedReport Read(int pid, bool walk, string? clrMdPath, Func<string, string> classify)
    {
        var ranges = new List<HeapRange>();
        var rangeSet = new HashSet<HeapRange>();
        var types = new Dictionary<(string Name, string Assembly, string Part), Bucket>();
        var methods = new Dictionary<(int Runtime, ulong MethodTable), Bucket>();
        var loadedModules = new Dictionary<string, ManagedModule>(StringComparer.OrdinalIgnoreCase);
        var runtimeVersions = new List<string>();
        var elapsed = Stopwatch.StartNew();
        int runtimeCount = 0;
        bool rangesComplete = false;
        ulong visited = 0;
        ulong objectCount = 0;
        ulong objectBytes = 0;
        string? walkFailure = null;
        ObjectAccess? access = null;
        object? target = null;
        Func<AssemblyLoadContext, AssemblyName, Assembly?>? resolver = null;

        void CheckTime()
        {
            JobStop.Check();
            if (elapsed.Elapsed >= MaxDuration)
                throw new InvalidOperationException("The 120-second managed-read limit was reached.");
        }

        ManagedReport Failed(string reason) => new()
        {
            Status = runtimeCount == 0 ? "unavailable" : "incomplete",
            Reason = reason,
            RuntimeCount = runtimeCount,
            RuntimeVersions = runtimeVersions,
            LoadedModules = loadedModules.Values.ToList(),
            RangesComplete = rangesComplete,
            Ranges = ranges
        };

        try
        {
            if (pid <= 0) return Failed("The process ID must be positive.");
            ArgumentNullException.ThrowIfNull(classify);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(profile, ".nuget", "packages");
            string dll = string.IsNullOrWhiteSpace(clrMdPath)
                ? CachedLibrary(cache, "microsoft.diagnostics.runtime", "Microsoft.Diagnostics.Runtime.dll", "3.1.512801")
                : Path.GetFullPath(clrMdPath);
            if (!File.Exists(dll)) return Failed("Optional ClrMD DLL is absent; supply --clrmd or populate the existing local cache. No packages were downloaded.");
            dll = Path.GetFullPath(dll);
            string directory = Path.GetDirectoryName(dll)!;
            resolver = (_, name) => ResolveLocalDependency(name, directory, cache);
            Assembly clrMd;
            lock (LoaderLock)
            {
                AssemblyLoadContext.Default.Resolving += resolver;
                clrMd = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            }
            CheckTime();
            Type targetType = clrMd.GetType("Microsoft.Diagnostics.Runtime.DataTarget", throwOnError: true)!;
            MethodInfo attach = RequiredMethod(targetType, "AttachToProcess", typeof(int), typeof(bool));
            // false is mandatory: do not suspend the target or create a snapshot.
            target = attach.Invoke(null, [pid, false]) ?? throw new InvalidOperationException("ClrMD did not return a data target.");
            // Also replace the default symbol locator: _NT_SYMBOL_PATH must not silently
            // enable network lookups while ClrMD resolves managed module metadata.
            Type locatorInterface = clrMd.GetType("Microsoft.Diagnostics.Runtime.IFileLocator", throwOnError: true)!;
            object localLocator = DispatchProxy.Create(locatorInterface, typeof(ManagedLocalFileLocator));
            (targetType.GetProperty("FileLocator") ?? throw new MissingMemberException(targetType.FullName, "FileLocator"))
                .SetValue(target, localLocator);
            CheckTime();

            using var runtimes = Iterate(Get(target, "ClrVersions"));
            while (runtimes.MoveNext())
            {
                CheckTime();
                if (runtimeCount == MaxRuntimes) throw new InvalidOperationException("The eight-runtime limit was reached.");
                object info = runtimes.Current!;
                int runtimeIndex = runtimeCount++;
                string version = Get(info, "Version")?.ToString() ?? "unknown";
                runtimeVersions.Add(version);
                object moduleInfo = Get(info, "ModuleInfo") ?? throw new InvalidOperationException("The CLR module is unavailable.");
                string modulePath = Get(moduleInfo, "FileName") as string ?? "";
                if (!Path.IsPathFullyQualified(modulePath)) throw new InvalidOperationException("The target CLR module has no absolute local path; matching DAC lookup is unavailable.");
                string localDac = Path.Combine(Path.GetDirectoryName(modulePath)!, "mscordaccore.dll");
                if (!File.Exists(localDac)) throw new InvalidOperationException("A matching local mscordaccore.dll was not found beside the target CLR module. Symbol downloads are disabled.");
                // This overload verifies the local DAC match; never use parameterless CreateRuntime.
                object runtime = RequiredMethod(info.GetType(), "CreateRuntime", typeof(string)).Invoke(info, [localDac])
                    ?? throw new InvalidOperationException("ClrMD did not create a runtime with the local DAC.");
                try
                {
                    using (var modules = Iterate(RequiredMethod(runtime.GetType(), "EnumerateModules").Invoke(runtime, null)))
                    {
                        int moduleVisits = 0;
                        while (modules.MoveNext())
                        {
                            CheckTime();
                            if (moduleVisits++ == MaxTypes) throw new InvalidOperationException("The 100,000-module visit limit was reached.");
                            object module = modules.Current!;
                            string name = Get(module, "Name") as string ?? "";
                            string identity = Get(module, "AssemblyName") as string ?? "";
                            string key = string.IsNullOrWhiteSpace(name) ? identity : name;
                            if (string.IsNullOrWhiteSpace(key)) continue;
                            if (!loadedModules.ContainsKey(key))
                            {
                                if (loadedModules.Count == MaxTypes) throw new InvalidOperationException("The 100,000-loaded-module limit was reached.");
                                loadedModules.Add(key, new ManagedModule(name, identity));
                            }
                        }
                    }
                    object heap = Get(runtime, "Heap") ?? throw new InvalidOperationException("The managed heap is unavailable.");
                    bool canWalk = Get(heap, "CanWalkHeap") is true;
                    if (walk && !canWalk) walkFailure ??= "ClrMD CanWalkHeap is false; complete managed counts are unavailable.";
                    using var segments = Iterate(Get(heap, "Segments"));
                    while (segments.MoveNext())
                    {
                        CheckTime();
                        object segment = segments.Current!;
                        object committed = Get(segment, "CommittedMemory") ?? throw new InvalidOperationException("A GC segment has no committed memory range.");
                        var range = new HeapRange(Number(committed, "Start"), Number(committed, "End"));
                        if (range.End < range.Start) throw new InvalidOperationException("A GC committed range is invalid.");
                        if (range.End > range.Start)
                        {
                            if (!rangeSet.Add(range)) throw new InvalidOperationException("A duplicate GC committed range was encountered.");
                            if (ranges.Count == MaxRanges) throw new InvalidOperationException("The 65,536 GC segment range limit was reached.");
                            ranges.Add(range);
                        }
                        if (!walk || !canWalk || walkFailure is not null) continue;
                        try
                        {
                            ulong segmentStart = Number(segment, "Start");
                            ulong segmentEnd = Number(segment, "End");
                            ulong previousAddress = 0;
                            object enumerable = RequiredMethod(segment.GetType(), "EnumerateObjects", typeof(bool)).Invoke(segment, [false])!;
                            using var objects = Iterate(enumerable);
                            while (objects.MoveNext())
                            {
                                CheckTime();
                                if (visited++ == MaxObjects) throw new InvalidOperationException("The 10,000,000-object visit limit was reached.");
                                object obj = objects.Current ?? throw new InvalidOperationException("ClrMD enumerated a null object.");
                                access ??= new ObjectAccess(obj.GetType());
                                ulong address = access.Address(obj);
                                if (address < segmentStart || address >= segmentEnd || (previousAddress != 0 && address <= previousAddress))
                                    throw new InvalidOperationException("Object enumeration returned an out-of-range or repeated address.");
                                previousAddress = address;
                                if (!access.IsValid(obj)) throw new InvalidOperationException("Object enumeration encountered an invalid object.");
                                if (access.IsFree(obj)) continue;
                                object type = access.Type(obj) ?? throw new InvalidOperationException("An enumerated object has no type.");
                                ulong size = access.Size(obj);
                                if (size == 0 || size > segmentEnd - address) throw new InvalidOperationException("An enumerated object has an invalid size.");
                                ulong methodTable = access.MethodTable(type);
                                if (methodTable == 0) throw new InvalidOperationException("An enumerated type has no method table.");
                                if (!methods.TryGetValue((runtimeIndex, methodTable), out Bucket? bucket))
                                {
                                    if (methods.Count == MaxTypes) throw new InvalidOperationException("The 100,000-type limit was reached.");
                                    string name = Get(type, "Name") as string ?? "";
                                    if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("An enumerated type has no name.");
                                    string assembly = AssemblyPath(type);
                                    string part = classify(assembly);
                                    var key = (name, assembly, part);
                                    if (!types.TryGetValue(key, out bucket))
                                    {
                                        bucket = new Bucket(name, assembly, part);
                                        types.Add(key, bucket);
                                    }
                                    methods.Add((runtimeIndex, methodTable), bucket);
                                }
                                bucket.Count = checked(bucket.Count + 1);
                                bucket.Bytes = checked(bucket.Bytes + size);
                                objectCount = checked(objectCount + 1);
                                objectBytes = checked(objectBytes + size);
                            }
                            CheckTime();
                        }
                        catch (Exception ex) { walkFailure ??= Explain(ex); }
                    }
                    CheckTime();
                    if (walk && Get(heap, "CanWalkHeap") is not true)
                        walkFailure ??= "ClrMD CanWalkHeap became false during enumeration.";
                }
                finally { (runtime as IDisposable)?.Dispose(); }
            }
            CheckTime();
            rangesComplete = true;
            if (runtimeCount == 0) return new ManagedReport { Status = "not-managed", Reason = "No CLR runtime was found.", RangesComplete = true };
            if (!walk) return new ManagedReport { Status = "ranges-only", Reason = "Managed object enumeration was not requested.", RangesComplete = true, RuntimeCount = runtimeCount, RuntimeVersions = runtimeVersions, LoadedModules = loadedModules.Values.ToList(), Ranges = ranges };
            if (walkFailure is not null) return Failed(walkFailure);

            List<ManagedTypeCount> counts = types.Values.Select(b => new ManagedTypeCount(b.Name, b.Assembly, b.Part, b.Count, b.Bytes))
                .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Assembly, StringComparer.Ordinal).ToList();
            var assemblyCounts = new Dictionary<(string Assembly, string Part), (ulong Count, ulong Bytes)>();
            int index = 0;
            while (index < counts.Count)
            {
                var item = counts[index++];
                var key = (item.Assembly, item.Part);
                assemblyCounts.TryGetValue(key, out var sum);
                assemblyCounts[key] = (checked(sum.Count + item.Count), checked(sum.Bytes + item.Bytes));
            }
            return new ManagedReport
            {
                Status = "complete", Complete = true, RangesComplete = true, RuntimeCount = runtimeCount,
                RuntimeVersions = runtimeVersions, LoadedModules = loadedModules.Values.ToList(),
                ObjectCount = objectCount, ObjectBytes = objectBytes, Ranges = ranges, TypeCounts = counts,
                TopTypes = counts.Take(30).ToList(),
                Assemblies = assemblyCounts.Select(a => new ManagedAssembly(a.Key.Assembly, a.Key.Part, a.Value.Count, a.Value.Bytes))
                    .OrderByDescending(a => a.Bytes).ThenBy(a => a.Assembly, StringComparer.Ordinal).ToList()
            };
        }
        catch (Exception ex) { return Failed(Explain(ex)); }
        finally
        {
            try { (target as IDisposable)?.Dispose(); }
            catch { /* A failed detach must not prevent delivery of the native report. */ }
            if (resolver is not null) AssemblyLoadContext.Default.Resolving -= resolver;
        }
    }

    private static string AssemblyPath(object type)
    {
        object? module = Get(type, "Module");
        if (module is null) return "unresolved";
        string? path = Get(module, "Name") as string;
        if (!string.IsNullOrWhiteSpace(path)) return path;
        string? identity = Get(module, "AssemblyName") as string;
        if (string.IsNullOrWhiteSpace(identity)) return "unresolved";
        string name = identity.Split(',', 2)[0].Trim();
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
    }

    private static Assembly? ResolveLocalDependency(AssemblyName name, string directory, string cache)
    {
        string simple = name.Name ?? "";
        // Only load dependencies required by ClrMD/client from local files. Framework assemblies
        // are normally resolved by the host; extensions may live in the installed ASP.NET framework.
        if (simple != "Microsoft.Diagnostics.NETCore.Client" && simple != "Microsoft.Bcl.AsyncInterfaces" &&
            !simple.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)) return null;
        var candidates = new List<string> { Path.Combine(directory, simple + ".dll") };
        if (simple == "Microsoft.Diagnostics.NETCore.Client")
            candidates.Add(CachedLibrary(cache, "microsoft.diagnostics.netcore.client", simple + ".dll", "0.2.410101"));
        if (simple == "Microsoft.Bcl.AsyncInterfaces")
            candidates.Add(Path.Combine(cache, "microsoft.bcl.asyncinterfaces", "1.1.0", "lib", "netstandard2.0", simple + ".dll"));
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        candidates.Add(Path.Combine(runtimeDirectory, simple + ".dll"));
        string? shared = Directory.GetParent(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName;
        if (shared is not null)
        {
            string aspnet = Path.Combine(shared, "Microsoft.AspNetCore.App");
            if (Directory.Exists(aspnet))
            {
                string currentVersion = Path.GetFileName(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar));
                candidates.Add(Path.Combine(aspnet, currentVersion, simple + ".dll"));
                // Do not enumerate unbounded user directories or load arbitrary package versions.
                using var versions = Directory.EnumerateDirectories(aspnet).Take(128).OrderByDescending(p => p, StringComparer.Ordinal).GetEnumerator();
                while (versions.MoveNext()) candidates.Add(Path.Combine(versions.Current, simple + ".dll"));
            }
        }
        int i = 0;
        while (i < candidates.Count)
        {
            string candidate = candidates[i++];
            if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(candidate));
        }
        return null;
    }

    private static string CachedLibrary(string cache, string package, string library, string preferred)
    {
        string root = Path.Combine(cache, package);
        string known = Path.Combine(root, preferred, "lib", "net6.0", library);
        if (File.Exists(known)) return known;
        if (!Directory.Exists(root)) return known;
        var versions = new List<string>();
        foreach (string version in Directory.EnumerateDirectories(root))
        {
            if (versions.Count == 128) throw new InvalidOperationException("128 cached-package-version limit exceeded; supply --clrmd explicitly.");
            versions.Add(version);
        }
        versions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(b, a));
        foreach (string version in versions)
            foreach (string framework in new[] { "net10.0", "net9.0", "net8.0", "net6.0", "netstandard2.0" })
            {
                string candidate = Path.Combine(version, "lib", framework, library);
                if (File.Exists(candidate)) return candidate;
            }
        return known;
    }

    private static object? Get(object value, string property) =>
        (value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(value.GetType().FullName, property)).GetValue(value);
    private static ulong Number(object value, string property) => Convert.ToUInt64(Get(value, property));
    private static MethodInfo RequiredMethod(Type type, string name, params Type[] arguments) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, arguments)
            ?? throw new MissingMethodException(type.FullName, name);
    private static DisposableEnumerator Iterate(object? value) => new(
        (value as IEnumerable ?? throw new InvalidOperationException("ClrMD did not return an enumerable collection.")).GetEnumerator());
    private static string Explain(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } wrapped) exception = wrapped.InnerException!;
        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private sealed class DisposableEnumerator(IEnumerator inner) : IDisposable
    {
        public object? Current => inner.Current;
        public bool MoveNext() => inner.MoveNext();
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    private sealed class Bucket(string name, string assembly, string part)
    {
        public readonly string Name = name;
        public readonly string Assembly = assembly;
        public readonly string Part = part;
        public ulong Count;
        public ulong Bytes;
    }

    // Compile accessors once per read rather than reflecting/binding each object property.
    // Only the current boxed ClrObject is held; no object-address or reference graph is retained.
    private sealed class ObjectAccess(Type type)
    {
        public readonly Func<object, ulong> Address = Getter<ulong>(type, "Address");
        public readonly Func<object, ulong> Size = Getter<ulong>(type, "Size");
        public readonly Func<object, bool> IsValid = Getter<bool>(type, "IsValid");
        public readonly Func<object, bool> IsFree = Getter<bool>(type, "IsFree");
        public readonly Func<object, object?> Type = Getter<object?>(type, "Type");
        public readonly Func<object, ulong> MethodTable = Getter<ulong>(
            type.GetProperty("Type")?.PropertyType ?? throw new MissingMemberException(type.FullName, "Type"), "MethodTable");
    }

    private static Func<object, T> Getter<T>(Type type, string property)
    {
        var value = Expression.Parameter(typeof(object));
        var body = Expression.Convert(Expression.Property(Expression.Convert(value, type), property), typeof(T));
        return Expression.Lambda<Func<object, T>>(body, value).Compile();
    }
}

/// <summary>Reflection-created ClrMD IFileLocator that cannot contact symbol servers.</summary>
public class ManagedLocalFileLocator : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.ReturnType != typeof(string))
            throw new NotSupportedException("Only local ClrMD file lookup is supported.");
        string? path = args is { Length: > 0 } ? args[0] as string : null;
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
            !path.StartsWith("\\\\", StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }
}
