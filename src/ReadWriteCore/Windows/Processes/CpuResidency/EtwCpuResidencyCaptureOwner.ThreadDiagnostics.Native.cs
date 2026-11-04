using System.Globalization;
using System.Threading.Channels;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwCpuResidencyCaptureOwner
{
    private static long nextNativeDiagnosticRequest;
    private sealed class NativeThreadDiagnosticRequest : IDisposable
    {
        private readonly NativeKernelEtwSession session;
        internal KernelEtwEventSource EventSource { get; }
        private readonly ulong id = checked((ulong)Interlocked.Increment(ref nextNativeDiagnosticRequest));
        private readonly int pid;
        private readonly long generation, birth;
        private readonly TimeSpan sampleBudget;
        private readonly SemaphoreSlim sampleGate = new(1, 1);
        private readonly CancellationTokenSource removed = new();
        private CpuCoreResidencySnapshot? pendingSnapshot, previousSnapshot;
        private CpuProcessThreadWindow? previousWindow;
        private ulong sampleTicket;
        private int disposed;
        internal NativeKernelEtwSession Session => session;
        internal NativeThreadDiagnosticRequest(NativeKernelEtwSession session, long generation, int pid, long birth,
            DateTimeOffset requestedAt, TimeSpan sampleBudget, KernelEtwEventSource eventSource)
        {
            this.session = session; this.generation = generation; this.pid = pid; this.birth = birth;
            this.sampleBudget = sampleBudget;
            EventSource = eventSource;
            KernelEtwNativeReaders.RegisterDiagnostics(session, id, pid, birth, requestedAt);
        }
        internal CpuProcessThreadWindow? ReadWindow(CpuCoreResidencySnapshot snapshot)
            => ReadWindowAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();

        internal async Task<CpuProcessThreadWindow?> ReadWindowAsync(CpuCoreResidencySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!EventSource.IsActive || snapshot.SessionGeneration != generation) return null;
            var source = snapshot.Processes.SingleOrDefault(row => row.ProcessId == pid
                && row.ProcessStartKey == birth.ToString(CultureInfo.InvariantCulture));
            if (source is null) return null;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, removed.Token);
            budget.CancelAfter(sampleBudget);
            var entered = false;
            try
            {
                await sampleGate.WaitAsync(budget.Token).ConfigureAwait(false);
                entered = true;
                if (Volatile.Read(ref disposed) != 0 || !EventSource.IsActive) return null;
                if (SameWindow(previousSnapshot, snapshot)) return previousWindow;
                var notices = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
                { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
                using var observation = EventSource.ObservePublications(() => notices.Writer.TryWrite(0));
                try
                {
                    // Preserve a submitted ticket across cancellation/retry of this same window.
                    if (!SameWindow(pendingSnapshot, snapshot))
                    {
                        sampleTicket = KernelEtwNativeReaders.SampleDiagnostics(session, id, snapshot.MeasuredFrom, snapshot.MeasuredThrough);
                        pendingSnapshot = snapshot;
                        session.RequestRefresh(Environment.TickCount64);
                    }
                    while (true)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (KernelEtwNativeReaders.ReadDiagnosticsAt(session, id, sampleTicket,
                            snapshot.MeasuredFrom, snapshot.MeasuredThrough, Copy, out DiagnosticFacts? facts, out var acknowledged))
                        {
                            if (Volatile.Read(ref disposed) != 0 || !EventSource.IsActive) return null;
                            previousWindow = Join(snapshot, source, facts!);
                            previousSnapshot = snapshot;
                            return previousWindow;
                        }
                        if (acknowledged)
                        {
                            // A removed request or a different sampled window is explicit unavailable.
                            previousSnapshot = snapshot;
                            previousWindow = null;
                            return null;
                        }
                        if (session.ProcessingCompletion.IsCompleted)
                        {
                            if (session.ProcessingFailure is { } failure)
                                throw new InvalidOperationException("The native diagnostic flow failed before sampling.", failure);
                            return null;
                        }
                        var signal = notices.Reader.ReadAsync(budget.Token).AsTask();
                        var ready = await Task.WhenAny(signal, session.ProcessingCompletion).WaitAsync(budget.Token).ConfigureAwait(false);
                        if (ready == signal) await signal.ConfigureAwait(false);
                    }
                }
                finally { notices.Writer.TryComplete(); }
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null; // Configured observation budget expired, or this request was removed.
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { return null; }
            finally { if (entered) sampleGate.Release(); }
        }
        private static bool SameWindow(CpuCoreResidencySnapshot? left, CpuCoreResidencySnapshot right)
            => left is not null && left.SessionGeneration == right.SessionGeneration
                && left.MeasuredFrom == right.MeasuredFrom && left.MeasuredThrough == right.MeasuredThrough;

        private CpuProcessThreadWindow Join(CpuCoreResidencySnapshot snapshot, CpuProcessCoreResidency source, DiagnosticFacts known)
        {
            var from = snapshot.MeasuredFrom.UtcDateTime.ToFileTimeUtc();
            var through = snapshot.MeasuredThrough.UtcDateTime.ToFileTimeUtc();
            var threads = source.Threads.Select(row =>
            {
                var separator = row.ThreadInstanceId.LastIndexOf(':');
                if (separator < 0 || !long.TryParse(row.ThreadInstanceId.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var instance))
                    throw new InvalidDataException("The native residency thread lifetime identity is malformed.");
                if (known.Threads.TryGetValue(instance, out var value))
                {
                    if (value.ExitFileTime != 0 && value.ExitFileTime <= from) return row;
                    var fact = value.Facts;
                    if (!fact.CreationFileTime100Ns.HasValue || fact.CreationFileTime100Ns <= through)
                        return row with { Diagnostic = fact };
                }
                return known.ProcessError.HasValue
                    ? row with { Diagnostic = new(null, null, null, null, null, null, null, known.ProcessError) } : row;
            }).ToArray();
            return new(snapshot.MeasuredFrom, snapshot.MeasuredThrough, snapshot.SessionGeneration, source with { Threads = threads });
        }
        private sealed record ThreadFacts(CpuThreadDiagnosticFacts Facts, long ExitFileTime);
        private sealed record DiagnosticFacts(Dictionary<long, ThreadFacts> Threads, int? ProcessError);
        private unsafe DiagnosticFacts Copy(KernelEtwNativeReaders.DiagnosticRequestView view)
        {
            if (view.RequestId != id || view.ProcessId != pid || view.ProcessBirth != birth)
                throw new InvalidDataException("The native diagnostic publication belongs to another request or process lifetime.");
            if (view.ThreadCount != 0 && view.Threads == 0) throw new InvalidDataException("Native thread metadata rows are absent.");
            var rows = new Dictionary<long, ThreadFacts>(checked((int)view.ThreadCount));
            for (var i = 0; i < view.ThreadCount; i++)
            {
                var row = ((KernelEtwNativeReaders.DiagnosticThreadView*)view.Threads)[i];
                if (row.ThreadInstance == 0) continue;
                rows.Add(row.ThreadInstance, new(KernelEtwNativeReaders.CopyDiagnosticFacts(row), row.ExitFileTime));
            }
            return new(rows, (view.Flags & 1) != 0 ? view.NativeError : null);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            removed.Cancel();
            // These same retained handles belong to the live consumer until its explicit removal command.
            try { KernelEtwNativeReaders.UnregisterDiagnostics(session, id); }
            catch (ObjectDisposedException) { }
            removed.Dispose();
        }
    }
}
