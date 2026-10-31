using System.IO.Compression;
using System.Text.Json;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.CrashReports;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using static Resource_Manager_APP.Tests.CrashReportTestSupport;

namespace Resource_Manager_APP.Tests;

// 崩溃记录 discovery, package, issue link, acknowledge and delete (docs/crash_dumps_2026-10-31.md 发现/接口/已读状态).
public sealed class CrashReportOwnerTests
{
    private static readonly DateTimeOffset Crash = new(2026, 10, 30, 15, 25, 6, TimeSpan.Zero);

    [Fact]
    public void StartupRegistersANewDumpWithItsEventLogSummaryAndNotifiesOnce()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddHours(-1));
        var events = new FakeEvents();
        // The service started this version before it crashed: that start records the version history.
        Owner(root.Path, new FixedPlan(Plan()), events, time).Owner.DiscoverAtStartup();
        time.Now = Crash.AddMinutes(1);
        events.Crashes.Add(ApplicationError(Crash.AddSeconds(1), "ResourceManager.exe"));
        events.Crashes.Add(DotNetRuntime(Crash.AddSeconds(2), "ResourceManager.exe"));
        // Another program's crash at the same time is not RM's.
        events.Crashes.Add(ApplicationError(Crash, "other.exe"));
        var dump = WriteDump(root.Path, "ResourceManager.exe", 4242, Crash);
        var (owner, notifications) = Owner(root.Path, new FixedPlan(Plan()), events, time);

        owner.DiscoverAtStartup();
        var report = Assert.Single(owner.Read().Reports);
        Assert.Equal(("ResourceManager.exe", "service", CrashReportDumpStatuses.Saved, BetaVersion),
            (report.Program, report.Role, report.DumpStatus, report.Version));
        Assert.Equal(new CrashReportDump(dump, 4096), report.Dump);
        Assert.Equal(("ntdll.dll", "0x00000000000a1b2c", "0xc0000005"),
            (report.Fault!.Module, report.Fault.Offset, report.Fault.ExceptionCode));
        Assert.Equal("at A.B()\nat C.D()", report.Fault.ManagedStack);
        Assert.False(report.Acknowledged);
        Assert.False(report.Reported);

        var posted = Assert.Single(notifications.Read().Notifications);
        Assert.Equal(NotificationKinds.CrashReport.Name, posted.Kind);
        Assert.Equal(1, posted.Facts.GetProperty("count").GetInt32());
        Assert.Equal(report.Id, posted.Facts.GetProperty("latestReportId").GetString());
        Assert.Equal("service", posted.Facts.GetProperty("role").GetString());

        // The next start finds nothing new and posts nothing.
        time.Now = time.Now.AddHours(1);
        var (restarted, restartedNotifications) = Owner(root.Path, new FixedPlan(Plan()), events, time);
        restarted.DiscoverAtStartup();
        Assert.Single(restarted.Read().Reports);
        Assert.Empty(restartedNotifications.Read().Notifications);
    }

    [Fact]
    public void AReusedPidRegistersTheNewDumpAndDeletingItsOlderRecordKeepsTheNewFile()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        var path = WriteDump(root.Path, "ResourceManager.exe", 7, Crash);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan()), new FakeEvents(), time);
        var previous = Assert.Single(owner.Read().Reports);
        WriteDump(root.Path, "ResourceManager.exe", 7, Crash.AddMinutes(5));
        time.Now = Crash.AddMinutes(6);
        var reports = owner.Read().Reports;
        Assert.Equal(2, reports.Count);
        Assert.Equal(CrashReportDumpStatuses.Saved, reports[0].DumpStatus);
        Assert.Equal(CrashReportDumpStatuses.Missing, reports[1].DumpStatus);
        using (var archive = ZipFile.OpenRead(owner.Package(previous.Id)!.Package.Path))
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".dmp", StringComparison.Ordinal));
        Assert.True(owner.Delete(previous.Id));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DumpsInTheSameSecondHaveDistinctPersistentIds()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        WriteDump(root.Path, "ResourceManager.exe", 1, Crash);
        WriteDump(root.Path, "ResourceManager.exe", 2, Crash);
        var plans = new FixedPlan(Plan());
        var (owner, _) = Owner(root.Path, plans, new FakeEvents(), time);
        var reports = owner.Read().Reports;
        Assert.Equal(2, reports.Select(report => report.Id).Distinct().Count());
        Assert.Equal(2, Owner(root.Path, plans, new FakeEvents(), time).Owner.Read().Reports.Count);
    }

    [Fact]
    public void AScanDoesNotRediscoverAnOlderDumpAfterTheWatermark()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash);
        var plans = new FixedPlan(Plan());
        var (owner, _) = Owner(root.Path, plans, new FakeEvents(), time);
        owner.DiscoverAtStartup();
        WriteDump(root.Path, "ResourceManager.exe", 1, Crash.AddMinutes(-1));
        time.Now = Crash.AddMinutes(1);
        Assert.Empty(owner.Read().Reports);
        WriteDump(root.Path, "ResourceManager.Desktop.exe", 2, Crash.AddMinutes(2));
        time.Now = Crash.AddMinutes(3);
        Assert.Equal("resident", Assert.Single(owner.Read().Reports).Role);
    }

    [Fact]
    public void AnIssueWithAnExtremelyLongFaultStillFitsTheDeclaredUrlLimit()
    {
        var report = new CrashReport("crash-test", Crash, "ResourceManager.exe", "service", BetaVersion,
            CrashReportDumpStatuses.NotEnabled, null, new(new string('故', 20_000), null, null, null,
                new string('障', 20_000)), null, false, false);
        var url = CrashIssueLink.Build(report, Options);
        Assert.True(url.Length <= Options.IssueUrlMaxChars);
        Assert.Contains(Uri.EscapeDataString("crash-test.zip"), url);
    }

    [Fact]
    public async Task SubscriberCapacityComesFromTheFreedomPointAndIsReleasedOnDispose()
    {
        using var root = new TempRoot();
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan(options: Options with { SubscriberCapacity = 1 })),
            new FakeEvents(), new ManualTime(Crash));
        await using var first = owner.SubscribeAsync(default).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await using var second = owner.SubscribeAsync(default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        await first.DisposeAsync();
        await using var replacement = owner.SubscribeAsync(default).GetAsyncEnumerator();
        Assert.True(await replacement.MoveNextAsync());
    }

    [Fact]
    public void ARescanOnReadRegistersAWindowCrashWithoutANotification()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash);
        var events = new FakeEvents();
        var (owner, notifications) = Owner(root.Path, new FixedPlan(Plan()), events, time);
        owner.DiscoverAtStartup();
        Assert.Empty(owner.Read().Reports);

        WriteDump(root.Path, "ResourceManager.NativeUi.exe", 77, Crash.AddMinutes(5));
        time.Now = Crash.AddMinutes(6);
        var report = Assert.Single(owner.Read().Reports);
        Assert.Equal("window", report.Role);
        Assert.Null(report.Fault);
        Assert.Empty(notifications.Read().Notifications);
    }

    [Theory]
    [InlineData("ask", CrashReportDumpStatuses.NotEnabled)]
    [InlineData("off", CrashReportDumpStatuses.NotEnabled)]
    [InlineData("on", CrashReportDumpStatuses.Missing)]
    public void ACrashOnlyInTheEventLogIsRegisteredWithoutADump(string mode, string status)
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        var events = new FakeEvents();
        events.Crashes.Add(ApplicationError(Crash, "ResourceManager.exe", "coreclr.dll"));
        events.Crashes.Add(DotNetRuntime(Crash.AddSeconds(3), "ResourceManager.exe"));
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan(new AppCrashDumpSettings(mode))), events, time);
        owner.DiscoverAtStartup();
        // 1000 and 1026 within the merge window are one crash.
        var report = Assert.Single(owner.Read().Reports);
        Assert.Equal(status, report.DumpStatus);
        Assert.Null(report.Dump);
        Assert.Equal("coreclr.dll", report.Fault!.Module);
        Assert.NotNull(report.Fault.ManagedStack);
    }

    [Fact]
    public void TheKeepCountKeepsTheNewestRecordsPerProgramAndDeletesTheRest()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddHours(1));
        var paths = Enumerable.Range(0, 4)
            .Select(index => WriteDump(root.Path, "ResourceManager.exe", 100 + index, Crash.AddMinutes(index * 10))).ToArray();
        var window = WriteDump(root.Path, "ResourceManager.NativeUi.exe", 9, Crash);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan(new AppCrashDumpSettings(KeepCount: 2))), new FakeEvents(), time);
        owner.DiscoverAtStartup();
        var reports = owner.Read().Reports;
        Assert.Equal(3, reports.Count);
        Assert.Equal([paths[3], paths[2]], reports.Where(static report => report.Role == "service").Select(static report => report.Dump!.Path));
        Assert.False(File.Exists(paths[0]));
        Assert.False(File.Exists(paths[1]));
        Assert.True(File.Exists(window));
    }

    [Fact]
    public void AGoneDumpIsMarkedMissingAndAnUnknownFileIsIgnored()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        var dump = WriteDump(root.Path, "ResourceManager.exe", 5, Crash);
        File.WriteAllBytes(Path.Combine(DumpFolder(root.Path), "notes.dmp"), [1]);
        File.WriteAllBytes(Path.Combine(DumpFolder(root.Path), "explorer.exe.12.dmp"), [1]);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan()), new FakeEvents(), time);
        Assert.Single(owner.Read().Reports);
        File.Delete(dump);
        var report = Assert.Single(owner.Read().Reports);
        Assert.Equal(CrashReportDumpStatuses.Missing, report.DumpStatus);
        Assert.Equal(dump, report.Dump!.Path);
    }

    [Fact]
    public void ThePackageHoldsTheWholeDumpReportEventsAndServiceLog()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        var events = new FakeEvents();
        events.Crashes.Add(ApplicationError(Crash, "ResourceManager.exe"));
        events.ServiceLog.Add(new(Crash.AddSeconds(-30), "Error", "Native kernel ETW processing failed."));
        events.ServiceLog.Add(new(Crash.AddHours(-2), "Error", "Long before the crash."));
        var dump = WriteDump(root.Path, "ResourceManager.exe", 5, Crash, bytes: 200_000);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan()), events, time);
        var id = Assert.Single(owner.Read().Reports).Id;

        var result = owner.Package(id)!;
        Assert.Null(result.Reason);
        Assert.False(result.Package.ExceedsIssueLimit);
        Assert.Equal(new FileInfo(result.Package.Path).Length, result.Package.SizeBytes);
        Assert.Equal(result.Package, result.Report.Package);
        using (var archive = ZipFile.OpenRead(result.Package.Path))
        {
            Assert.Equal(["ResourceManager.exe.5.dmp", "events.txt", "report.json", "service-log.txt"],
                archive.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal));
            Assert.Equal(200_000, archive.GetEntry("ResourceManager.exe.5.dmp")!.Length);
            using var log = new StreamReader(archive.GetEntry("service-log.txt")!.Open());
            var text = log.ReadToEnd();
            Assert.Contains("Native kernel ETW processing failed.", text);
            Assert.DoesNotContain("Long before the crash.", text);
            using var json = JsonDocument.Parse(archive.GetEntry("report.json")!.Open());
            Assert.Equal(BetaVersion, json.RootElement.GetProperty("buildVersion").GetString());
            Assert.Equal(id, json.RootElement.GetProperty("report").GetProperty("id").GetString());
        }
        Assert.True(File.Exists(dump));
    }

    [Fact]
    public void APackageOverTheIssueLimitIsReportedNotTruncated()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        WriteDump(root.Path, "ResourceManager.exe", 5, Crash, bytes: 100_000);
        var plan = Plan(options: Options with { IssueAttachmentLimitBytes = 1_000 });
        var (owner, _) = Owner(root.Path, new FixedPlan(plan), new FakeEvents(), time);
        var result = owner.Package(Assert.Single(owner.Read().Reports).Id)!;
        Assert.Equal(CrashReportPackageReasons.ExceedsIssueAttachmentLimit, result.Reason);
        Assert.True(result.Package.ExceedsIssueLimit);
        using var archive = ZipFile.OpenRead(result.Package.Path);
        Assert.Equal(100_000, archive.GetEntry("ResourceManager.exe.5.dmp")!.Length);
    }

    [Fact]
    public void TheIssueLinkIsEncodedBoundedAndHasNoLocalPath()
    {
        var report = new CrashReport("20261030T152506Z-ResourceManager.exe", Crash, "ResourceManager.exe", "service",
            BetaVersion, CrashReportDumpStatuses.Saved, new("C:\\Users\\someone\\x.dmp", 10),
            new("ntdll.dll", "0x1", "0xc0000005", string.Join('\n', Enumerable.Range(0, 40).Select(static i => $"at Frame{i}() & more")),
                "The process was terminated."), null, false, false);
        var url = CrashIssueLink.Build(report, Options);
        Assert.StartsWith(Options.IssueNewUrl + "?title=", url);
        Assert.True(url.Length <= Options.IssueUrlMaxChars);
        var query = new Uri(url).Query.TrimStart('?').Split('&')
            .ToDictionary(static part => part[..part.IndexOf('=')], static part => Uri.UnescapeDataString(part[(part.IndexOf('=') + 1)..]));
        Assert.Contains("ResourceManager.exe", query["title"]);
        Assert.Contains("ntdll.dll", query["title"]);
        Assert.Contains(BetaVersion, query["body"]);
        Assert.Contains("0xc0000005", query["body"]);
        Assert.Contains(report.Id + ".zip", query["body"]);
        Assert.Contains("at Frame0() & more", query["body"]);
        Assert.DoesNotContain($"Frame{Options.IssueStackMaxLines}()", query["body"]);
        Assert.DoesNotContain("C:\\Users", query["body"]);

        // A small bound quotes fewer stack lines and still fits.
        var tight = Options with { IssueUrlMaxChars = 900 };
        var shortUrl = CrashIssueLink.Build(report, tight);
        Assert.True(shortUrl.Length <= 900, $"{shortUrl.Length}");
        Assert.Contains(Uri.EscapeDataString(report.Id + ".zip"), shortUrl);
    }

    [Fact]
    public void IssueLinkMarksReportedAcknowledgePersistsAndDeleteRemovesFiles()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        var dump = WriteDump(root.Path, "ResourceManager.exe", 5, Crash);
        var plans = new FixedPlan(Plan());
        var (owner, _) = Owner(root.Path, plans, new FakeEvents(), time);
        var id = Assert.Single(owner.Read().Reports).Id;

        Assert.True(owner.IssueLink(id)!.Report.Reported);
        Assert.True(owner.Acknowledge(id)!.Report.Acknowledged);
        var package = owner.Package(id)!.Package.Path;
        Assert.Null(owner.Acknowledge("unknown"));
        Assert.Null(owner.Package("unknown"));
        Assert.Null(owner.IssueLink("unknown"));
        Assert.False(owner.Delete("unknown"));

        var (reloaded, _) = Owner(root.Path, plans, new FakeEvents(), time);
        var stored = Assert.Single(reloaded.Read().Reports);
        Assert.True(stored.Acknowledged && stored.Reported);
        Assert.True(reloaded.Delete(id));
        Assert.Empty(reloaded.Read().Reports);
        Assert.False(File.Exists(dump));
        Assert.False(File.Exists(package));
    }

    [Fact]
    public async Task SubscribersGetTheListThenEveryChange()
    {
        using var root = new TempRoot();
        var time = new ManualTime(Crash.AddMinutes(1));
        WriteDump(root.Path, "ResourceManager.exe", 5, Crash);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan()), new FakeEvents(), time);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var frames = owner.SubscribeAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Assert.True(await frames.MoveNextAsync());
        var report = Assert.Single(frames.Current.Reports);
        Assert.Equal(DumpFolder(root.Path), frames.Current.DumpFolder);
        owner.Acknowledge(report.Id);
        Assert.True(await frames.MoveNextAsync());
        Assert.True(Assert.Single(frames.Current.Reports).Acknowledged);
    }

    [Theory]
    [InlineData("ResourceManager.exe.4242.dmp", "ResourceManager.exe")]
    [InlineData("ResourceManager.NativeUi.exe.7.dmp", "ResourceManager.NativeUi.exe")]
    [InlineData("ResourceManager.exe.dmp", null)]
    [InlineData("notes.dmp", null)]
    [InlineData("ResourceManager.exe.12.txt", null)]
    public void DumpFileNamesNameTheirImage(string name, string? image)
        => Assert.Equal(image, CrashDumpFileName.ImageOf(name));

    [Fact]
    public void DotNetRuntimeMessagesParseImageDescriptionAndStack()
    {
        var parsed = CrashEventText.ParseDotNetRuntime(
            "Application: ResourceManager.exe\r\nCoreCLR Version: 10.0\r\nDescription: The process was terminated due to an unhandled exception.\r\nStack:\r\n   at A.B()\r\n   at C.D()\r\n");
        Assert.Equal(("ResourceManager.exe", "The process was terminated due to an unhandled exception.", "at A.B()\nat C.D()"), parsed);
    }

    [Fact]
    public async Task TheServiceBlockRunsDiscoveryOnceWithTheFirstPlan()
    {
        var plans = new ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimePlanProvider(
            new ResourceManager.App.Infrastructure.RuntimeSpecialization.HostManagerDeploymentState());
        var owner = new RecordingOwner();
        var service = new ServiceRecoverySettings(plans, BackendHostEnvironment.Compile(false, false),
            new NoRecoveryControl(), DumpSettings(), owner, NullLogger<ServiceRecoverySettings>.Instance);
        await service.StartAsync(default);
        Assert.Equal(0, owner.Discoveries);
        plans.Publish(Plan());
        plans.Publish(Plan(version: 2));
        await service.StopAsync(default);
        Assert.Equal(1, owner.Discoveries);
    }

    private sealed class NoRecoveryControl : IServiceRecoveryControl
    {
        public string? GetSkipReason() => "test";
        public void Write(ServiceRecoveryConfiguration configuration) => throw new InvalidOperationException();
        public ServiceRecoveryConfiguration Read() => throw new InvalidOperationException();
    }
}
