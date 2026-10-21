using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Printers;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class PrinterInformationSubscriptionConsumer
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
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var state = catalog.Fields.Single(static field =>
            field.Field.FieldName == "printer_all_queue_state_actual").Field;
        var borrow = access.BorrowSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(2, 1_000, 1, 3_000, 1_000, 1_000);
        var configuration = Configuration();
        var failures = new ConcurrentQueue<string>();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"), TimeSpan.FromMilliseconds(10));

        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var stream = composition.SubscribeAsync("recorded-printer", [state],
            TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token),
                "explicit role-2 subscription published a selected reading");
            var reading = AssertSingle(stream.Current);
            Require(reading.Status == FieldObservationStatus.Current
                && reading.Value?.Integer == "1152" && reading.ObservedAt.HasValue,
                "recorded combined queue-state flags remain a current integer bitmask");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(state));
            Require(reader.Requests.Last() == WindowsPrinterInformationFields.QueueStateActual,
                "borrowed subscription capture reads only the selected printer field");
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync(), "explicit stop retires the selected stream");
        }
        Require(failures.IsEmpty, "recorded subscription completed without provider or worker failures");

        Console.WriteLine("[PRINTER-INFORMATION-SUBSCRIPTION] recorded role-2 selected printer bitmask, same-current read and explicit Start/Stop; no live printer read.");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1_000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3_000,
        MaximumFutureSkewMilliseconds = 1_000
    };

    private static FieldReading AssertSingle(ImmutableArray<FieldReading> readings)
    {
        if (readings.Length != 1) throw new InvalidDataException("Expected exactly one printer field reading.");
        return readings[0];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"Printer subscription consumer failed: {message}.");
    }

    private sealed class RecordedReader : IWindowsPrinterInformationReader
    {
        private static readonly DeviceTopologyPrinterInformation Printer = new(
            "Office Queue", "srv", "USB001:,FILE:",
            WindowsPrinterQueueStatusFlags.Printing | WindowsPrinterQueueStatusFlags.Offline,
            12_345);

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
