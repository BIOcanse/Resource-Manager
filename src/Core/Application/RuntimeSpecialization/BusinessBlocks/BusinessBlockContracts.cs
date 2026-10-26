namespace ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

public enum BusinessBlockOutcome { Done, Stopped, Failed }

public enum BusinessBlockFailureStage { Start, Run, Stop, ReportedFailure }

public sealed record BusinessBlockResult(BusinessBlockOutcome Outcome, string? FailureType = null, BusinessBlockFailureStage? FailureStage = null)
{
    internal Exception? Error { get; init; }
    public static BusinessBlockResult Done { get; } = new(BusinessBlockOutcome.Done);
    public static BusinessBlockResult Stopped { get; } = new(BusinessBlockOutcome.Stopped);
    public static BusinessBlockResult Failed(Exception error, BusinessBlockFailureStage stage = BusinessBlockFailureStage.Run)
        => new(BusinessBlockOutcome.Failed, error.GetType().FullName, stage) { Error = error };
}

public sealed class BusinessBlockOptions
{
    public const string SectionName = "BusinessBlocks";
    public int HistoryLimit { get; set; } = 128;
}

public interface IBusinessBlockDiagnostics
{
    void ReportFailure(string blockId, Exception error, string operation);
    IDisposable MeasureSegment(string blockId);
}

public sealed record BusinessBlockDiagnostics(string Id, BusinessBlockResult? Result, BusinessBlockMetricFacts Metrics);
public sealed record BusinessBlockDebugSnapshot(BusinessBlocksSnapshot Scheduling, IReadOnlyList<BusinessBlockDiagnostics> Diagnostics);
