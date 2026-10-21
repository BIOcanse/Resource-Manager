using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class ProcessGpuResidencyConsumer
{
    // Real readonly PDH only when explicitly invoked by the central Windows harness.
    // No GPU workload, ETW subscription, setter, installation or hidden retry is started.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        using var http = new HttpClient();
        var api = new ReadWriteCore(owner.Backend, new DependencyActions(http, []));
        await api.DiscoverAsync(timeout.Token); // original process identity/basic capture only
        await owner.CollectAsync([WindowsPdhProcessGpuFields.FieldName], timeout.Token); // explicit first baseline
        await Task.Delay(100, timeout.Token); // caller chooses a second sample beyond original 50ms reuse
        await owner.CollectAsync([WindowsPdhProcessGpuFields.FieldName], timeout.Token);
        var fields = api.Catalog.Fields.Where(field => field.Field.FieldName == WindowsPdhProcessGpuFields.FieldName)
            .Select(field => field.Field).ToImmutableArray();
        if (fields.IsEmpty)
        { Console.WriteLine("GPU residency actual PDH path: no reported verified process/engine scopes; unavailable, no numeric success claimed."); return; }
        var readings = await api.CollectAsync(fields, timeout.Token);
        foreach (var reading in readings)
        {
            if (!api.Catalog.Fields.Any(field => field.Field == reading.Field))
            { Console.WriteLine($"GPU residency scope retired during explicit capture: {reading.Field.DeviceIdentity}; unavailable cached-address branch."); continue; }
            _ = api.Query(reading.Field);
            if (api.ReadCached(reading.Field) != reading) throw new InvalidDataException("GPU current was recaptured or changed.");
            if (reading.Value?.Number is { } busy && (!double.IsFinite(busy) || busy is < 0 or > 100
                || reading.ObservedAt is null || reading.SampleDurationMilliseconds is null))
                throw new InvalidDataException("GPU execution interval proof missing.");
        }
        using var borrow = owner.BorrowSubscriptionCapture();
        if (!ReferenceEquals(borrow.Backend, owner.Backend) || !ReferenceEquals(borrow.Publications, owner.Publications))
            throw new InvalidDataException("Subscription current diverged.");
        _ = borrow.Bind(fields); // existing external composition can use this same typed binding
        Console.WriteLine($"GPU residency actual readonly scopes={fields.Length}; numeric={readings.Count(row => row.Value?.Number is not null)}; unavailable={readings.Count(row => row.Value is null)}; no summed engines or fresh LastGood claim.");
    }
}
