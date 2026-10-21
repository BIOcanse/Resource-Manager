using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Diagnostics;

internal static class WindowsWheaHistoryConsumer
{
    // Explicit opt-in function: root consumer invokes with its chosen UTC scope.
    // Uses real public source constructor; an empty log is a valid history result.
    internal static async Task RunWindowsAsync(WindowsWheaHistoryRequest request)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsWheaInformationOwner(request);
        var backend = owner.CreateBackend();
        if (!backend.Catalog.Fields.IsEmpty || owner.ReadHistory() is not null)
            throw new InvalidDataException("WHEA construction queried history.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var catalog = await backend.DiscoverAsync(cancellation.Token);
        var history = owner.ReadHistory() ?? throw new InvalidDataException("Explicit WHEA discovery did not publish source result.");
        if (!history.Available) { Console.WriteLine($"WHEA bounded history unavailable: {history.Error}"); return; }
        foreach (var item in history.Events)
        {
            if (!catalog.Devices.Any(device => device.Identity == item.DeviceIdentity)) throw new InvalidDataException("Original event identity missing.");
            var fields = catalog.Fields.Where(field => field.Field.DeviceIdentity == item.DeviceIdentity).Select(static field => field.Field).ToArray();
            if (fields.Length != 3) throw new InvalidDataException("Three WHEA declarations were not bound.");
            foreach (var field in fields)
            {
                var cached = backend.ReadCached(field);
                if (cached.SourceId != "windows.whea-logger") throw new InvalidDataException("Event source changed.");
                if (cached.Status == FieldObservationStatus.Current && field.FieldName == "whea_all_raw_record_reported"
                    && (item.RawRecord is not { } raw || cached.Value?.Bytes is not { } bytes || !raw.AsSpan().SequenceEqual(bytes.AsSpan())))
                    throw new InvalidDataException("Raw record package projection changed bytes.");
                if (cached.Status != FieldObservationStatus.Current && cached.Value is not null)
                    throw new InvalidDataException("Unavailable WHEA value fabricated.");
            }
        }
        Console.WriteLine($"WHEA actual scoped history: channel={request.Channel}; provider={WindowsWheaHistoryRequest.Provider}; events={history.Events.Count}; limit-reached={history.LimitReached}");
    }
}
