using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

namespace ResourceManager.App.Endpoints.Transport;

// R-recording removes this synchronous original-report bridge. H1 removes the
// ASP.NET download shell; H3 removes the final managed native imports.
internal static partial class NativeTargetedRecordingCsv
{
    internal static IResult Download(TargetedRecordingReport report, string fileName)
        => new DownloadResult(report, fileName);

    // Preserves the existing synchronous byte[] caller without an output copy.
    // A counting pass reads the same immutable source; only the final bytes are
    // allocated. The HTTP path above always writes final response segments.
    internal static byte[] Write(TargetedRecordingReport report)
    {
        var length = Encode(null, report, CancellationToken.None);
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var output = new ExactOutput(bytes);
        if (Encode(output, report, CancellationToken.None) != length || output.Written != bytes.Length)
            throw new InvalidOperationException("Recording CSV source changed while borrowed.");
        return bytes;
    }

    private sealed class ExactOutput(byte[] bytes) : PipeWriter
    {
        internal int Written;
        public override void Advance(int count)
        {
            if (count < 0 || count > bytes.Length - Written) throw new ArgumentOutOfRangeException(nameof(count));
            Written += count;
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => bytes.AsMemory(Written);
        public override Span<byte> GetSpan(int sizeHint = 0) => bytes.AsSpan(Written);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
    }

    private sealed class DownloadResult(TargetedRecordingReport report, string fileName) : IResult
    {
        private TargetedRecordingReport? source = report;
        public Task ExecuteAsync(HttpContext context)
            // ASP.NET retains its original attachment/filename header rules.
            // Transfer this single response's source into synchronous encoding;
            // neither the result nor its asynchronous callback retains it.
            => Results.Stream(_ => Send(context, Interlocked.Exchange(ref source, null)
                    ?? throw new InvalidOperationException("Recording CSV response was already consumed.")),
                    "text/csv; charset=utf-8", fileName)
                .ExecuteAsync(context);
    }

    private static Task Send(HttpContext context, TargetedRecordingReport report)
    {
        context.Response.ContentLength = checked((long)Encode(context.Response.BodyWriter, report, context.RequestAborted));
        return FlushAsync(context.Response.BodyWriter, context.RequestAborted);
    }

    internal static Task WriteAsync(PipeWriter output, TargetedRecordingReport report, CancellationToken cancellationToken = default)
    {
        Encode(output, report, cancellationToken);
        return FlushAsync(output, cancellationToken);
    }

    private static async Task FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        var result = await output.FlushAsync(cancellationToken);
        if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
    }

    private static unsafe ulong Encode(PipeWriter? output, TargetedRecordingReport report, CancellationToken cancellationToken)
    {
        using var source = new BorrowedReport(report, output, cancellationToken);
        var handle = GCHandle.Alloc(source);
        try
        {
            var input = new Callbacks { Count = &ReadCount, Field = &ReadField, Compare = &Compare, Metric = &ReadMetric };
            var sink = new OutputCallbacks { Reserve = &Reserve, Commit = &Commit };
            ulong length;
            var code = output is null
                ? NativeMethods.Measure(GCHandle.ToIntPtr(handle), in input, out length)
                : NativeMethods.Encode(GCHandle.ToIntPtr(handle), in input, in sink, out length);
            source.Failure?.Throw();
            if (code != 0) throw new InvalidOperationException($"Native recording CSV encoding failed ({code}).");
            return length;
        }
        finally
        {
            handle.Free();
            GC.KeepAlive(report);
        }
    }

    private static BorrowedReport Borrow(nint context) => (BorrowedReport)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadCount(nint context, nuint index, uint field, nuint* count)
    {
        var source = Borrow(context);
        try
        {
            *count = checked((nuint)(field switch
            {
                0 => source.Report.Summary is null ? 0 : 1,
                1 => source.Report.FrameIntervals.Count,
                2 => source.Report.ResourceSamples.Count,
                3 => source.Values(index, 0).Count,
                4 => source.Values(index, 1).Count,
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            }));
            return 0;
        }
        catch (Exception error) { return source.Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadField(nint context, nuint index, uint field, nint writer)
    {
        var source = Borrow(context);
        try { return source.WriteField(index, field, writer); }
        catch (Exception error) { return source.Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Compare(nint context, nuint sample, uint scope, nuint left, nuint right, int* order)
    {
        var source = Borrow(context);
        try
        {
            var values = source.Values(sample, scope);
            var a = source.Entry(values, left).Key;
            var b = source.Entry(values, right).Key;
            fixed (char* pa = a)
            fixed (char* pb = b)
                *order = NativeMethods.Compare((ushort*)pa, checked((nuint)a.Length), (ushort*)pb, checked((nuint)b.Length));
            return 0;
        }
        catch (Exception error) { return source.Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadMetric(nint context, nuint sample, uint scope, nuint index, nint writer)
    {
        var source = Borrow(context);
        try
        {
            var pair = source.Entry(source.Values(sample, scope), index);
            fixed (char* key = pair.Key)
                return NativeMethods.Metric(writer, (ushort*)key, checked((nuint)pair.Key.Length),
                    pair.Value.HasValue ? 1U : 0U, pair.Value.GetValueOrDefault());
        }
        catch (Exception error) { return source.Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Reserve(nint context, nuint completed, byte** pointer, nuint* capacity)
    {
        var source = Borrow(context);
        try
        {
            source.Commit(completed);
            source.CancellationToken.ThrowIfCancellationRequested();
            var memory = source.Output.GetMemory();
            source.Pin = memory.Pin();
            source.Pinned = true;
            source.Capacity = memory.Length;
            *pointer = (byte*)source.Pin.Pointer;
            *capacity = checked((nuint)memory.Length);
            return 0;
        }
        catch (Exception error) { return source.Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(nint context, nuint completed)
    {
        var source = Borrow(context);
        try { source.Commit(completed); return 0; }
        catch (Exception error) { return source.Fail(error); }
    }

    private sealed class BorrowedReport(TargetedRecordingReport report, PipeWriter? output, CancellationToken cancellationToken) : IDisposable
    {
        internal TargetedRecordingReport Report => report;
        internal PipeWriter Output => output ?? throw new InvalidOperationException("Counting output has no segments.");
        internal CancellationToken CancellationToken => cancellationToken;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal int Capacity;
        internal ExceptionDispatchInfo? Failure;
        private IReadOnlyDictionary<string, double?>? entries;
        private IEnumerator<KeyValuePair<string, double?>>? cursor;
        private nuint position;
        // Three call-local reference descriptors cover the native comparison's
        // candidate, predecessor and minimum. No key/value collection is copied.
        // The moving original enumerator avoids rescanning for each comparison.
        private EntryReference? recent, older, oldest;
        private readonly record struct EntryReference(nuint Index, KeyValuePair<string, double?> Value);

        internal KeyValuePair<string, double?> Entry(IReadOnlyDictionary<string, double?> values, nuint index)
        {
            if (!ReferenceEquals(entries, values))
            {
                cursor?.Dispose();
                cursor = null;
                entries = values;
                recent = older = oldest = null;
            }
            if (recent is { } first && first.Index == index) return first.Value;
            EntryReference found;
            if (older is { } second && second.Index == index)
            {
                found = second;
                older = recent;
            }
            else if (oldest is { } third && third.Index == index)
            {
                found = third;
                oldest = older;
                older = recent;
            }
            else
            {
                if (cursor is null || position > index)
                {
                    cursor?.Dispose();
                    cursor = values.GetEnumerator();
                    position = 0;
                }
                while (cursor.MoveNext())
                {
                    var current = position++;
                    if (current != index) continue;
                    found = new(index, cursor.Current);
                    oldest = older;
                    older = recent;
                    recent = found;
                    return found.Value;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            recent = found;
            return found.Value;
        }

        internal int Fail(Exception error) { Failure ??= ExceptionDispatchInfo.Capture(error); return 2; }
        internal IReadOnlyDictionary<string, double?> Values(nuint index, uint scope) => scope switch
        {
            0 => report.ResourceSamples[checked((int)index)].Software,
            1 => report.ResourceSamples[checked((int)index)].System,
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

        internal void Commit(nuint completed)
        {
            if (completed > checked((nuint)Capacity)) throw new InvalidOperationException("CSV output exceeds its borrowed segment.");
            if (Pinned)
            {
                Pin.Dispose();
                Pinned = false;
                Capacity = 0;
                Output.Advance(checked((int)completed));
            }
            else if (completed != 0) throw new InvalidOperationException("No CSV segment is borrowed.");
        }

        internal int WriteField(nuint index, uint field, nint writer)
        {
            var header = report.Recording;
            return field switch
            {
                0 => RequiredText(writer, header.Id),
                1 => RequiredText(writer, header.SoftwareId),
                2 => RequiredText(writer, header.SoftwareName),
                3 => Date(writer, header.StartedAt),
                4 => header.EndedAt is { } ended ? Date(writer, ended) : 0,
                5 => Integer(writer, header.MaximumDurationSeconds, CultureInfo.InvariantCulture.NumberFormat.NegativeSign),
                6 => RequiredText(writer, header.Status),
                7 => Text(writer, header.StopReason),
                8 => NativeMethods.Boolean(writer, header.Incomplete ? 1U : 0U),
                9 => NativeMethods.Number(writer, report.Summary!.AverageFps),
                10 => NativeMethods.Number(writer, report.Summary!.OnePercentLowFps),
                11 => NativeMethods.Number(writer, report.Summary!.PointOnePercentLowFps),
                12 => NativeMethods.Number(writer, report.Summary!.P50FrameTimeMs),
                13 => NativeMethods.Number(writer, report.Summary!.P95FrameTimeMs),
                14 => NativeMethods.Number(writer, report.Summary!.P99FrameTimeMs),
                15 => NativeMethods.Number(writer, report.Summary!.MaxFrameTimeMs),
                16 => Date(writer, report.FrameIntervals[checked((int)index)].EndedAt),
                17 => Integer(writer, report.FrameIntervals[checked((int)index)].ProcessId, CultureInfo.CurrentCulture.NumberFormat.NegativeSign),
                18 => Integer(writer, report.FrameIntervals[checked((int)index)].ProcessStartKey, CultureInfo.CurrentCulture.NumberFormat.NegativeSign),
                19 => FrameSource(writer, (int)report.FrameIntervals[checked((int)index)].Source),
                20 => NativeMethods.Unsigned(writer, report.FrameIntervals[checked((int)index)].SwapChain),
                21 => NativeMethods.Number(writer, report.FrameIntervals[checked((int)index)].DurationMs),
                22 => Date(writer, report.ResourceSamples[checked((int)index)].CapturedAt),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }

        private static unsafe int Text(nint writer, string? value)
        {
            fixed (char* pointer = value)
                return NativeMethods.Text(writer, (ushort*)pointer, checked((nuint)(value?.Length ?? 0)));
        }
        // Old Escape dereferences required metadata strings. Do not turn an
        // invalid null in those fields into the nullable stopReason's empty cell.
        private static int RequiredText(nint writer, string value)
            => Text(writer, value ?? throw new NullReferenceException());
        private static unsafe int Integer(nint writer, long value, string negativeSign)
        {
            fixed (char* pointer = negativeSign)
                return NativeMethods.Integer(writer, value, (ushort*)pointer, checked((nuint)negativeSign.Length));
        }
        private static unsafe int FrameSource(nint writer, int value)
        {
            var sign = CultureInfo.CurrentCulture.NumberFormat.NegativeSign;
            fixed (char* pointer = sign)
                return NativeMethods.Source(writer, value, (ushort*)pointer, checked((nuint)sign.Length));
        }
        private static int Date(nint writer, DateTimeOffset date)
        {
            var parts = new DateParts
            {
                Year = date.Year, Month = date.Month, Day = date.Day,
                Hour = date.Hour, Minute = date.Minute, Second = date.Second,
                Fraction = checked((int)(date.Ticks % TimeSpan.TicksPerSecond)),
                OffsetMinutes = checked((int)(date.Offset.Ticks / TimeSpan.TicksPerMinute))
            };
            return NativeMethods.DateTime(writer, in parts);
        }
        public void Dispose()
        {
            cursor?.Dispose();
            if (Pinned) { Pin.Dispose(); Pinned = false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, nuint*, int> Count;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, nint, int> Field;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, nuint, nuint, int*, int> Compare;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, nuint, nint, int> Metric;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputCallbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, byte**, nuint*, int> Reserve;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Commit;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DateParts { internal int Year, Month, Day, Hour, Minute, Second, Fraction, OffsetMinutes; }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_measure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Measure(nint context, in Callbacks input, out ulong length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_encode")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Encode(nint context, in Callbacks input, in OutputCallbacks output, out ulong length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_text")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Text(nint writer, ushort* text, nuint length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_integer")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Integer(nint writer, long value, ushort* sign, nuint length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_unsigned")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Unsigned(nint writer, ulong value);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_boolean")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Boolean(nint writer, uint value);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_number")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Number(nint writer, double value);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_datetime")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int DateTime(nint writer, in DateParts date);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_source")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Source(nint writer, int value, ushort* sign, nuint length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_compare")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Compare(ushort* left, nuint leftLength, ushort* right, nuint rightLength);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_targeted_recording_csv_metric")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Metric(nint writer, ushort* key, nuint length, uint present, double value);
    }
}
