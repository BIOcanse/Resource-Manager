using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class JsonStartupModuleSettingsStore(IHostEnvironment environment, PackageRuntimeCatalog catalog)
    : IStartupModuleSettingsStore
{
    private readonly string path = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "SoftwareProfiles", "startup-modules.local.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<StartupModuleSettingsDocument> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<StartupModuleSelection> SaveAsync(StartupModuleSelection selection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selection.SoftwareId) || selection.SoftwareId.Length > 256
            || selection.ModuleIds is null || selection.ModuleIds.Count > PackageRuntimeCatalog.MaximumModules
            || selection.ModuleIds.Distinct(StringComparer.Ordinal).Count() != selection.ModuleIds.Count
            || selection.ModuleIds.Any(id => id is null || !catalog.TryGetStartupModule(id, out _)))
            throw new ArgumentException("Invalid startup module selection.");
        var normalized = selection with { ModuleIds = selection.ModuleIds.Order(StringComparer.Ordinal).ToArray() };
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            var next = new StartupModuleSettingsDocument(1, document.Software
                .Where(item => !item.SoftwareId.Equals(selection.SoftwareId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized).OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase).ToArray());
            if (next.Software.Count > 4096) throw new ArgumentException("Startup software limit exceeded.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, StartupModuleStorageJson.Default.StartupModuleSettingsDocument);
            if (bytes.Length > 1024 * 1024) throw new ArgumentException("Startup module settings limit exceeded.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return normalized;
        }
        finally { gate.Release(); }
    }

    private async Task<StartupModuleSettingsDocument> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var input = NativeCore.WindowsNativeAtomicFileCommitter.OpenCommittedRead(path, FileOptions.Asynchronous);
            if (input.Length > 1024 * 1024) throw new InvalidDataException("Startup module settings limit exceeded.");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            var document = JsonSerializer.Deserialize(bytes, StartupModuleStorageJson.Default.StartupModuleSettingsDocument);
            if (document is not { Version: 1, Software: not null } || document.Software.Count > 4096
                || document.Software.Any(static item => item is null || string.IsNullOrWhiteSpace(item.SoftwareId)
                    || item.SoftwareId.Length > 256 || item.ModuleIds is null || item.ModuleIds.Count > PackageRuntimeCatalog.MaximumModules
                    || item.ModuleIds.Any(static id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
                    || item.ModuleIds.Distinct(StringComparer.Ordinal).Count() != item.ModuleIds.Count)
                || document.Software.Select(static item => item.SoftwareId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Software.Count)
                throw new InvalidDataException("Invalid startup module settings.");
            return document;
        }
        catch (FileNotFoundException) { return new(1, []); }
        catch (DirectoryNotFoundException) { return new(1, []); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StartupModuleSettingsDocument))]
internal sealed partial class StartupModuleStorageJson : JsonSerializerContext { }
