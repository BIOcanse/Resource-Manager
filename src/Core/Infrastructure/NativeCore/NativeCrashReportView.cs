using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Call-scoped CLR source bridge, retired by R-crash. The domain view and every
// consumer share these original fields. No HTTP writer or copied report graph.
internal static partial class NativeCrashReportView
{
    internal enum ScalarField : uint
    {
        ReportCount, AttachmentLimit, DumpPresent, DumpSize, FaultPresent, PackagePresent,
        PackageSize, PackageExceeds, Acknowledged, Reported, ResultPackageSize, ResultPackageExceeds
    }
    internal enum TextField : uint
    {
        DumpFolder = 0, Id = 1, Program = 3, Role = 4, Version = 5, DumpStatus = 6,
        DumpPath = 7, FaultModule = 8, FaultOffset = 9, FaultExceptionCode = 10,
        FaultManagedStack = 11, FaultDescription = 12, PackagePath = 13,
        ResultPackagePath = 15, Reason = 17, Url = 18
    }
    internal enum DateField : uint { OccurredAt, PackageCreatedAt, ResultPackageCreatedAt }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Source { internal nint Context; internal nint Accessors; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BorrowedResult
    {
        internal NativeCrashReportCapabilities.Operation Operation;
        internal NativeCrashReportCapabilities.Outcome Outcome;
        internal Source Source;
    }
    internal interface ISink
    {
        void Scalar(nuint index, ScalarField field, long value);
        void Text(nuint index, TextField field, ReadOnlySpan<char> value, bool isNull);
        void Date(nuint index, DateField field, DateTimeOffset value);
    }

    // This synchronous callback must finish before returning: its Source holds
    // call-local GCHandle/accessor addresses and is never an asynchronous value.
    internal static unsafe int Borrow(NativeCrashReportCapabilities.Result result, Func<BorrowedResult, int> consume)
    {
        var borrowed = new BorrowedSource(result.Value);
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var accessors = new Accessors { Scalar = &ReadScalar, Text = &ReadText, Date = &ReadDate };
            var input = new BorrowedResult { Operation = result.Operation, Outcome = result.Outcome,
                Source = new Source { Context = GCHandle.ToIntPtr(handle), Accessors = (nint)(&accessors) } };
            int code;
            try { code = consume(input); }
            catch { borrowed.Failure?.Throw(); throw; }
            borrowed.Failure?.Throw();
            return code;
        }
        finally { handle.Free(); GC.KeepAlive(result.Value); }
    }

    internal static unsafe void Consume(NativeCrashReportCapabilities.Result result, ISink sink)
    {
        var target = new BorrowedSink(sink);
        var handle = GCHandle.Alloc(target);
        try
        {
            var callbacks = new Sink { Scalar = &AcceptScalar, Text = &AcceptText, Date = &AcceptDate };
            var context = GCHandle.ToIntPtr(handle);
            // Pass only the callback-table address through the managed closure;
            // its actual storage and consumer both remain in this sync scope.
            var pointer = (nint)(&callbacks);
            var code = Borrow(result, input => NativeMethods.Visit(in input, context, pointer));
            target.Failure?.Throw();
            if (code != 0) throw new InvalidOperationException($"Native crash view failed ({code}).");
        }
        finally { handle.Free(); GC.KeepAlive(sink); }
    }

    private sealed class BorrowedSource(object? snapshot)
    {
        internal ExceptionDispatchInfo? Failure;
        private CrashReport Report(nuint index) => snapshot switch
        {
            CrashReportList list => list.Reports[checked((int)index)],
            CrashReportPackageResult result => result.Report,
            CrashReportIssueLink result => result.Report,
            CrashReportAcknowledgement result => result.Report,
            _ => throw new InvalidOperationException("No crash-report source is borrowed.")
        };
        internal long Scalar(nuint index, ScalarField field) => field switch
        {
            ScalarField.ReportCount => ((CrashReportList)snapshot!).Reports.Count,
            ScalarField.AttachmentLimit => ((CrashReportList)snapshot!).IssueAttachmentLimitBytes,
            ScalarField.DumpPresent => Report(index).Dump is null ? 0 : 1,
            ScalarField.DumpSize => Report(index).Dump!.SizeBytes,
            ScalarField.FaultPresent => Report(index).Fault is null ? 0 : 1,
            ScalarField.PackagePresent => Report(index).Package is null ? 0 : 1,
            ScalarField.PackageSize => Report(index).Package!.SizeBytes,
            ScalarField.PackageExceeds => Report(index).Package!.ExceedsIssueLimit ? 1 : 0,
            ScalarField.Acknowledged => Report(index).Acknowledged ? 1 : 0,
            ScalarField.Reported => Report(index).Reported ? 1 : 0,
            ScalarField.ResultPackageSize => ((CrashReportPackageResult)snapshot!).Package.SizeBytes,
            ScalarField.ResultPackageExceeds => ((CrashReportPackageResult)snapshot!).Package.ExceedsIssueLimit ? 1 : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        internal string? Text(nuint index, TextField field) => field switch
        {
            TextField.DumpFolder => ((CrashReportList)snapshot!).DumpFolder,
            TextField.Id => Report(index).Id,
            TextField.Program => Report(index).Program,
            TextField.Role => Report(index).Role,
            TextField.Version => Report(index).Version,
            TextField.DumpStatus => Report(index).DumpStatus,
            TextField.DumpPath => Report(index).Dump!.Path,
            TextField.FaultModule => Report(index).Fault!.Module,
            TextField.FaultOffset => Report(index).Fault!.Offset,
            TextField.FaultExceptionCode => Report(index).Fault!.ExceptionCode,
            TextField.FaultManagedStack => Report(index).Fault!.ManagedStack,
            TextField.FaultDescription => Report(index).Fault!.Description,
            TextField.PackagePath => Report(index).Package!.Path,
            TextField.ResultPackagePath => ((CrashReportPackageResult)snapshot!).Package.Path,
            TextField.Reason => ((CrashReportPackageResult)snapshot!).Reason,
            TextField.Url => ((CrashReportIssueLink)snapshot!).Url,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        internal DateTimeOffset Date(nuint index, DateField field) => field switch
        {
            DateField.OccurredAt => Report(index).OccurredAt,
            DateField.PackageCreatedAt => Report(index).Package!.CreatedAt,
            DateField.ResultPackageCreatedAt => ((CrashReportPackageResult)snapshot!).Package.CreatedAt,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }
    private sealed class BorrowedSink(ISink sink)
    {
        internal ISink Sink => sink;
        internal ExceptionDispatchInfo? Failure;
    }
    private static BorrowedSource SourceAt(nint context) => (BorrowedSource)GCHandle.FromIntPtr(context).Target!;
    private static BorrowedSink SinkAt(nint context) => (BorrowedSink)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadScalar(nint context, nuint index, uint field, long* value)
    {
        var source = SourceAt(context);
        try { *value = source.Scalar(index, (ScalarField)field); return 0; }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    // Fixed for this exact immutable result borrow, not across owner updates.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static unsafe int ReadText(nint context, nuint index, uint field, nint receiverContext,
        delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int> receive)
    {
        var source = SourceAt(context);
        try
        {
            var value = source.Text(index, (TextField)field);
            fixed (char* pointer = value)
                return receive(receiverContext, (ushort*)pointer, checked((nuint)(value?.Length ?? 0)));
        }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadDate(nint context, nuint index, uint field, DateParts* value)
    {
        var source = SourceAt(context);
        try { *value = DateParts.From(source.Date(index, (DateField)field)); return 0; }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AcceptScalar(nint context, nuint index, uint field, long value)
    {
        var target = SinkAt(context);
        try { target.Sink.Scalar(index, (ScalarField)field, value); return 0; }
        catch (Exception error) { target.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int AcceptText(nint context, nuint index, uint field, ushort* pointer, nuint length)
    {
        var target = SinkAt(context);
        try { target.Sink.Text(index, (TextField)field, new ReadOnlySpan<char>((char*)pointer, checked((int)length)), pointer == null); return 0; }
        catch (Exception error) { target.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int AcceptDate(nint context, nuint index, uint field, DateParts* value)
    {
        var target = SinkAt(context);
        try { target.Sink.Date(index, (DateField)field, value->ToDate()); return 0; }
        catch (Exception error) { target.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Accessors
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, long*, int> Scalar;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, nint, delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int>, int> Text;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, DateParts*, int> Date;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Sink
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, long, int> Scalar;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, ushort*, nuint, int> Text;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, DateParts*, int> Date;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DateParts
    {
        internal int Year, Month, Day, Hour, Minute, Second, Fraction, OffsetMinutes;
        internal static DateParts From(DateTimeOffset value) => new()
        {
            Year = value.Year, Month = value.Month, Day = value.Day, Hour = value.Hour, Minute = value.Minute,
            Second = value.Second, Fraction = checked((int)(value.Ticks % TimeSpan.TicksPerSecond)),
            OffsetMinutes = checked((int)(value.Offset.Ticks / TimeSpan.TicksPerMinute))
        };
        internal readonly DateTimeOffset ToDate() => new DateTimeOffset(Year, Month, Day, Hour, Minute, Second,
            TimeSpan.FromMinutes(OffsetMinutes)).AddTicks(Fraction);
    }
    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_capability_visit")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Visit(in BorrowedResult result, nint context, nint sink);
    }
}
