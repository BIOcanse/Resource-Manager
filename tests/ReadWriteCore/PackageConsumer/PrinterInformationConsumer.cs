using System.Collections.Concurrent;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Printers;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class PrinterInformationConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var reader = new RecordedReader();
        var scope = new WindowsPrinterInformationScope(
            WindowsPrinterInformationFields.All,
            WindowsPrinterEnumerationScope.LocalAndConnections,
            maximumPrinters: 8,
            maximumDriverEntriesPerServer: 32,
            maximumNativeBufferBytes: 128 * 1024,
            maximumStringCharacters: 256);
        var access = owner.CreatePrinterInformation(scope, reader);
        var backend = access.Backend;
        Require(backend.Catalog.Fields.IsEmpty && reader.Requests.IsEmpty,
            "construction and catalog inspection are passive");

        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        Require(reader.Requests.Single() == WindowsPrinterInformationFields.All,
            "explicit discovery requested only the configured printer fields");
        var version = catalog.Fields.Single(static field =>
            field.Field.FieldName == "printer_all_driver_version_declared").Field;
        var port = catalog.Fields.Single(static field =>
            field.Field.FieldName == "printer_all_port_declared").Field;
        var state = catalog.Fields.Single(static field =>
            field.Field.FieldName == "printer_all_queue_state_actual").Field;
        Require(catalog.Devices.All(static device => device.Resource == "printer"),
            "the directory binds only printer objects");
        Require(catalog.Fields.Single(static field => field.Field.FieldName == "printer_all_driver_version_declared").Definition.Unit == "text"
            && catalog.Fields.Single(static field => field.Field.FieldName == "printer_all_port_declared").Definition.Unit == "text"
            && catalog.Fields.Single(static field => field.Field.FieldName == "printer_all_queue_state_actual").Definition.Unit == "enum",
            "canonical units remain text, text, and enum");

        var ownerBackend = owner.CreateBackend();
        Require(backend.ReadCached(version).Value?.Text == "18446744073709551615"
            && backend.ReadCached(port).Value?.Text == "USB001:,FILE:"
            && backend.ReadCached(state).Value?.Integer == "2147484800",
            "recorded source values preserve unsigned version, multi-port text and combined raw status bits");
        Require(backend.ReadCached(port) == ownerBackend.ReadCached(port),
            "scoped cache is the original topology owner's current");
        var readCount = reader.Requests.Count;
        var selected = await backend.CollectAsync([port], CancellationToken.None);
        Require(selected.Single().Value?.Text == "USB001:,FILE:"
            && reader.Requests.Last() == WindowsPrinterInformationFields.PortDeclared,
            "direct collection reads one selected field");
        Require(backend.ReadCached(port) == ownerBackend.ReadCached(port)
            && backend.ReadCached(port) == selected.Single(),
            "direct and cached reads observe the same original current");
        Require(reader.Requests.Count == readCount + 1, "only explicit collection invoked the provider");

        Console.WriteLine("[PRINTER-INFORMATION-PACKAGE] recorded canonical printer discovery, exact fields, selected direct capture and shared current; no live printer read.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"Printer information package consumer failed: {message}.");
    }

    private sealed class RecordedReader : IWindowsPrinterInformationReader
    {
        private static readonly DeviceTopologyPrinterInformation Printer = new(
            "Office Queue", "srv", "USB001:,FILE:",
            WindowsPrinterQueueStatusFlags.Printing | WindowsPrinterQueueStatusFlags.Offline
                | (WindowsPrinterQueueStatusFlags)0x80000000,
            ulong.MaxValue);

        public ConcurrentQueue<WindowsPrinterInformationFields> Requests { get; } = new();

        public WindowsPrinterInformationReadResult Read(WindowsPrinterInformationScope scope,
            WindowsPrinterInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(selectedFields);
            return new(true, [Printer], []);
        }
    }
}
