using Microsoft.Win32;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class StartupConfigurationOwnerTests
{
    private const string Xml = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger><UserId>S-1-5-21-123</UserId><Enabled>false</Enabled></LogonTrigger></Triggers><Principals><Principal><GroupId>S-1-5-32-545</GroupId></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions><Exec><Command>%CONFIGURED%\\program.exe</Command><Arguments>--exact</Arguments></Exec></Actions></Task>";
    private static WindowsPlatformDeclarationFacts Facts() => new(null, [],
        [new("HKU", RegistryView.Registry64, "S-1-5-21-123", @"Software\Microsoft\Windows\CurrentVersion\Run", "SameName", RegistryValueKind.ExpandString, "%CONFIGURED%\\run.exe")], null)
    {
        StartupAdditional = [new(WindowsStartupConfigurationSource.StartupFolderFile, "user-sid:S-1-5-21-123", "recorded-folder/SameName.lnk",
            System.Text.Json.JsonSerializer.Serialize(new WindowsStartupFileDeclaration("recorded-folder/SameName.lnk", "shell-link", "%CONFIGURED%\\folder.exe", "--exact", "recorded-working-directory"))),
            WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\SameName", Xml)!]
    };

    [Fact]
    public void ProductionTaskProjectionRetainsDisabledBootLogonConfigurationAndRejectsDailyOnly()
    {
        var logon = WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\Logon", Xml);
        Assert.NotNull(logon); Assert.Equal(Xml, logon.ConfiguredPayload); Assert.Equal("local-task-scheduler", logon.Scope);
        Assert.Equal("GroupId:S-1-5-32-545", logon.PrincipalDeclaration); Assert.Contains("<Enabled>false</Enabled>", logon.ConfiguredPayload);
        var bootXml = Xml.Replace("LogonTrigger", "BootTrigger", StringComparison.Ordinal);
        Assert.Equal(bootXml, WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\Boot", bootXml)!.ConfiguredPayload);
        Assert.Null(WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\Daily", Xml.Replace("LogonTrigger", "CalendarTrigger", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\Unsupported", "<Task/>"));
        Assert.ThrowsAny<System.Xml.XmlException>(() => WindowsStartupConfigurationReader.DecodeTask(@"\Recorded\Dtd", "<!DOCTYPE Task [<!ENTITY x SYSTEM 'file:///recorded'>]><Task>&x;</Task>"));
    }

    [Fact]
    public void ExplicitTargetUserFolderExpansionDoesNotBorrowCallingSystemEnvironment()
    {
        var target = Path.Combine(Path.GetTempPath(), "recorded-target-user", "Roaming");
        var configured = "%APPDATA%" + Path.DirectorySeparatorChar + "Startup";
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["APPDATA"] = target };
        Assert.Equal(Path.Combine(target, "Startup"), WindowsStartupConfigurationReader.ResolveUserFolder(configured, map));
        Assert.Throws<InvalidDataException>(() => WindowsStartupConfigurationReader.ResolveUserFolder("%MISSING_TARGET_VALUE%/Startup", map));
        Assert.Throws<InvalidDataException>(() => WindowsStartupConfigurationReader.ResolveUserFolder(configured, new Dictionary<string, string?> { ["APPDATA"] = "%INHERITED_VALUE%" }));
    }

    [Fact]
    public async Task RegistryFolderAndTaskKeepSeparateSourceIdentitiesInOneFieldAndOriginalCurrent()
    {
        var requested = new List<WindowsOperatingSystemInformationFields>();
        using var owner = Owner((selected, _) => { requested.Add(selected); return Facts(); });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Empty(requested);
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Startup);
        var startup = backend.Catalog.Fields.Where(field => field.Field.FieldName == "startup_all_entry_configured").ToArray();
        Assert.Equal(3, startup.Length); Assert.Equal(3, startup.Select(field => field.Field.DeviceIdentity).Distinct().Count());
        Assert.Contains(startup, field => field.Field.DeviceIdentity.StartsWith("windows-registry|", StringComparison.Ordinal));
        Assert.Contains(startup, field => field.Field.DeviceIdentity.Contains("StartupFolderFile", StringComparison.Ordinal));
        Assert.Contains(startup, field => field.Field.DeviceIdentity.Contains("TaskSchedulerBootOrLogon", StringComparison.Ordinal));
        var task = startup.Single(field => field.Field.DeviceIdentity.Contains("TaskSchedulerBootOrLogon", StringComparison.Ordinal)).Field;
        var result = (await backend.CollectAsync([task], default)).Single();
        Assert.Equal(WindowsOperatingSystemInformationFields.Startup, requested.Last()); Assert.Equal(Xml, result.Value!.Text);
        Assert.Equal(result, backend.ReadCached(task)); Assert.Equal("windows.startup.configuration", result.SourceId);
    }

    [Fact]
    public async Task IncompleteSourceDoesNotRetireUnqueriedOriginsAndSuccessfulEmptyRetiresAll()
    {
        var partial = false; var empty = false;
        using var owner = Owner((_, _) => empty ? new(null, [], [], null) { StartupAdditional = [] }
            : partial ? new(null, [], null, null) { StartupAdditional = [Facts().StartupAdditional![0]] } : Facts());
        var backend = owner.CreateBackend(); var catalog = await backend.DiscoverAsync(default);
        var fields = catalog.Fields.Where(field => field.Field.FieldName == "startup_all_entry_configured").Select(field => field.Field).ToArray();
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([.. fields]);
        partial = true; var unavailable = await borrow.CollectAsync([.. fields], new Permit());
        Assert.All(unavailable.Readings, reading => { Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); Assert.Null(reading.ObservedAt); });
        Assert.Equal(3, backend.Catalog.Fields.Count(field => field.Field.FieldName == "startup_all_entry_configured"));
        empty = true; var retired = await borrow.CollectAsync([.. fields], new Permit());
        Assert.All(retired.Readings, reading => Assert.Equal(FieldObservationStatus.Unavailable, reading.Status));
        Assert.DoesNotContain(backend.Catalog.Fields, field => field.Field.FieldName == "startup_all_entry_configured");
    }
    private static WindowsOperatingSystemInformationOwner Owner(Func<WindowsOperatingSystemInformationFields, CancellationToken, WindowsPlatformDeclarationFacts> read)
        => new(() => new(TimeSpan.FromSeconds(7), 7), new(TimeSpan.FromSeconds(2), static (_, _, _) => new([], "Recorded no WMI")), new WindowsPlatformDeclarationReader(read));
    private sealed class Permit : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => true; public bool TryPublish(Action commit) { commit(); return true; } }
}
