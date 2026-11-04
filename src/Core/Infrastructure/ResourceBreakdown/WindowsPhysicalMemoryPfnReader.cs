using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal sealed class WindowsPhysicalMemoryPfnReader(Action<string>? logState = null,
    Func<ulong, CancellationToken, PhysicalMemoryResidualFrame>? read = null)
{
    internal const int BatchPages = 65536, MaximumRanges = 4095;
    internal const ulong MaximumPages = 16777216;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    private readonly object stateGate = new();
    private string? previousState;

    internal PhysicalMemoryResidualFrame Capture(ulong attributedPrivateBytes, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var diagnostics = new SortedSet<string>(StringComparer.Ordinal);
        PhysicalMemoryResidualFrame frame;
        try
        {
            frame = read is null ? Walk(attributedPrivateBytes, cancellationToken, state => diagnostics.Add(state))
                : read(attributedPrivateBytes, cancellationToken);
            if (frame.ReasonCode is { } reason) diagnostics.Add(reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is PfnQueryException or Win32Exception or OverflowException)
        {
            var reason = error is PfnQueryException query ? query.Code
                : error is Win32Exception native ? $"native-error:{native.NativeErrorCode}" : "arithmetic-overflow";
            diagnostics.Add(reason);
            frame = PhysicalMemoryResidualFrame.Unavailable(at, reason);
        }
        var state = diagnostics.Count == 0 ? "ready" : string.Join('|', diagnostics);
        lock (stateGate)
        {
            if (previousState != state) { previousState = state; logState?.Invoke(state); }
        }
        return frame;
    }

    private static unsafe PhysicalMemoryResidualFrame Walk(ulong attributed, CancellationToken token,
        Action<string> diagnostic)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Environment.SystemPageSize != 4096)
            throw new PfnQueryException("unsupported-platform");
        var timer = Stopwatch.StartNew();
        const int rangeBytes = 65536;
        PhysicalRangeHeader* ranges = null;
        PfnRequestHeader* header = null;
        try
        {
            ranges = (PhysicalRangeHeader*)NativeMemory.AllocZeroed(rangeBytes);
            header = (PfnRequestHeader*)NativeMemory.AllocZeroed((nuint)(sizeof(PfnRequestHeader) + BatchPages * sizeof(PfnIdentityLayout)));
            ranges->Version = 2;
            Query(17, ranges, rangeBytes);
            if (ranges->Version != 2 || ranges->RangeCount is 0 or > MaximumRanges)
                throw new PfnQueryException("invalid-physical-ranges");
            var entries = new ReadOnlySpan<PhysicalRangeLayout>(ranges + 1, (int)ranges->RangeCount);
            var totalPages = ValidateRanges(entries);
            var aggregation = new PhysicalMemoryPfnAggregation();
            ulong available = 0;
            DateTimeOffset capturedAt = default;
            foreach (ref readonly var range in entries)
            {
                for (ulong offset = 0; offset < range.PageCount;)
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.Elapsed > MaximumDuration) throw new PfnQueryException("capture-time-limit");
                    var take = (int)Math.Min((ulong)BatchPages, range.PageCount - offset);
                    var size = sizeof(PfnRequestHeader) + take * sizeof(PfnIdentityLayout);
                    new Span<byte>(header, size).Clear();
                    header->Version = 1;
                    header->Flags = capturedAt == default ? 1U : 0;
                    header->PfnCount = (ulong)take;
                    var pages = new Span<PfnIdentityLayout>(header + 1, take);
                    for (var index = 0; index < take; index++)
                        pages[index] = new(0, checked(range.BasePfn + offset + (ulong)index));
                    var queryAt = DateTimeOffset.UtcNow;
                    Query(6, header, (uint)size);
                    if (header->Version != 1 || header->PfnCount != (ulong)take)
                        throw new PfnQueryException("invalid-pfn-response");
                    if (capturedAt == default)
                    {
                        available = header->Memory.AvailablePages();
                        capturedAt = queryAt;
                    }
                    aggregation.Add(pages);
                    offset += (ulong)take;
                }
            }
            if (timer.Elapsed > MaximumDuration) throw new PfnQueryException("capture-time-limit");
            if (available > totalPages) throw new PfnQueryException("available-exceeds-total");
            var tags = WindowsPhysicalMemoryResidualReader.ReadTags(token);
            if (tags.State != "ready") diagnostic(tags.State);
            return aggregation.CreateFrame(capturedAt, checked((totalPages - available) * 4096), attributed,
                new(null, tags.Tags, tags.OtherBytes, tags.State), diagnostic);
        }
        finally { NativeMemory.Free(header); NativeMemory.Free(ranges); }
    }

    internal static ulong ValidateRanges(ReadOnlySpan<PhysicalRangeLayout> ranges)
    {
        if (ranges.Length is 0 or > MaximumRanges) throw new PfnQueryException("range-count-limit");
        ulong count = 0, end = 0;
        foreach (ref readonly var range in ranges)
        {
            if (range.PageCount == 0 || range.BasePfn < end || ulong.MaxValue - range.BasePfn < range.PageCount)
                throw new PfnQueryException("invalid-physical-ranges");
            end = range.BasePfn + range.PageCount;
            count = checked(count + range.PageCount);
            if (count > MaximumPages) throw new PfnQueryException("page-count-limit");
        }
        return count;
    }

    private static unsafe void Query(uint informationClass, void* data, uint length)
    {
        var envelope = new SuperfetchInformationLayout
        { Version = SuperfetchInformationLayout.CurrentVersion, Magic = SuperfetchInformationLayout.Signature,
            InformationClass = informationClass, Data = data, Length = length };
        if (!envelope.IsValid || sizeof(PfnRequestHeader) != 192 || sizeof(PfnIdentityLayout) != 24)
            throw new PfnQueryException("invalid-superfetch-layout");
        using var privilege = ProfileSingleProcessPrivilege.Enter();
        var status = NtQuerySystemInformation(79, &envelope, (uint)sizeof(SuperfetchInformationLayout), out _);
        if (status < 0) throw new PfnQueryException($"superfetch-{informationClass}:0x{(uint)status:X8}");
        if (!envelope.IsValid) throw new PfnQueryException("invalid-superfetch-response");
    }
    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(int informationClass, void* data, uint length, out uint returned);
}
