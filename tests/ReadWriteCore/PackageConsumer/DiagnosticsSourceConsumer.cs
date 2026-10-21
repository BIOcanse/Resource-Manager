using ResourceManager.ReadWrite.Windows.Diagnostics;

internal static class DiagnosticsSourceConsumer
{
    internal static async Task RunWindowsAsync()
    {
        var through = DateTimeOffset.UtcNow;
        await WindowsWheaHistoryConsumer.RunWindowsAsync(new("System", through - TimeSpan.FromDays(7),
            through, 64, TimeSpan.FromSeconds(5)));
        var dump = Environment.GetEnvironmentVariable("RM_SDK_EXISTING_DUMP_PATH");
        var engine = Environment.GetEnvironmentVariable("RM_SDK_DBGENG_PATH");
        if (string.IsNullOrWhiteSpace(dump) || string.IsNullOrWhiteSpace(engine))
        {
            Console.WriteLine("Dump actual source not selected; passive catalog alone does not verify DbgEng.");
            return;
        }
        var helper = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "ResourceManager.DumpAnalysis.exe");
        if (!File.Exists(dump) || !File.Exists(engine) || !File.Exists(helper))
            throw new InvalidDataException("Selected existing dump, installed engine and packaged helper must exist.");
        var result = await WindowsDumpAnalysisConsumer.AnalyzeExisting("package-consumer|explicit-existing-dump",
            new(dump, helper, engine, [], TimeSpan.FromSeconds(20)));
        Console.WriteLine($"Dump actual existing-source outcome: status={result.Status}; bugcheck={result.BugcheckStatus}; exception={result.ExceptionStatus}; stack={result.StackStatus}; frames={result.SymbolizedStackInterpreted?.Length ?? 0}; same-current=yes; no-live-attach; no-symbol-download.");
        if (result.Status is not (WindowsDumpAnalysisStatus.Available or WindowsDumpAnalysisStatus.Partial)
            || result.ExceptionContextInterpreted is null)
            throw new InvalidDataException($"Actual selected DbgEng source could not interpret the existing user dump: {result.Status}; {result.Diagnostic}");
    }
}
