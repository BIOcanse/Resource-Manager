using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Files.Layout;

internal static class WindowsFileLayoutOwnerConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = "windows-file-layout|file|0000000000000012|00112233445566778899AABBCCDDEEFF";
        var reader = new RecordedFileReader(new(WindowsFileLayoutTargetKind.File, identity, 0, null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\empty.bin"), reader);

        var catalog = await query.Backend.DiscoverAsync(timeout.Token);
        var capability = catalog.Fields.Single();
        var reading = query.Backend.ReadCached(capability.Field);
        if (capability.Field.FieldName != "file_all_extent_count_actual"
            || capability.ReadSource != "windows.fsctl.file-layout"
            || reading.Status != ResourceManager.ReadWrite.FieldObservationStatus.Current
            || reading.Value?.Integer != MetricInteger.Parse("0") || reader.Reads != 1)
            throw new InvalidDataException("The ReadWriteCore package consumer did not observe the recorded zero-extent result.");
        Console.WriteLine("ReadWriteCore package consumer: one explicit recorded file-extent capture; zero is current; no live filesystem call.");
    }

    private sealed class RecordedFileReader(WindowsFileLayoutReadResult result) : IWindowsFileLayoutInformationReader
    {
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        public WindowsFileLayoutReadResult Read(WindowsFileLayoutQueryScope scope,
            WindowsFileLayoutInformationFields selected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.TargetKind != WindowsFileLayoutTargetKind.File
                || selected != WindowsFileLayoutInformationFields.FileExtentCount)
                throw new InvalidOperationException("The recorded producer was invoked outside the requested file field.");
            Interlocked.Increment(ref reads);
            return result;
        }
    }
}
