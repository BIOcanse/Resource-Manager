using System.Xml.Linq;
using Microsoft.Win32;
using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.Shared.Desktop;

namespace Resource_Manager_APP.Tests;

public sealed class LoginStartupRegistrationTests
{
    private const string UserSid = "S-1-5-21-100-200-300-1001";
    private const string OtherUserSid = "S-1-5-21-100-200-300-1002";
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string Executable = Path.GetFullPath(Path.Combine("Bin", "ResourceManagerNativeUi", "test frontend.exe"));

    [Fact]
    public void DefinitionStartsTheFrontendElevatedAtThisUsersLogonWithoutSchedulerLimits()
    {
        var root = XDocument.Parse(FrontendLogonTask.BuildDefinition(Executable, UserSid)).Root!;
        var principal = Assert.Single(root.Element(TaskNs + "Principals")!.Elements(TaskNs + "Principal"));
        Assert.Equal(UserSid, principal.Element(TaskNs + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNs + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(TaskNs + "RunLevel")!.Value);
        var trigger = Assert.Single(root.Element(TaskNs + "Triggers")!.Elements());
        Assert.Equal(TaskNs + "LogonTrigger", trigger.Name);
        Assert.Equal(UserSid, trigger.Element(TaskNs + "UserId")!.Value);
        var settings = root.Element(TaskNs + "Settings")!;
        Assert.Equal("PT0S", settings.Element(TaskNs + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(TaskNs + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(TaskNs + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(TaskNs + "MultipleInstancesPolicy")!.Value);
        Assert.Equal("4", settings.Element(TaskNs + "Priority")!.Value);
        var exec = Assert.Single(root.Element(TaskNs + "Actions")!.Elements());
        Assert.Equal(Executable, exec.Element(TaskNs + "Command")!.Value);
        Assert.Equal("--background-startup", exec.Element(TaskNs + "Arguments")!.Value);
        Assert.Equal(Path.GetDirectoryName(Executable), exec.Element(TaskNs + "WorkingDirectory")!.Value);
        Assert.Equal(FrontendLogonTaskState.Active, FrontendLogonTask.Inspect(root.ToString(), Executable));
        Assert.Equal("ResourceManager.FrontendLogon." + UserSid, FrontendLogonTask.NameFor(UserSid));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("not-a-sid")]
    public void TaskNameNeedsTheUsersAccountSid(string sid)
    {
        Assert.ThrowsAny<ArgumentException>(() => FrontendLogonTask.NameFor(sid));
    }

    [Theory]
    [InlineData("quoted-command", FrontendLogonTaskState.Active)]
    [InlineData("defaults-omitted", FrontendLogonTaskState.Active)]
    [InlineData("task-disabled", FrontendLogonTaskState.Inactive)]
    [InlineData("trigger-disabled", FrontendLogonTaskState.Inactive)]
    [InlineData("no-logon-trigger", FrontendLogonTaskState.Inactive)]
    [InlineData("least-privilege", FrontendLogonTaskState.Inactive)]
    [InlineData("other-executable", FrontendLogonTaskState.Foreign)]
    [InlineData("other-arguments", FrontendLogonTaskState.Foreign)]
    [InlineData("second-action", FrontendLogonTaskState.Foreign)]
    [InlineData("malformed", FrontendLogonTaskState.Foreign)]
    public void InspectionClassifiesRegisteredDefinitions(string variant, FrontendLogonTaskState expected)
    {
        Assert.Equal(expected, FrontendLogonTask.Inspect(Variant(variant), Executable));
    }

    [Fact]
    public void LogonTaskIsTheOnlyStateAndWritesAreReadBack()
    {
        var tasks = new FakeTaskFolder();
        var owner = new LoginStartupRegistration(Executable, UserSid, tasks);
        Assert.False(owner.Read());
        Assert.Empty(tasks.Calls);

        Assert.True(owner.Set(true));
        Assert.Equal(FrontendLogonTask.BuildDefinition(Executable, UserSid), tasks.Tasks[owner.TaskName]);
        Assert.True(new LoginStartupRegistration(Executable, UserSid, tasks).Read());
        Assert.False(new LoginStartupRegistration(Executable, OtherUserSid, tasks).Read());

        Assert.False(owner.Set(false));
        Assert.Empty(tasks.Tasks);
        Assert.Equal(["register " + owner.TaskName, "delete " + owner.TaskName], tasks.Calls);
    }

    [Fact]
    public void InactiveOwnTaskReadsFalseAndIsReRegisteredWhenEnabled()
    {
        var tasks = new FakeTaskFolder();
        var owner = new LoginStartupRegistration(Executable, UserSid, tasks);
        tasks.Tasks[owner.TaskName] = Variant("task-disabled");
        Assert.False(owner.Read());
        Assert.True(owner.Set(true));
        Assert.Equal(FrontendLogonTaskState.Active, FrontendLogonTask.Inspect(tasks.Tasks[owner.TaskName], Executable));
    }

    [Fact]
    public void ForeignTaskUnderTheNameIsNeverOverwrittenOrDeleted()
    {
        var tasks = new FakeTaskFolder();
        var owner = new LoginStartupRegistration(Executable, UserSid, tasks);
        var foreign = Variant("other-executable");
        tasks.Tasks[owner.TaskName] = foreign;
        Assert.Throws<InvalidDataException>(() => owner.Read());
        Assert.Throws<InvalidDataException>(() => owner.Set(true));
        Assert.Throws<InvalidDataException>(() => owner.Set(false));
        Assert.Equal(foreign, tasks.Tasks[owner.TaskName]);
        Assert.Empty(tasks.Calls);
    }

    [Fact]
    public void SchedulerThatDoesNotRetainAWriteIsReportedAsAFailedWrite()
    {
        var tasks = new FakeTaskFolder { IgnoreWrites = true };
        var owner = new LoginStartupRegistration(Executable, UserSid, tasks);
        Assert.Throws<IOException>(() => owner.Set(true));
        tasks.IgnoreWrites = false;
        Assert.True(owner.Set(true));
        tasks.IgnoreWrites = true;
        Assert.Throws<IOException>(() => owner.Set(false));
    }

    [Fact]
    public void UninstallRemovesEveryUsersTaskForThisInterfaceOnly()
    {
        var tasks = new FakeTaskFolder();
        var other = Path.GetFullPath(Path.Combine("Other", "ResourceManager.NativeUi.exe"));
        tasks.Tasks[FrontendLogonTask.NameFor(UserSid)] = FrontendLogonTask.BuildDefinition(Executable, UserSid);
        tasks.Tasks[FrontendLogonTask.NameFor(OtherUserSid)] = Variant("task-disabled");
        tasks.Tasks[FrontendLogonTask.NamePrefix + "S-1-5-21-100-200-300-1003"] = FrontendLogonTask.BuildDefinition(other, "S-1-5-21-100-200-300-1003");
        tasks.Tasks["ResourceManager.UserLogonBootstrap"] = FrontendLogonTask.BuildDefinition(Executable, UserSid);
        tasks.Tasks["OtherSoftware"] = "<Task/>";

        var removed = FrontendLogonTask.RemoveAllFor(tasks, Executable);

        Assert.Equal([FrontendLogonTask.NameFor(UserSid), FrontendLogonTask.NameFor(OtherUserSid)], removed.Order(StringComparer.Ordinal));
        Assert.Equal(["OtherSoftware", FrontendLogonTask.NamePrefix + "S-1-5-21-100-200-300-1003", "ResourceManager.UserLogonBootstrap"],
            tasks.Tasks.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EarlierRunValueBecomesTheLogonTaskAndForeignValuesStay()
    {
        // The isolated Run key must not end in CurrentVersion\Run: security products revert its deletion.
        const string runKey = "StartupRun";
        var name = @"Software\ResourceManager.Tests.LoginStartup\" + Guid.NewGuid().ToString("N");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var root = hive.CreateSubKey(name);
            var tasks = new FakeTaskFolder();
            var owner = new LoginStartupRegistration(Executable, UserSid, tasks);
            Assert.Equal(LegacyLoginStartupOutcome.Absent, LegacyLoginStartupRunValue.Migrate(root, Executable, owner, runKey));
            using var run = root.CreateSubKey(runKey, writable: true);
            Assert.Equal(LegacyLoginStartupOutcome.Absent, LegacyLoginStartupRunValue.Migrate(root, Executable, owner, runKey));
            Assert.Empty(tasks.Calls);

            run.SetValue(LegacyLoginStartupRunValue.ValueName, "foreign.exe --action");
            Assert.Equal(LegacyLoginStartupOutcome.Foreign, LegacyLoginStartupRunValue.Migrate(root, Executable, owner, runKey));
            Assert.Equal("foreign.exe --action", run.GetValue(LegacyLoginStartupRunValue.ValueName));
            Assert.Empty(tasks.Calls);

            // The registration fails while another program holds the task name: the value stays.
            run.SetValue(LegacyLoginStartupRunValue.ValueName, $"\"{Executable}\" --background-startup");
            tasks.Tasks[owner.TaskName] = Variant("other-executable");
            Assert.Throws<InvalidDataException>(() => LegacyLoginStartupRunValue.Migrate(root, Executable, owner, runKey));
            Assert.NotNull(run.GetValue(LegacyLoginStartupRunValue.ValueName));

            tasks.Tasks.Clear();
            Assert.Equal(LegacyLoginStartupOutcome.Migrated, LegacyLoginStartupRunValue.Migrate(root, Executable, owner, runKey));
            Assert.Null(run.GetValue(LegacyLoginStartupRunValue.ValueName));
            Assert.True(owner.Read());
        }
        finally { hive.DeleteSubKeyTree(name, throwOnMissingSubKey: false); }
    }

    private static string Variant(string variant)
    {
        if (variant == "malformed") return "<Task";
        var document = XDocument.Parse(FrontendLogonTask.BuildDefinition(Executable, UserSid));
        var root = document.Root!;
        var exec = root.Element(TaskNs + "Actions")!.Element(TaskNs + "Exec")!;
        switch (variant)
        {
            case "quoted-command": exec.Element(TaskNs + "Command")!.Value = $"\"{Executable}\""; break;
            case "defaults-omitted":
                root.Element(TaskNs + "Settings")!.Element(TaskNs + "Enabled")!.Remove();
                root.Element(TaskNs + "Triggers")!.Element(TaskNs + "LogonTrigger")!.Element(TaskNs + "Enabled")!.Remove();
                break;
            case "task-disabled": root.Element(TaskNs + "Settings")!.Element(TaskNs + "Enabled")!.Value = "false"; break;
            case "trigger-disabled": root.Element(TaskNs + "Triggers")!.Element(TaskNs + "LogonTrigger")!.Element(TaskNs + "Enabled")!.Value = "false"; break;
            case "no-logon-trigger": root.Element(TaskNs + "Triggers")!.RemoveNodes(); break;
            case "least-privilege": root.Element(TaskNs + "Principals")!.Element(TaskNs + "Principal")!.Element(TaskNs + "RunLevel")!.Value = "LeastPrivilege"; break;
            case "other-executable": exec.Element(TaskNs + "Command")!.Value = Path.GetFullPath("foreign.exe"); break;
            case "other-arguments": exec.Element(TaskNs + "Arguments")!.Value = "--show-existing"; break;
            case "second-action": exec.AddAfterSelf(new XElement(exec)); break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        return document.ToString();
    }

    private sealed class FakeTaskFolder : ITaskSchedulerRootFolder
    {
        public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public bool IgnoreWrites { get; set; }
        public string? ReadDefinition(string taskName) => Tasks.GetValueOrDefault(taskName);
        public IReadOnlyList<string> ListTaskNames() => [.. Tasks.Keys];
        public void Register(string taskName, string definitionXml)
        {
            Calls.Add("register " + taskName);
            if (!IgnoreWrites) Tasks[taskName] = definitionXml;
        }
        public void Delete(string taskName)
        {
            Calls.Add("delete " + taskName);
            if (!IgnoreWrites) Tasks.Remove(taskName);
        }
    }
}
