using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class PdhCpuPerformanceReader : IDisposable
{
    private const string ProcessorPerformanceCounter = @"\Processor Information(_Total)\% Processor Performance";

    private readonly object gate = new();
    private IntPtr query;
    private IntPtr counter;
    private bool initialized;
    private bool unavailable;

    public double? ReadPerformancePercent()
    {
        lock (gate)
        {
            if (unavailable)
            {
                return null;
            }

            if (!initialized && !Initialize())
            {
                unavailable = true;
                return null;
            }

            if (NativeMethods.PdhCollectQueryData(query) != NativeMethods.ErrorSuccess)
            {
                return null;
            }

            var result = NativeMethods.PdhGetFormattedCounterValue(
                counter,
                NativeMethods.PdhFmtDouble,
                out _,
                out var value);

            if (result != NativeMethods.ErrorSuccess || value.CStatus != NativeMethods.ErrorSuccess)
            {
                return null;
            }

            return Sanitize(value.DoubleValue);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            CloseQuery();
        }
    }

    private bool Initialize()
    {
        if (NativeMethods.PdhOpenQuery(null, UIntPtr.Zero, out query) != NativeMethods.ErrorSuccess)
        {
            query = IntPtr.Zero;
            return false;
        }

        if (NativeMethods.PdhAddEnglishCounter(query, ProcessorPerformanceCounter, UIntPtr.Zero, out counter) != NativeMethods.ErrorSuccess)
        {
            CloseQuery();
            return false;
        }

        initialized = true;
        return true;
    }

    private void CloseQuery()
    {
        if (query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(query);
        }

        query = IntPtr.Zero;
        counter = IntPtr.Zero;
        initialized = false;
    }

    private static double? Sanitize(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? null
            : value;
    }
}
