using System.Globalization;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>The product's original exact conversion of process query counters to canonical fields.</summary>
public static class ProcessMetricFieldProjection
{
    public static ProcessMetricFieldValue CreateCpuUsageValue(ProcessMetricFieldDefinition field, double? percent)
        => new(field.Name, null, percent is { } value && double.IsFinite(value) && value is >= 0 and <= 100 ? value : null);

    public static ProcessMetricFieldValue CreateBasicValue(ProcessMetricFieldDefinition field, ProcessBasicMetricObservation value)
    {
        if (field.Code is ProcessMetricFieldCode.StartTime or ProcessMetricFieldCode.ImagePath or ProcessMetricFieldCode.UserSid or ProcessMetricFieldCode.CommandLine)
        {
            var text = field.Code switch
            {
                ProcessMetricFieldCode.StartTime => CreationUtc(value.CreationFileTime100Ns),
                ProcessMetricFieldCode.ImagePath => value.ImagePath,
                ProcessMetricFieldCode.UserSid => value.UserSid,
                ProcessMetricFieldCode.CommandLine => value.CommandLine,
                _ => null
            };
            return new(field.Name, null, null) { StringValue = field.Code == ProcessMetricFieldCode.CommandLine ? text : string.IsNullOrWhiteSpace(text) ? null : text };
        }
        if (field.Code == ProcessMetricFieldCode.CpuTime)
            return new(field.Name, null, value.CpuTime100Ns is { } time ? (double)time / 10_000_000d : null);
        UInt128? count = field.Code switch
        {
            ProcessMetricFieldCode.WorkingSet => value.Memory?.WorkingSet,
            ProcessMetricFieldCode.PrivateWorkingSet => value.Memory?.PrivateWorkingSet,
            ProcessMetricFieldCode.PrivateCommit => value.Memory?.PrivateCommit,
            ProcessMetricFieldCode.PeakPrivateCommit => value.Memory?.PeakPrivateCommit,
            ProcessMetricFieldCode.IoReadBytes => value.Io?.ReadBytes,
            ProcessMetricFieldCode.IoWriteBytes => value.Io?.WriteBytes,
            ProcessMetricFieldCode.IoOtherBytes => value.Io?.OtherBytes,
            ProcessMetricFieldCode.IoReadOperations => value.Io?.ReadOperations,
            ProcessMetricFieldCode.IoWriteOperations => value.Io?.WriteOperations,
            ProcessMetricFieldCode.IoOtherOperations => value.Io?.OtherOperations,
            ProcessMetricFieldCode.IoOperations => value.Io is { } io ? (UInt128)io.ReadOperations + io.WriteOperations + io.OtherOperations : null,
            ProcessMetricFieldCode.HandleCount => value.HandleCount,
            ProcessMetricFieldCode.ParentPid => value.ParentPid,
            ProcessMetricFieldCode.ThreadCount => value.ThreadCount,
            _ => null
        };
        return new(field.Name, count?.ToString(CultureInfo.InvariantCulture), null);
    }

    private static string? CreationUtc(long? fileTime)
    {
        if (fileTime is not > 0) return null;
        try { return DateTime.FromFileTimeUtc(fileTime.Value).ToString("O", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
