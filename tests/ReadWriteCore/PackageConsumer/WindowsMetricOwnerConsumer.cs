using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Infrastructure.Monitoring;

internal static class WindowsMetricOwnerConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Real default sources, restricted explicitly to OS memory. No recorded backend or host assembly.
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(),
            modeProvider: static source => source is NativeMetricSnapshotSources.WindowsMemory
                or NativeMetricSnapshotSources.WindowsVirtualMemory
                ? NativeMetricSnapshotZoneMode.Normal : NativeMetricSnapshotZoneMode.Freeze);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ReadWriteCatalog catalog;
        try { catalog = await core.DiscoverAsync(timeout.Token); }
        catch (InvalidOperationException exception) when (exception.Message == "The required exact GPU topology is unavailable.")
        {
            Console.WriteLine($"Actual Windows metric owner unavailable: {exception.Message}; no current memory claim.");
            return;
        }
        Require(owner.ReadCachedSnapshot() is null, "Catalog discovery does not publish numeric current.");
        var fields = catalog.Fields.Where(item => item.Definition.Name is "memory_all_physical_total_bytes"
            or "memory_all_commit_limit_bytes").Select(item => item.Field).ToImmutableArray();
        Require(fields.Length == 2, "Default canonical configuration declares physical memory and commit limit.");
        foreach (var field in fields)
        {
            var support = core.Query(field);
            Require(support.Read.Supported && support.Read.Dependencies.IsEmpty && !support.Write.Supported
                && support.Definition.ValueKind == "float64",
                "Original float64 OS memory contract, explicit read support and no write binding.");
        }
        var readings = await core.CollectAsync(fields, timeout.Token);
        var current = owner.ReadCachedSnapshot();
        Require(current is not null, "Actual capture publishes the original managed current owner.");
        foreach (var reading in readings)
        {
            if (reading.Status != FieldObservationStatus.Current)
            {
                Require(reading.Value is null, "Unavailable memory is not a numeric zero.");
                Console.WriteLine($"Actual Windows metric {reading.Field.FieldName}: status={reading.Status}; value unavailable; support={core.Query(reading.Field).Read.Supported}.");
                continue;
            }
            // Original revision59 defaults declare float64; full UInt64 is a separate recorded contract.
            var actual = reading.Value?.Number;
            Require(actual is { } number && double.IsFinite(number) && number > 0 && Math.Truncate(number) == number
                && reading.Value!.Integer is null && reading.ObservedAt.HasValue,
                "Real current OS memory uses its declared positive whole-byte float64 value and observation time.");
            var cached = core.ReadCached(reading.Field);
            Require(cached == reading, "Cached canonical value exactly equals the collected observation.");
            Require(ReferenceEquals(current, owner.ReadCachedSnapshot()), "ReadCached does not recapture or replace the original current.");
            Console.WriteLine($"Actual Windows metric {reading.Field.FieldName}: number={actual!.Value.ToString("R", CultureInfo.InvariantCulture)}; valueKind=float64; status={reading.Status}; support={core.Query(reading.Field).Read.Supported}; cached=no-recapture.");
        }
        Require(typeof(WindowsHardwareMetricOwner).Assembly == typeof(ReadWriteCore).Assembly, "Default owner is independently packaged.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
