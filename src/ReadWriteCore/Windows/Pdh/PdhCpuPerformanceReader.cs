

namespace ResourceManager.ReadWrite.Windows.Pdh;

public sealed class PdhCpuPerformanceReader : IDisposable
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

            if (PdhCounterMethods.PdhCollectQueryData(query) != PdhCounterMethods.ErrorSuccess)
            {
                return null;
            }

            var result = PdhCounterMethods.PdhGetFormattedCounterValue(
                counter,
                PdhCounterMethods.PdhFmtDouble,
                out _,
                out var value);

            if (result != PdhCounterMethods.ErrorSuccess || value.CStatus != PdhCounterMethods.ErrorSuccess)
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
        if (PdhCounterMethods.PdhOpenQuery(null, UIntPtr.Zero, out query) != PdhCounterMethods.ErrorSuccess)
        {
            query = IntPtr.Zero;
            return false;
        }

        if (PdhCounterMethods.PdhAddEnglishCounter(query, ProcessorPerformanceCounter, UIntPtr.Zero, out counter) != PdhCounterMethods.ErrorSuccess)
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
            PdhCounterMethods.PdhCloseQuery(query);
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
