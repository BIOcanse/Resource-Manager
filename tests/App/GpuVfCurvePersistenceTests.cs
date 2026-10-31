using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class GpuVfCurvePersistenceTests
{
    [Fact]
    public async Task AtomicCatalogRoundTripPreservesNativeVoltageAndOwnsImmutablePoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-vf-store-" + Guid.NewGuid().ToString("N"));
        var environment = new Environment(root);
        var path = ControlDataPaths.FilePath(root, "gpu-vf-curves.json");
        try
        {
            using var store = new JsonGpuVfCurvePresetStore(environment);
            var descriptor = GpuVfCurveServiceTests.Descriptor();
            var points = descriptor.Points.Select(p => new GpuVfCurvePoint(p.PointId, p.VoltageMicrovolts, p.CurrentOffsetKhz)).ToList();
            var preset = new GpuVfCurvePreset("curve", "Precise native values", descriptor.Binding!, points,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await store.SaveAsync(new([preset]), default);
            points[0] = points[0] with { FrequencyOffsetKhz = 99999 };
            var cached = await store.LoadAsync(default);
            Assert.Equal(15000, cached.Presets[0].Points[0].FrequencyOffsetKhz);
            using var reopened = new JsonGpuVfCurvePresetStore(environment);
            var read = await reopened.LoadAsync(default);
            Assert.Equal(700000, read.Presets[0].Points[0].VoltageMicrovolts);
            Assert.Equal(7, read.Presets[0].Points[0].PointId);
            Assert.Equal(descriptor.Binding, read.Presets[0].Binding);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { RemoveOwnedFiles(path, root); }
    }

    [Fact]
    public async Task CorruptCatalogFailsWithoutSilentlyDiscardingSavedCurves()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-vf-corrupt-" + Guid.NewGuid().ToString("N"));
        var path = ControlDataPaths.FilePath(root, "gpu-vf-curves.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string damaged = "{\"presets\":[";
            await File.WriteAllTextAsync(path, damaged);
            using var store = new JsonGpuVfCurvePresetStore(new Environment(root));
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync(default));
            Assert.Equal(damaged, await File.ReadAllTextAsync(path));
        }
        finally { RemoveOwnedFiles(path, root); }
    }

    private static void RemoveOwnedFiles(string path, string root)
    {
        if (File.Exists(path)) File.Delete(path);
        var control = Path.GetDirectoryName(path)!;
        if (Directory.Exists(control)) Directory.Delete(control);
        var userData = Path.GetDirectoryName(control)!;
        if (Directory.Exists(userData)) Directory.Delete(userData);
        if (Directory.Exists(root)) Directory.Delete(root);
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "GpuVfCurvePersistenceTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
