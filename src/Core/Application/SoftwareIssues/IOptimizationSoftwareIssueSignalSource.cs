namespace ResourceManager.App.Application.SoftwareIssues;

public interface IOptimizationSoftwareIssueSignalSource
{
    OptimizationSoftwareIssueSnapshot ReadSoftwareIssueSnapshot();
}

public sealed record OptimizationSoftwareIssueSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<OptimizationSoftwareIssueSignal> Signals)
{
    public static OptimizationSoftwareIssueSnapshot Empty { get; } = new(
        DateTimeOffset.UnixEpoch,
        []);
}

public sealed record OptimizationSoftwareIssueSignal(
    string ReportId,
    string Kind,
    string Severity,
    string Label,
    string Message,
    string? SoftwareId,
    string? ArtifactPath);
