using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

// The helper-chain facts of one process instance: whether it is a runtime helper,
// and the WebView host it names. They depend only on the name, path and command
// line it was read from, so the existing per-instance attribution entry keeps
// them and later batches reuse them instead of matching the command line again.
internal sealed record RuntimeProcessHelperFacts(
    int ProcessId,
    long? StartKey,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    bool IsHelper,
    string? WebViewHostName)
{
    internal bool Describes(RuntimeProcessIdentity process)
        => process.ProcessId == ProcessId
            && process.StartKey == StartKey
            && string.Equals(process.Name, Name, StringComparison.Ordinal)
            && string.Equals(process.ExecutablePath, ExecutablePath, StringComparison.Ordinal)
            && string.Equals(process.CommandLine, CommandLine, StringComparison.Ordinal);
}
