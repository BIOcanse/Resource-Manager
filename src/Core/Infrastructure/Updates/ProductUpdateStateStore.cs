using System.Text.Json;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Updates;

// Config survives package switches and participates in the updater's data backup.
public sealed class ProductUpdateStateStore(string installRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
    private string StatePath => Path.Combine(root, "Config", "product-update-state.json");

    public ProductUpdateState? Read()
        => File.Exists(StatePath) ? ReadJson<ProductUpdateState>(StatePath) : null;

    public void Write(ProductUpdateState state)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        ReleasePackageLayout.RejectReparse(directory);
        if (File.Exists(StatePath)) ReleasePackageLayout.RejectReparse(StatePath);
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ProductUpdateState ProjectOutcome(ProductUpdateState state)
    {
        string? outcomePackageRoot = null;
        var resultPath = Path.Combine(UpdateManagerPaths.InstalledDirectory(root), "last-operation-result.json");
        if (File.Exists(resultPath))
        {
            using var result = ReadDocument(resultPath);
            var data = result.RootElement;
            if (Get(data, "Arguments") is { ValueKind: JsonValueKind.Array } arguments)
            {
                var args = arguments.EnumerateArray().Select(value => value.GetString()).ToArray();
                var finished = Date(data, "FinishedAt");
                if (args.Length == 3 && args[0] == "--apply" && SameRoot(args[2]) && finished >= state.UpdatedAt)
                {
                    var success = Get(data, "Success")?.ValueKind == JsonValueKind.True;
                    var code = success ? null : Text(data, "FailureCode") ?? "update.applyFailed";
                    var target = Text(data, "TargetVersion") ?? state.TargetVersion;
                    IReadOnlyDictionary<string, string>? parameters = target is null ? null
                        : new Dictionary<string, string> { ["targetVersion"] = target };
                    if (Get(data, "FailureParameters") is { ValueKind: JsonValueKind.Array } failureParameters)
                    {
                        var values = failureParameters.EnumerateArray().Select(value => value.GetString()).ToArray();
                        if (values is [not null, not null]) parameters = new Dictionary<string, string>
                        {
                            ["installedVersion"] = values[0]!, ["targetVersion"] = values[1]!
                        };
                    }
                    outcomePackageRoot = args[1];
                    state = state with
                    {
                        Stage = success ? "completed" : "error", Error = code, ErrorCode = code,
                        TargetVersion = target, Workspace = args[1] is null ? state.Workspace : Path.GetDirectoryName(args[1]),
                        ErrorParameters = parameters,
                        UpdatedAt = finished, FailedVersion = success ? state.FailedVersion : target
                    };
                }
            }
        }

        // The service can restart before the manager writes its result. The transaction
        // journal is already durable at that point and also distinguishes rollback.
        var parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent)) return state;
        var inspected = 0;
        foreach (var workspace in Directory.EnumerateDirectories(parent, "ResourceManager-update-*"))
        {
            if (++inspected > 100_000) throw new InvalidDataException("Update history exceeds the entry limit.");
            var path = Path.Combine(workspace, "update-transaction.json");
            if (!File.Exists(path)) continue;
            ReleasePackageLayout.RejectReparse(workspace);
            using var journal = ReadDocument(path);
            var data = journal.RootElement;
            if (!SameRoot(Text(data, "InstallRoot"))) continue;
            var changed = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            var version = Text(data, "targetVersion");
            var stage = Text(data, "stage");
            var terminal = stage is "committed" or "rolledBack" or "recovered" or "recoveryFailed";
            var sourcePackageRoot = Text(data, "sourcePackageRoot");
            var sameAttempt = sourcePackageRoot is not null && string.Equals(Path.GetFullPath(sourcePackageRoot),
                Path.GetFullPath(outcomePackageRoot ?? Path.Combine(state.Workspace ?? root, "package")), StringComparison.OrdinalIgnoreCase)
                || string.Equals(workspace, state.Workspace, StringComparison.OrdinalIgnoreCase);
            // A matching failure result can be written just after rollback. Preserve
            // that transaction's more specific disposition instead of flattening it.
            if (changed < state.UpdatedAt && !(terminal && sameAttempt
                && ProductUpdateCoordinator.VersionIdentity(version) == ProductUpdateCoordinator.VersionIdentity(state.TargetVersion)
                && state.Stage == "error" && stage is "rolledBack" or "recovered" or "recoveryFailed")) continue;
            var projected = stage switch
            {
                "committed" => "completed",
                "rolledBack" or "recovered" => "rolledBack",
                "recoveryFailed" => "error",
                _ => "waitingForExit"
            };
            var code = projected == "rolledBack" ? "update.rolledBack"
                : projected == "error" ? "update.recoveryRequired" : null;
            state = state with
            {
                Stage = projected, TargetVersion = version, Workspace = workspace,
                Error = code, ErrorCode = code, UpdatedAt = changed > state.UpdatedAt ? changed : state.UpdatedAt,
                FailedVersion = projected is "rolledBack" or "error" ? version : state.FailedVersion
            };
        }
        return state;
    }

    private bool SameRoot(string? path) => path is not null && string.Equals(
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);

    private static T? ReadJson<T>(string path)
    {
        using var document = ReadDocument(path);
        return document.RootElement.Deserialize<T>(Json);
    }

    private static JsonDocument ReadDocument(string path)
    {
        ReleasePackageLayout.RejectReparse(path);
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Invalid update state record.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement? Get(JsonElement data, string name)
    {
        foreach (var property in data.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
    private static string? Text(JsonElement data, string name)
        => Get(data, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static DateTimeOffset Date(JsonElement data, string name)
        => DateTimeOffset.TryParse(Text(data, name), out var value) ? value : DateTimeOffset.MinValue;
}
