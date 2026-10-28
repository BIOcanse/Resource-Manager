using System.Globalization;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.KernelLeak;

public sealed class KernelLeakProcessSampleSource(IResourceBreakdownPushSource processes) : IKernelLeakSampleSource
{
    public const string HandleField = "process_all_handle_count_actual";
    public async IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Identity and handles are from the original process capture, no second inventory or ETW subscription.
        var request = ResourceBreakdownSampleRequest.ForResourceTable([HandleField], new Dictionary<string, string>());
        DateTimeOffset? last = null;
        await foreach (var snapshot in processes.SubscribeAsync(holder, request, interval, cancellationToken))
        {
            // Native source warm-up is not a failed observation or a sample. Keep the current progress until data arrives.
            if (snapshot.Sampling.Status == ResourceBreakdownSamplingStatuses.Warming) continue;
            var fields = snapshot.ProcessFields;
            if (fields?.CapturedAt is not { } captured || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready)
            { last = null; yield return new(snapshot.CapturedAt, false, []); continue; }
            if (last is { } previous && captured <= previous) continue; // Current-value replay is not a new observation.
            last = captured;
            if (fields.Processes.Count > 16384) throw new OverflowException("Kernel leak input capacity exceeded.");
            var samples = new List<KernelLeakSample>(fields.Processes.Count);
            var unavailable = 0;
            foreach (var process in fields.Processes)
            {
                var value = process.Values.FirstOrDefault(value => value.Field == HandleField)?.IntegerValue;
                if (process.ProcessId <= 0) continue;
                if (!ulong.TryParse(process.CreationWindowsFileTime100Ns, NumberStyles.None, CultureInfo.InvariantCulture, out var birth)
                    || birth == 0 || value is not { Negative: false, HighBits: 0 } count)
                { unavailable++; continue; }
                samples.Add(new(process.ProcessId, birth.ToString(CultureInfo.InvariantCulture), KernelLeakSampleKind.ProcessHandles, count.LowBits));
            }
            // Missing counts break only that holder's series. Missing identity cannot create a false holder.
            yield return new(captured, samples.Count > 0 || unavailable == 0, samples) { UnavailableHolderCount = unavailable };
        }
    }
}
