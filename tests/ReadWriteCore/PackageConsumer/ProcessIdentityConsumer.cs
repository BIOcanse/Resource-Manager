using System.Globalization;
using System.Diagnostics;
using System.Security.Principal;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class ProcessIdentityConsumer
{
    internal static void Run()
    {
        string[] names = ["process_all_start_time_actual", "process_all_image_path_actual",
            "process_all_parent_pid_actual", "process_all_user_sid_actual"];
        var definitions = names.Select(name => ProcessMetricFieldRegistry.Find(name)
            ?? throw new InvalidOperationException("Independent process identity field missing: " + name)).ToArray();
        Require((int)ProcessMetricFieldCode.CpuUsage == 14 && (int)ProcessMetricFieldCode.StartTime == 15
            && (int)ProcessMetricFieldCode.UserSid == 18, "original enum values and appended identity fields");
        Require(definitions.Select(field => field.ReadMask).SequenceEqual(new[] { ProcessMetricReadMask.StartTime,
            ProcessMetricReadMask.ImagePath, ProcessMetricReadMask.ParentPid, ProcessMetricReadMask.UserSid }), "explicit selected query masks");
        Require(definitions.All(field => field.SourceId == "windows.process.query" && field.DatasetId == ProcessMetricDatasetIds.BasicMetrics),
            "original query publication owner");
        var birth = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567).ToFileTimeUtc();
        var observation = new ProcessBasicMetricObservation(null, null, null, null)
        { CreationFileTime100Ns = birth, ImagePath = @"C:\Recorded\application.exe", ParentPid = 0, UserSid = "S-1-5-21-1-2-3-1001" };
        var values = definitions.Select(field => ProcessMetricFieldProjection.CreateBasicValue(field, observation)).ToArray();
        Require(values[0].StringValue == "2026-10-07T12:00:00.1234567Z", "100ns FILETIME UTC projection without new clock");
        Require(values[1].StringValue == observation.ImagePath && values[2].IntegerValue == "0"
            && values[3].StringValue == observation.UserSid, "typed recorded path, inherited PID zero and untransformed SID");
        var at = DateTimeOffset.UnixEpoch;
        var snapshot = new ProcessMetricFieldSnapshot(at, null, definitions,
            [new(42, birth.ToString(CultureInfo.InvariantCulture), values)])
        { Sources = [new("windows.process.query", at, null)] };
        Require(snapshot.Select(names.ToHashSet()).WindowMilliseconds is null, "identity query does not acquire CPU interval window");
        Require(snapshot.Clear().Processes.SelectMany(row => row.Values).All(value => value.StringValue is null
            && value.IntegerValue is null && value.NumericValue is null), "clearing includes text values");
        var reused = snapshot with { Processes = [new(42, (birth + 1).ToString(CultureInfo.InvariantCulture), values)] };
        var merged = ProcessMetricFieldSnapshot.Merge([snapshot, reused]);
        Require(merged.Processes.Count == 2 && merged.FindProcess(42, birth.ToString(CultureInfo.InvariantCulture)) is not null,
            "PID reuse preserves separate creation rows");
        var unavailable = new ProcessBasicMetricObservation(null, null, null, null);
        Require(definitions.Select(field => ProcessMetricFieldProjection.CreateBasicValue(field, unavailable))
            .All(value => value.StringValue is null && value.IntegerValue is null && value.NumericValue is null),
            "permission or identity failures remain typed empty");
        Console.WriteLine("Process identity package consumer passed: append-only fields, recorded 100ns UTC, path/SID text, inherited PID, exact creation rows and clearing; no hardware calls.");
    }

    internal static void RunWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = Process.GetCurrentProcess();
        using var user = WindowsIdentity.GetCurrent();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        var mask = ProcessMetricReadMask.StartTime | ProcessMetricReadMask.ImagePath
            | ProcessMetricReadMask.ParentPid | ProcessMetricReadMask.UserSid;
        var reader = new WindowsProcessBasicMetricReader();
        var value = reader.Read(process.Id, birth, mask);
        Require(value.CreationFileTime100Ns == birth && value.ImagePath is not null
            && string.Equals(value.ImagePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && value.ParentPid.HasValue && value.UserSid == user.User?.Value,
            "actual creation-verified read-only native identity query");
        var reused = reader.Read(process.Id, birth + 1, mask);
        Require(reused.CreationFileTime100Ns is null && reused.ImagePath is null
            && reused.ParentPid is null && reused.UserSid is null, "actual wrong creation rejected");
        Console.WriteLine("Process identity Windows consumer passed: actual creation, executable path, inherited parent PID and token SID on one verified handle; no process or registry writes.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
