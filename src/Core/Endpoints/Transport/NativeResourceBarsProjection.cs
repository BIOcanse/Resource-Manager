using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints.Transport;

// Synchronous R-resource source bridge. The original snapshot and each result's
// original identity are retained, not translated into a second object graph.
// H1/H2 retain their existing HTTP/wire plumbing. No product fallback lives here.
internal static partial class NativeResourceBarsProjection
{
    internal static ResourceBreakdownBar[] Select(ResourceBreakdownSnapshot source, IReadOnlyList<string> ids)
    {
        var borrowed = new BorrowedSnapshot(source, ids, null, null, null);
        Invoke(borrowed, Operation.Select);
        return borrowed.Results.ToArray();
    }

    internal static ResourceBreakdownBar WithoutReading(ResourceBreakdownBar bar, string reason)
    {
        var borrowed = new BorrowedSnapshot(null, [], bar, reason, null);
        Invoke(borrowed, Operation.WithoutReading);
        return borrowed.Results[0];
    }

    // The default remains declared at the original interaction calling edge.
    internal static string UnavailableReason(ResourceBreakdownSnapshot source, string id,
        string? observedReason, string fallbackReason)
    {
        var borrowed = new BorrowedSnapshot(source, [id], null, observedReason, fallbackReason);
        Invoke(borrowed, Operation.UnavailableReason);
        return borrowed.Reason!;
    }

    private static unsafe void Invoke(BorrowedSnapshot borrowed, Operation operation)
    {
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var callbacks = new Callbacks { Scalar = &ReadScalar, Compare = &Compare, Emit = &Emit };
            var request = new Request
            {
                RequestCount = checked((nuint)borrowed.Ids.Count),
                BarCount = checked((nuint)borrowed.BarCount),
                DatasetCount = checked((nuint)(borrowed.Source?.Datasets.Count ?? 0)),
                Operation = operation, LayoutVersion = 1
            };
            var code = NativeMethods.Project(GCHandle.ToIntPtr(handle), in callbacks, in request);
            borrowed.Failure?.Throw();
            if (code is 3 or 4) throw new ArgumentException("An item with the same key has already been added.");
            if (code == 5) throw new ArgumentNullException("key");
            if (code != 0) throw new InvalidOperationException($"Native resource-bar projection failed ({code}).");
        }
        finally
        {
            // No callback, source reference, pointer or lease survives Invoke.
            handle.Free();
            GC.KeepAlive(borrowed);
        }
    }

    private static BorrowedSnapshot Borrow(nint context) => (BorrowedSnapshot)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadScalar(nint context, nuint index, Scalar field, uint* value)
    {
        var borrowed = Borrow(context);
        try { *value = borrowed.ReadScalar(checked((int)index), field) ? 1U : 0U; return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Compare(nint context, Comparison operation, Text left, Text right, uint* value)
    {
        var borrowed = Borrow(context);
        try
        {
            var a = borrowed.ReadText(left);
            var b = operation is Comparison.Blank or Comparison.IsNull ? null : borrowed.ReadText(right);
            *value = (operation switch
            {
                Comparison.EqualIgnoreCase => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
                Comparison.EqualOrdinal => string.Equals(a, b, StringComparison.Ordinal),
                Comparison.StartsIgnoreCase => a!.StartsWith(b!, StringComparison.OrdinalIgnoreCase),
                Comparison.EndsIgnoreCase => a!.EndsWith(b!, StringComparison.OrdinalIgnoreCase),
                Comparison.Blank => string.IsNullOrWhiteSpace(a),
                Comparison.IsNull => a is null,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            }) ? 1U : 0U;
            return 0;
        }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Emit(nint context, Result* result)
    {
        var borrowed = Borrow(context);
        try { borrowed.Emit(*result); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    // This object lives only for a single synchronous call. It owns the actual
    // output list, never a source DTO array, pin array, sampling cache or lease.
    private sealed class BorrowedSnapshot(ResourceBreakdownSnapshot? source, IReadOnlyList<string> ids,
        ResourceBreakdownBar? singleBar, string? observedReason, string? fallbackReason)
    {
        internal ResourceBreakdownSnapshot? Source => source;
        internal IReadOnlyList<string> Ids => ids;
        internal int BarCount => source?.Bars.Count ?? (singleBar is null ? 0 : 1);
        internal List<ResourceBreakdownBar> Results { get; } = [];
        internal string? Reason;
        internal ExceptionDispatchInfo? Failure;

        private ResourceBreakdownBar Bar(int index) => source is not null ? source.Bars[index]
            : index == 0 ? singleBar! : throw new ArgumentOutOfRangeException(nameof(index));

        // Registry queries supply the original directory declarations. No
        // selection, availability, ordering or reason policy is repeated here.
        internal bool ReadScalar(int index, Scalar field) => field switch
        {
            Scalar.Supported => ResourceBreakdownMetricIds.IsSupported(ids[index]),
            Scalar.ProcessFact => ProcessMemoryFactFields.TotalMetricId(ids[index]) is not null,
            Scalar.Captured => source!.Datasets[index].CapturedAt.HasValue,
            Scalar.TotalPresent => Bar(index).TotalValue.HasValue,
            Scalar.CapacityPresent => Bar(index).CapacityValue.HasValue,
            Scalar.PercentPresent => Bar(index).TotalSystemPercent.HasValue,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        internal string? ReadText(Text text)
        {
            var index = checked((int)text.Index);
            return text.Kind switch
            {
                TextKind.RequestId => ids[index],
                TextKind.BarId => Bar(index).MetricId,
                TextKind.DatasetId => source!.Datasets[index].DatasetId,
                TextKind.DatasetStatus => source!.Datasets[index].Status,
                TextKind.DatasetFailure => source!.Datasets[index].FailureCode,
                TextKind.BarReason => Bar(index).UnavailableReason,
                TextKind.RequestDataset => SamplingDatasetIds.ForProcessMetric(ids[index]),
                TextKind.ObservedReason => observedReason,
                TextKind.FallbackReason => fallbackReason,
                TextKind.RequestScale => ResourceBreakdownScaleModes.Normalize(ids[index], null),
                TextKind.Declaration => index switch
                {
                    0 => ResourceBreakdownSamplingStatuses.Ready,
                    1 => ResourceBreakdownSamplingStatuses.Warming,
                    2 => ResourceBreakdownMetricIds.GpuPrefix,
                    3 => ResourceBarsProjectionDeclarations.VramSuffix,
                    4 => ResourceBarsProjectionDeclarations.DiskPrefix,
                    5 => ResourceBarsProjectionDeclarations.NetworkPrefix,
                    6 => ResourceBarsProjectionDeclarations.UsageSuffix,
                    7 => ResourceBreakdownMetricIds.CpuUsage,
                    8 => ResourceBarsProjectionDeclarations.WarmingReason,
                    9 => ResourceBarsProjectionDeclarations.GpuMemoryReason,
                    10 => ResourceBarsProjectionDeclarations.DefaultReason,
                    11 => MetricUnits.Bytes,
                    12 => ResourceBarsProjectionDeclarations.RateUnit,
                    13 => MetricUnits.Percent,
                    _ => throw new ArgumentOutOfRangeException(nameof(text))
                },
                _ => throw new ArgumentOutOfRangeException(nameof(text))
            };
        }

        internal void Emit(Result result)
        {
            // Mechanical construction of the actual typed result. Decisions
            // (retain/clear/missing/reason, source index, unit, reason) are Zig's.
            switch (result.Action)
            {
                case Action.Retain:
                    Results.Add(Bar(checked((int)result.BarIndex)));
                    break;
                case Action.Clear:
                    Results.Add(Bar(checked((int)result.BarIndex)) with
                    {
                        TotalValue = null, CapacityValue = null, TotalSystemPercent = null,
                        SharedValue = null, ProcessSumValue = null, ResidualValue = null,
                        Software = [], UnavailableReason = ReadText(result.Reason)
                    });
                    break;
                case Action.Missing:
                    Results.Add(new(ids[checked((int)result.RequestIndex)], ReadText(result.Unit)!,
                        ReadText(new Text { Kind = TextKind.RequestScale, Index = result.RequestIndex })!,
                        null, null, null, []) { UnavailableReason = ReadText(result.Reason) });
                    break;
                case Action.Reason:
                    Reason = ReadText(result.Reason);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }
        }
    }

    private enum TextKind : uint { RequestId, BarId, DatasetId, DatasetStatus, DatasetFailure, BarReason,
        RequestDataset, ObservedReason, FallbackReason, Declaration, RequestScale }
    private enum Scalar : uint { Supported, ProcessFact, Captured, TotalPresent, CapacityPresent, PercentPresent }
    private enum Comparison : uint { EqualIgnoreCase, EqualOrdinal, StartsIgnoreCase, EndsIgnoreCase, Blank, IsNull }
    private enum Operation : uint { Select, WithoutReading, UnavailableReason }
    private enum Action : uint { Retain, Clear, Missing, Reason }
    [StructLayout(LayoutKind.Sequential)]
    private struct Text { internal nuint Index; internal TextKind Kind; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { internal nuint RequestIndex, BarIndex; internal Text Reason, Unit; internal Action Action; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Request { internal nuint RequestCount, BarCount, DatasetCount; internal Operation Operation; internal uint LayoutVersion; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, Scalar, uint*, int> Scalar;
        internal delegate* unmanaged[Cdecl]<nint, Comparison, Text, Text, uint*, int> Compare;
        internal delegate* unmanaged[Cdecl]<nint, Result*, int> Emit;
    }
    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_resource_bars_project")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Project(nint context, in Callbacks callbacks, in Request request);
    }
}
