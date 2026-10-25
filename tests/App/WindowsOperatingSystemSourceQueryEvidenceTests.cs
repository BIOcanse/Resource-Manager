using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsOperatingSystemSourceQueryEvidenceTests
{
    private const string FeatureIdentity = "windows-os-component|feature:Feature.One";

    [Fact]
    public async Task BorrowReportsOnlyReaderCategoriesAndRetainsEvidenceWhenPermitRejectsPublication()
    {
        var reader = new RecordedComponentReader(_ => new([new("Feature.One", 0)], null, null));
        using var owner = new WindowsOperatingSystemInformationOwner(
            new WindowsOperatingSystemComponentInformationScope(WindowsOperatingSystemInformationFields.Features),
            reader, TimeSpan.FromSeconds(1));

        var captures = new List<WindowsOperatingSystemInformationFields>();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features,
            sourceQueriesCompleted: captures.Add);
        Assert.Equal(new[] { WindowsOperatingSystemInformationFields.Features }, captures);

        var field = new FieldReference(FeatureIdentity, DeviceInformationFieldRegistry.OsAllFeatureStateActual.Name);
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([field]);
        var borrowed = await borrow.CollectAsync([field], new Permit(accept: false));

        Assert.False(borrowed.Committed);
        Assert.Equal(new[] { field }, borrowed.QueriedFields);
        Assert.Equal(new[]
        {
            WindowsOperatingSystemInformationFields.Features,
            WindowsOperatingSystemInformationFields.Features
        }, reader.Calls);
        Assert.Equal(MetricInteger.Parse("0"), Assert.Single(borrowed.Readings).Value!.Integer);
    }

    [Fact]
    public async Task ScopeExcludedAndEmptySelectionsReportNoSourceQuery()
    {
        var reader = new RecordedComponentReader(_ => new([new("Feature.One", 0)], null, null));
        using var owner = new WindowsOperatingSystemInformationOwner(
            new WindowsOperatingSystemComponentInformationScope(WindowsOperatingSystemInformationFields.Features),
            reader, TimeSpan.FromSeconds(1));

        var reports = new List<WindowsOperatingSystemInformationFields>();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.ComponentPackages,
            sourceQueriesCompleted: reports.Add);
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.None,
            sourceQueriesCompleted: reports.Add);

        Assert.Equal(new[] { WindowsOperatingSystemInformationFields.None, WindowsOperatingSystemInformationFields.None }, reports);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task FailedReaderCallStillReportsItsAttemptedCategoryAndDoesNotCommit()
    {
        var reader = new RecordedComponentReader(_ => throw new InvalidOperationException("Recorded DISM read failure."));
        using var owner = new WindowsOperatingSystemInformationOwner(
            new WindowsOperatingSystemComponentInformationScope(WindowsOperatingSystemInformationFields.Features),
            reader, TimeSpan.FromSeconds(1));
        var previous = owner.ReadInformation();
        var reported = WindowsOperatingSystemInformationFields.None;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Features,
                sourceQueriesCompleted: mask => reported = mask));

        Assert.Equal(WindowsOperatingSystemInformationFields.Features, reported);
        Assert.Equal(WindowsOperatingSystemInformationFields.Features, Assert.Single(reader.Calls));
        Assert.Same(previous, owner.ReadInformation());
    }

    [Fact]
    public void RecordedPlatformReaderReportsOnlySelectedPlatformCategories()
    {
        var calls = new List<WindowsOperatingSystemInformationFields>();
        var reader = new WindowsPlatformDeclarationReader((selected, _) =>
        {
            calls.Add(selected);
            return new(null, null, null, null);
        });
        var queryEvidence = WindowsOperatingSystemInformationFields.None;
        var selected = WindowsOperatingSystemInformationFields.Uptime
            | WindowsOperatingSystemInformationFields.Activation
            | WindowsOperatingSystemInformationFields.Packages
            | WindowsOperatingSystemInformationFields.Features;

        _ = reader.Read(selected, sourceQueryAttempted: mask => queryEvidence |= mask);

        Assert.Equal(selected, Assert.Single(calls));
        Assert.Equal(WindowsOperatingSystemInformationFields.Activation
            | WindowsOperatingSystemInformationFields.Packages, queryEvidence);
    }

    [Fact]
    public void DisabledPlatformScopesDoNotReportRequestedCategoriesAsQueried()
    {
        var reader = new WindowsPlatformDeclarationReader(new WindowsPlatformDeclarationScope
        {
            IncludeMachineConfiguration = false,
            IncludeUserConfiguration = false,
            IncludeTaskSchedulerConfiguration = false
        });
        var queryEvidence = WindowsOperatingSystemInformationFields.None;

        _ = reader.Read(WindowsOperatingSystemInformationFields.Environment
            | WindowsOperatingSystemInformationFields.Packages,
            sourceQueryAttempted: mask => queryEvidence |= mask);

        Assert.Equal(WindowsOperatingSystemInformationFields.None, queryEvidence);
    }

    private sealed class RecordedComponentReader(
        Func<WindowsOperatingSystemInformationFields, WindowsOperatingSystemComponentInformationReadResult> read)
        : IWindowsOperatingSystemComponentInformationReader
    {
        private readonly List<WindowsOperatingSystemInformationFields> calls = [];
        internal IReadOnlyList<WindowsOperatingSystemInformationFields> Calls => calls.ToArray();

        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add(selected);
            return read(selected);
        }
    }

    private sealed class Permit(bool accept) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => accept;
        public bool TryPublish(Action commit)
        {
            if (!accept) return false;
            commit();
            return true;
        }
    }
}
