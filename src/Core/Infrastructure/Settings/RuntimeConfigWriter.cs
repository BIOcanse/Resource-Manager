using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>The two GC values of the memory/CPU tier options that only a process restart applies.
/// <c>Gen0MaxBudget</c> null means not set (the key is absent and the runtime sizes gen0 itself).</summary>
public readonly record struct RuntimeGcSettings(long? Gen0MaxBudget, int ConserveMemory);

/// <summary>One runtimeconfig file this writer owns: the process it configures and its path.</summary>
public sealed record RuntimeConfigFile(string Process, string Path);

/// <summary>What the writer can report: whether it writes (installed service) and why not, the files,
/// what the service runs with now, what the files hold for the next start, and the last write outcome.</summary>
public sealed record RuntimeConfigState(
    bool Writable,
    string? Reason,
    IReadOnlyList<RuntimeConfigFile> Files,
    RuntimeGcSettings Running,
    RuntimeGcSettings NextStart,
    string? LastFailure);

/// <summary>
/// Writes <c>System.GC.Gen0MaxBudget</c> and <c>System.GC.ConserveMemory</c> into the installed service and
/// NativeUi runtimeconfig files when their effective values change. It reads each file first, changes only
/// those two keys and replaces the file atomically; it never restarts anything. A service that does not run
/// from an installed <c>Bin\ResourceManager</c> directory (a dev run, tests) writes nothing and says so.
/// </summary>
public sealed class RuntimeConfigWriter
{
    public const string Gen0MaxBudgetKey = "System.GC.Gen0MaxBudget";
    public const string ConserveMemoryKey = "System.GC.ConserveMemory";
    public const string ServiceProcess = "service";
    public const string NativeUiProcess = "nativeUi";
    public const string NotInstalledReason = "not-installed: the service does not run from an installed Bin\\ResourceManager directory, so no runtimeconfig is written";
    /// <summary>The service cannot read another process's GC settings; NativeUi's value is the one written for its next start.</summary>
    public const string NativeUiRunningUnavailable = "NativeUi is a separate process whose GC settings the service cannot read; its value is the one written for its next start";

    private readonly object gate = new();
    private readonly IReadOnlyList<RuntimeConfigFile>? files;
    private RuntimeGcSettings? lastEffective;
    private RuntimeGcSettings nextStart;
    private string? lastFailure;

    public RuntimeConfigWriter() : this(InstalledFiles(AppContext.BaseDirectory), ReadRunning()) { }

    internal RuntimeConfigWriter(IReadOnlyList<RuntimeConfigFile>? files, RuntimeGcSettings running)
    {
        this.files = files;
        Running = running;
        nextStart = running;
    }

    /// <summary>The service's GC values, read once at startup from the runtime (AppContext data).</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public RuntimeGcSettings Running { get; }

    public bool Writable => files is not null;

    public RuntimeConfigState Read()
    {
        lock (gate)
            return new(Writable, Writable ? null : NotInstalledReason, files ?? [], Running, nextStart, lastFailure);
    }

    /// <summary>Called by the settings owner with each published effective value. The first call compares
    /// with what the service runs with now, later calls with the previous effective value; only a difference
    /// writes, so the files stay untouched while the values stay the same.</summary>
    internal void Apply(RuntimeGcSettings effective, ILogger? logger = null)
    {
        lock (gate)
        {
            var baseline = lastEffective ?? Running;
            lastEffective = effective;
            if (files is null || effective == baseline) return;
            try
            {
                foreach (var file in files) WriteFile(file.Path, effective);
                nextStart = effective;
                lastFailure = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or InvalidDataException)
            {
                lastFailure = exception.Message;
                logger?.LogWarning(exception, "The memory/CPU tier GC values could not be written to the runtimeconfig files.");
            }
        }
    }

    /// <summary>Returns the runtimeconfig text with only the two GC keys set to <paramref name="values"/>
    /// (a null gen0 budget removes its key); every other property is kept as read.</summary>
    internal static string Rewrite(string json, RuntimeGcSettings values)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) as JsonObject ?? throw new InvalidDataException("The runtimeconfig root is not an object.");
        if (root["runtimeOptions"] is not JsonObject runtimeOptions)
            throw new InvalidDataException("The runtimeconfig has no runtimeOptions object.");
        if (runtimeOptions["configProperties"] is not JsonObject properties)
        {
            if (runtimeOptions.ContainsKey("configProperties"))
                throw new InvalidDataException("The runtimeconfig configProperties is not an object.");
            properties = new JsonObject();
            runtimeOptions["configProperties"] = properties;
        }
        if (values.Gen0MaxBudget is { } gen0) properties[Gen0MaxBudgetKey] = gen0;
        else properties.Remove(Gen0MaxBudgetKey);
        properties[ConserveMemoryKey] = values.ConserveMemory;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
    }

    private static void WriteFile(string path, RuntimeGcSettings values)
    {
        var text = Rewrite(File.ReadAllText(path), values);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporary, path);
        }
        catch
        {
            if (File.Exists(temporary)) WindowsNativeAtomicFileCommitter.DeleteExact(temporary);
            throw;
        }
    }

    /// <summary>The installed layout is <c>&lt;root&gt;\Bin\ResourceManager</c> (this service) next to
    /// <c>&lt;root&gt;\Bin\ResourceManagerNativeUi</c>, with the installed manager in
    /// <c>&lt;root&gt;\Internal\Management</c>. Anything else is a dev run: null, nothing is written.</summary>
    internal static IReadOnlyList<RuntimeConfigFile>? InstalledFiles(string serviceDirectory)
    {
        var service = Path.GetFullPath(serviceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bin = Path.GetDirectoryName(service);
        if (!string.Equals(Path.GetFileName(service), "ResourceManager", StringComparison.OrdinalIgnoreCase)
            || bin is null || !string.Equals(Path.GetFileName(bin), "Bin", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(bin) is not { } root || !Directory.Exists(UpdateManagerPaths.InstalledDirectory(root)))
            return null;
        var list = new List<RuntimeConfigFile>(2);
        var servicePath = Path.Combine(service, "ResourceManager.runtimeconfig.json");
        var nativeUiPath = Path.Combine(bin, "ResourceManagerNativeUi", "ResourceManager.NativeUi.runtimeconfig.json");
        if (File.Exists(servicePath)) list.Add(new(ServiceProcess, servicePath));
        if (File.Exists(nativeUiPath)) list.Add(new(NativeUiProcess, nativeUiPath));
        return list.Count == 0 ? null : list.AsReadOnly();
    }

    private static RuntimeGcSettings ReadRunning()
        => new(ReadInteger(Gen0MaxBudgetKey),
            ReadInteger(ConserveMemoryKey) is long conserve and >= int.MinValue and <= int.MaxValue ? (int)conserve : 0);

    /// <summary>Runtime config properties reach AppContext as text (decimal, or hex with 0x); absent is null.</summary>
    internal static long? ReadInteger(string key) => ParseInteger(AppContext.GetData(key));

    internal static long? ParseInteger(object? data) => data switch
    {
        null => null,
        long value => value,
        int value => value,
        string text when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) => hex,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => null,
    };
}
