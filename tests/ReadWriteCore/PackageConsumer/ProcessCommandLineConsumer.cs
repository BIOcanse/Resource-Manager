using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class ProcessCommandLineConsumer
{
    private const string Name = "process_all_command_line_actual";
    internal static void Run()
    {
        var field = ProcessMetricFieldRegistry.Find(Name) ?? throw new InvalidOperationException("Command line field missing");
        Require((int)field.Code == 20 && (int)field.ReadMask == 1024 && ProcessMetricFieldRegistry.Definitions.Count == 34, "appended directory/mask");
        Require(field.SourceId == "windows.process.query" && field.DatasetId == ProcessMetricDatasetIds.BasicMetrics && field.ValueKind == "string", "original query source/string contract");
        foreach (var text in new[] { "", "  ", "\"app path.exe\" --arg=\"raw value\"", "中文 😀" })
        {
            var observation = new ProcessBasicMetricObservation(null, null, null, null) { CommandLine = text };
            var value = ProcessMetricFieldProjection.CreateBasicValue(field, observation);
            Require(value.StringValue == text && value.IntegerValue is null && value.NumericValue is null, "exact raw text including valid empty");
            var snapshot = new ProcessMetricFieldSnapshot(DateTimeOffset.UnixEpoch, null, [field], [new(42, "100", [value])]);
            Require(snapshot.Clear().Processes.Single().Values.Single().StringValue is null, "original text clear");
            Require(snapshot.Select(new HashSet<string> { Name }).WindowMilliseconds is null, "no CPU interval");
        }
        Require(ProcessMetricFieldProjection.CreateBasicValue(field, new(null, null, null, null)).StringValue is null, "unavailable is distinct from empty");
        Console.WriteLine("Process command-line recorded canonical consumer passed: directory21, code20/mask1024, raw text/valid empty/clear; recorded projection does not prove native transport.");
    }
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var current = Process.GetCurrentProcess();
        var birth = current.StartTime.ToFileTimeUtc();
        // Environment.CommandLine may describe managed application arguments,
        // excluding the dotnet host. Compare a raw class60 read with the OS's
        // raw current-process command line rather than a managed reconstruction.
        var expectedRaw = Marshal.PtrToStringUni(GetCommandLineW())
            ?? throw new InvalidOperationException("OS current-process command line unavailable");
        var direct = new WindowsProcessBasicMetricReader().Read(Environment.ProcessId, birth, ProcessMetricReadMask.CommandLine);
        Console.WriteLine($"Process command-line raw comparison: native-length={direct.CommandLine?.Length}; OS-length={expectedRaw.Length}; managed-length={Environment.CommandLine.Length}; same-OS={direct.CommandLine == expectedRaw}; same-managed={direct.CommandLine == Environment.CommandLine}; text-not-logged.");
        Require(direct.CommandLine == expectedRaw, "actual same-identity current-process raw command line");
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        await owner.CollectAsync([Name], timeout.Token);
        var identity = $"windows-process|{Environment.ProcessId}:{birth.ToString(CultureInfo.InvariantCulture)}";
        var field = owner.Backend.Catalog.Fields.Single(item => item.Field.FieldName == Name && item.Field.DeviceIdentity == identity).Field;
        var values = await owner.Backend.CollectAsync([field], timeout.Token);
        Require(values.Single().Status == FieldObservationStatus.Current && values.Single().Value?.Text == expectedRaw, "actual public selected canonical text");
        Require(values.Single() == owner.Backend.ReadCached(field), "same original current");
        Require(values.Single().SampleDurationMilliseconds is null, "command line has no CPU window");
        Require(owner.Backend.Catalog.Require(field).Read.Supported && !owner.Backend.Catalog.Require(field).Write.Supported, "read-only public support");
        Console.WriteLine("Process command-line actual Windows readonly consumer passed: PID/FILETIME verified native text and default owner selected canonical/current; no text logged or ETW requested.");
    }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCommandLineW();
}
