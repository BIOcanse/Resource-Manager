using System.Globalization;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public sealed class AmdSmuCpuSensorReader : IDisposable
{
    private readonly TimeSpan failureCacheDuration;
    private readonly TimeProvider clock;
    private readonly Func<IAmdSmuSession> openSession;
    private bool disposed;
    private readonly object gate = new();
    private readonly int physicalCoreCount;
    private IAmdSmuSession? session;
    private string? cachedFailure;
    private DateTimeOffset cachedFailureAt;

    /// <summary>Creates no WMI, driver or table access. The caller supplies topology, failure interval, clock and session selection.</summary>
    public AmdSmuCpuSensorReader(Func<IAmdSmuSession> openSession, int physicalCoreCount,
        TimeSpan failureCacheDuration, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        ArgumentNullException.ThrowIfNull(clock);
        if (physicalCoreCount < 1) throw new ArgumentOutOfRangeException(nameof(physicalCoreCount));
        if (failureCacheDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(failureCacheDuration));
        this.openSession = openSession;
        this.physicalCoreCount = physicalCoreCount;
        this.failureCacheDuration = failureCacheDuration;
        this.clock = clock;
    }

    /// <summary>
    /// 这条路现在到底走到哪一步了：驱动打不开、表读不出、还是版本没映射。
    /// 诊断用 —— "没读数"有好几种完全不同的原因，得分得开。
    /// </summary>
    public AmdSmuSessionDescription DescribeSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var unavailable = EnsureSession();
            if (unavailable is not null)
            {
                return new AmdSmuSessionDescription(false, null, 0, false, unavailable);
            }
            try
            {
                var table = session!.UpdateAndReadPmTable();
                var layout = AmdSmuSensorIndexResolver.Resolve(session.TableVersion);
                // 把前若干项原样带出来。映射一张 PM table 只能看真实数值，
                // 猜索引会把电流标成温度，那比没有读数更糟。
                var sample = table
                    .Take(256)
                    .Select(static value => Math.Round(value, 3))
                    .ToArray();
                return new AmdSmuSessionDescription(
                    true,
                    $"0x{session.TableVersion:X8}",
                    table.Length,
                    layout is not null,
                    layout is null
                        ? "PM table 读得出来，但这个版本还没做索引映射。"
                        : null,
                    sample);
            }
            catch (AmdSmuProviderUnavailableException error)
            {
                ResetSession();
                return new AmdSmuSessionDescription(false, null, 0, false, error.Message);
            }
        }
    }

    public CpuSensorMetrics Read(AmdSmuCpuSensorReadRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!request.IncludesAnyMetric)
        {
            return CreateNotRequested();
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var unavailable = EnsureSession();
            if (unavailable is not null)
            {
                return CreateUnavailable(unavailable);
            }

            try
            {
                var table = session!.UpdateAndReadPmTable();
                if (table.Length == 0)
                {
                    return CreateUnavailable("RyzenSMU PM table 读取为空。");
                }

                var layout = AmdSmuSensorIndexResolver.Resolve(session.TableVersion);
                if (layout is null)
                {
                    return CreateUnavailable(
                        $"RyzenSMU PM table 0x{session.TableVersion:X8} 已读取，但当前版本尚未完成指标索引映射。");
                }

                return MapTable(request, table, layout, session);
            }
            catch (AmdSmuProviderUnavailableException ex)
            {
                ResetSession();
                CacheFailure(ex.Message);
                return CreateUnavailable(ex.Message);
            }
            catch (IOException ex)
            {
                ResetSession();
                CacheFailure(ex.Message);
                return CreateUnavailable(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                ResetSession();
                CacheFailure(ex.Message);
                return CreateUnavailable(ex.Message);
            }
        }
    }

    /// <summary>Releases this reader's native session without retiring the reader or its failure cache.</summary>
    public void ReleaseSession()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ResetSession();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ResetSession();
        }
    }

    private CpuSensorMetrics MapTable(
        AmdSmuCpuSensorReadRequest request,
        float[] table,
        AmdSmuSensorLayout layout,
        IAmdSmuSession activeSession)
    {
        var stapmPower = request.IncludeStapmPower || request.IncludePackagePower
            ? ReadPower(table, layout.StapmPower)
            : null;
        var actualPower = request.IncludeActualPower || request.IncludePackagePower
            ? ReadPower(table, layout.ActualPower)
            : null;
        var averagePower = request.IncludeAveragePower || request.IncludePackagePower
            ? ReadPower(table, layout.AveragePower)
            : null;
        var packagePower = request.IncludePackagePower
            ? actualPower ?? averagePower ?? stapmPower
            : null;
        var tdcCurrent = request.IncludeTdcCurrent || request.IncludePackageCurrent
            ? ReadCurrent(table, layout.TdcCurrent)
            : null;
        var edcCurrent = request.IncludeEdcCurrent || request.IncludePackageCurrent
            ? ReadCurrent(table, layout.EdcCurrent)
            : null;
        var packageCurrent = request.IncludePackageCurrent
            ? edcCurrent ?? tdcCurrent
            : null;
        var coreVoltage = request.IncludeCoreVoltage
            ? AverageCoreValues(table, layout.CpuVoltageStart, 0.4, 2.0, layout.IsHawkPointVoltageLayout ? 2 : NormalizedCoreSlots())
            : null;
        var temperature = request.IncludeTemperature
            ? ReadTemperature(table, layout.CpuTemperature)
            : null;
        var socPower = request.IncludeSocPower
            ? ReadPower(table, layout.SocPower)
            : null;
        var socVoltage = request.IncludeSocVoltage
            ? ReadVoltage(table, layout.SocVoltage)
            : null;
        var apuFrequency = request.IncludeApuFrequency
            ? NormalizeClockToMhz(ReadClock(table, layout.ApuFrequency))
            : null;
        var apuVoltage = request.IncludeApuVoltage
            ? ReadVoltage(table, layout.ApuVoltage) ?? ReadVoltage(table, layout.ApuVoltageFallback)
            : null;
        var apuTemperature = request.IncludeApuTemperature
            ? ReadTemperature(table, layout.ApuTemperature)
            : null;
        var smuFrequency = request.IncludeSmuFrequency
            ? NormalizeClockToMhz(AverageCoreValues(table, layout.CpuFrequencyStart, 0.2, 8.5, NormalizedCoreSlots()))
            : null;

        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD SMU / PawnIO",
                HardwareSensorProviderStates.Active,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PM table 0x{0:X8}; executor {1}; module {2}",
                    activeSession.TableVersion,
                    activeSession.ExecutorName,
                    activeSession.ModulePath)),
            packagePower,
            coreVoltage,
            packageCurrent,
            temperature,
            request.IncludeStapmPower ? stapmPower : null,
            request.IncludeActualPower ? actualPower : null,
            request.IncludeAveragePower ? averagePower : null,
            request.IncludeTdcCurrent ? tdcCurrent : null,
            request.IncludeEdcCurrent ? edcCurrent : null,
            socPower,
            socVoltage,
            apuFrequency,
            apuVoltage,
            apuTemperature,
            smuFrequency);
    }

    private string? EnsureSession()
    {
        if (session is not null)
        {
            return null;
        }

        if (cachedFailure is not null && clock.GetUtcNow() - cachedFailureAt < failureCacheDuration)
        {
            return cachedFailure;
        }

        try
        {
            session = openSession();
            cachedFailure = null;
            return null;
        }
        catch (AmdSmuProviderUnavailableException ex)
        {
            CacheFailure(ex.Message);
            return ex.Message;
        }
        catch (DllNotFoundException ex)
        {
            CacheFailure(ex.Message);
            return ex.Message;
        }
        catch (BadImageFormatException ex)
        {
            CacheFailure(ex.Message);
            return ex.Message;
        }
        catch (EntryPointNotFoundException ex)
        {
            CacheFailure(ex.Message);
            return ex.Message;
        }
    }

    private void ResetSession()
    {
        session?.Dispose();
        session = null;
    }

    private void CacheFailure(string message)
    {
        cachedFailure = message;
        cachedFailureAt = clock.GetUtcNow();
    }

    private int NormalizedCoreSlots()
    {
        if (physicalCoreCount > 8)
        {
            return 16;
        }

        if (physicalCoreCount > 4)
        {
            return 8;
        }

        return physicalCoreCount > 1 ? 4 : 1;
    }

    private static double? AverageCoreValues(float[] table, int startIndex, double min, double max, int slotCount)
    {
        var sum = 0d;
        var count = 0;
        for (var offset = 0; offset < slotCount; offset++)
        {
            var value = ReadValue(table, startIndex + offset, min, max);
            if (value is null)
            {
                continue;
            }

            sum += value.Value;
            count++;
        }

        return count > 0 ? sum / count : null;
    }

    private static double? ReadPower(float[] table, int index)
    {
        return ReadValue(table, index, 0.001, 500);
    }

    private static double? ReadCurrent(float[] table, int index)
    {
        return ReadValue(table, index, 0.001, 500);
    }

    private static double? ReadVoltage(float[] table, int index)
    {
        return ReadValue(table, index, 0.1, 2.5);
    }

    private static double? ReadTemperature(float[] table, int index)
    {
        return ReadValue(table, index, -40, 150);
    }

    private static double? ReadClock(float[] table, int index)
    {
        return ReadValue(table, index, 0.001, 10000);
    }

    private static double? ReadValue(float[] table, int index, double min, double max)
    {
        if (index < 0 || index >= table.Length)
        {
            return null;
        }

        var value = table[index];
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0 || value < min || value > max)
        {
            return null;
        }

        return value;
    }

    private static double? NormalizeClockToMhz(double? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value <= 20 ? value.Value * 1000d : value.Value;
    }

    private static CpuSensorMetrics CreateNotRequested()
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD SMU / PawnIO",
                HardwareSensorProviderStates.NotRequested,
                "当前快照未请求 AMD SMU CPU 传感指标。"),
            null,
            null,
            null,
            null);
    }

    private static CpuSensorMetrics CreateUnavailable(string message)
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD SMU / PawnIO",
                HardwareSensorProviderStates.Unavailable,
                message),
            null,
            null,
            null,
            null);
    }


}

public sealed record AmdSmuCpuSensorReadRequest(
    bool IncludePackagePower,
    bool IncludeCoreVoltage,
    bool IncludePackageCurrent,
    bool IncludeTemperature,
    bool IncludeStapmPower,
    bool IncludeActualPower,
    bool IncludeAveragePower,
    bool IncludeTdcCurrent,
    bool IncludeEdcCurrent,
    bool IncludeSocPower,
    bool IncludeSocVoltage,
    bool IncludeApuFrequency,
    bool IncludeApuVoltage,
    bool IncludeApuTemperature,
    bool IncludeSmuFrequency)
{
    public bool IncludesAnyMetric =>
        IncludePackagePower
        || IncludeCoreVoltage
        || IncludePackageCurrent
        || IncludeTemperature
        || IncludeStapmPower
        || IncludeActualPower
        || IncludeAveragePower
        || IncludeTdcCurrent
        || IncludeEdcCurrent
        || IncludeSocPower
        || IncludeSocVoltage
        || IncludeApuFrequency
        || IncludeApuVoltage
        || IncludeApuTemperature
        || IncludeSmuFrequency;
}

/// <summary>AMD SMU 这条路的现场状态，给诊断端点用。</summary>
public sealed record AmdSmuSessionDescription(
    bool SessionOpen,
    string? TableVersion,
    int TableLength,
    bool LayoutMapped,
    string? Message,
    IReadOnlyList<double>? Sample = null);
