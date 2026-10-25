using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class OperatingSystemSessionOwnerTests
{
    private static WindowsLoginSession Row(uint id, long birth, WindowsSessionConnectionState state = WindowsSessionConnectionState.Active)
        => new(id, birth, "reported-station", "reported-domain", "reported-user", state);
    private static WindowsOperatingSystemInformationOwner Owner(Func<WindowsOperatingSystemInformationFields, CancellationToken, WindowsPlatformDeclarationFacts> read, TimeProvider? clock = null)
        => new(() => new(TimeSpan.FromSeconds(7), 7), new(TimeSpan.FromSeconds(2), static (_, _, _) => new([], "Recorded no WMI")), new(read), clock);

    [Fact]
    public void DisappearingAndInvalidSessionsOnlyRemoveTheirOwnRows()
    {
        var queried = new List<uint>();
        var result = WindowsSessionInformationReader.ReadRows([1, 2, 3, 4, 5, 5], id =>
        {
            queried.Add(id);
            return id switch
            {
                2 => null,
                3 => WindowsSessionInformationReader.Decode(3, 99, 1, "", "", "user", WindowsSessionConnectionState.Active),
                _ => Row(id, 134116992000000000L + id)
            };
        });
        Assert.Equal(new uint[] { 1, 4 }, result.Select(static row => row.SessionId));
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, queried);
        Assert.Equal(134116992000000004L, result[1].LogonFileTime);
    }

    [Fact]
    public void NativeProductionDecoderDistinguishesLoginsFromSlotsAndRetainsExactReportedIds()
    {
        // Independent Windows SDK WTSINFOEXW / LEVEL1_W ABI, not a live WTS call.
        Assert.Equal((232, 8, 160), WindowsSessionInformationReader.NativeLayout);
        var birth = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.ToFileTimeUtc();
        var zero = WindowsSessionInformationReader.Decode(0, 0, birth, "Console", "DOMAIN", "actual-user", WindowsSessionConnectionState.Active);
        Assert.NotNull(zero); Assert.Equal(0u, zero.SessionId); Assert.Equal(birth, zero.LogonFileTime);
        var maximum = WindowsSessionInformationReader.Decode(uint.MaxValue, uint.MaxValue, birth, "RDP-Tcp#7", "DOMAIN", "other-user", WindowsSessionConnectionState.Disconnected);
        Assert.Equal(uint.MaxValue, maximum!.SessionId); Assert.Equal(WindowsSessionConnectionState.Disconnected, maximum.ConnectionState);
        Assert.Null(WindowsSessionInformationReader.Decode(9, 9, 0, "RDP-Tcp", "", "", WindowsSessionConnectionState.Listen));
        Assert.Throws<InvalidDataException>(() => WindowsSessionInformationReader.Decode(9, 10, birth, "", "", "user", WindowsSessionConnectionState.Active));
        Assert.Throws<InvalidDataException>(() => WindowsSessionInformationReader.Decode(9, 9, 0, "", "", "user", WindowsSessionConnectionState.Active));
    }

    [Fact]
    public async Task AllReportedLoginsAndReusedOrdinalHaveSeparateBirthBoundIdentities()
    {
        var birth = 134116992000000000L; var replaced = false; var reads = 0;
        using var owner = Owner((selected, _) =>
        {
            reads++;
            return new(null, null, null, null) { Sessions = [Row(0, birth), Row(42, replaced ? birth + 1 : birth, WindowsSessionConnectionState.Disconnected)] };
        });
        var backend = owner.CreateBackend(); Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        var original = await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.SessionId);
        var sessions = backend.Catalog.Fields.Where(field => field.Field.FieldName == "os_all_session_id_actual").ToArray();
        Assert.Equal(2, sessions.Length); Assert.Equal(1, reads);
        Assert.All(sessions, field => { Assert.Equal("unsigned64", field.Definition.ValueKind); Assert.Equal("session-id", field.Definition.Unit); });
        Assert.Contains(sessions, field => backend.ReadCached(field.Field).Value!.Integer == MetricInteger.Parse("0"));
        var prior = sessions.Single(field => backend.ReadCached(field.Field).Value!.Integer == MetricInteger.Parse("42")).Field;
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([prior]);
        replaced = true;
        var result = await borrow.CollectAsync([prior], new Permit());
        Assert.True(result.Committed); Assert.Equal(FieldObservationStatus.Unavailable, result.Readings.Single().Status);
        var replacement = backend.Catalog.Fields.Single(field => field.Field.FieldName == "os_all_session_id_actual"
            && backend.ReadCached(field.Field).Value!.Integer == MetricInteger.Parse("42")).Field;
        Assert.NotEqual(prior.DeviceIdentity, replacement.DeviceIdentity); Assert.Null(owner.ReadInformation().FindField(prior));
        Assert.NotSame(original, owner.ReadInformation());
    }

    [Fact]
    public async Task SessionOnlyFailureEmptyRetirementAndUnselectedTimesUseOriginalCurrent()
    {
        var requested = new List<WindowsOperatingSystemInformationFields>(); var failure = false; var empty = false;
        var clock = new Clock();
        using var owner = Owner((selected, _) =>
        {
            requested.Add(selected);
            return new(null, [new("HKLM", Microsoft.Win32.RegistryView.Registry64, null, "Environment", "Recorded", Microsoft.Win32.RegistryValueKind.String, "exact")], null, null)
                { Sessions = failure ? null : empty ? [] : [Row(42, 134116992000000000L)] };
        }, clock);
        var backend = owner.CreateBackend(); var catalog = await backend.DiscoverAsync(default);
        var session = catalog.Fields.Single(field => field.Field.FieldName == "os_all_session_id_actual").Field;
        var environment = catalog.Fields.Single(field => field.Field.FieldName == "environment_all_variable_configured").Field;
        var previousEnvironment = backend.ReadCached(environment);
        failure = true; clock.Now = clock.Now.AddHours(1);
        var unavailable = (await backend.CollectAsync([session], default)).Single();
        Assert.Equal(WindowsOperatingSystemInformationFields.SessionId, requested.Last());
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status); Assert.Null(unavailable.Value); Assert.Null(unavailable.ObservedAt);
        Assert.Equal(previousEnvironment, backend.ReadCached(environment));
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([session]);
        failure = false; empty = true;
        Assert.Equal(FieldObservationStatus.Unavailable, (await borrow.CollectAsync([session], new Permit())).Readings.Single().Status);
        Assert.DoesNotContain(backend.Catalog.Fields, field => field.Field.FieldName == "os_all_session_id_actual");
        Assert.Equal(previousEnvironment, backend.ReadCached(environment));
    }

    [Fact]
    public async Task SessionSelectedCancellationAndBorrowRejectionDoNotPublishNewCurrent()
    {
        Action? duringRead = null;
        using var owner = Owner((_, _) => { duringRead?.Invoke(); return new(null, null, null, null) { Sessions = [Row(3, 134116992000000000L)] }; });
        var backend = owner.CreateBackend(); var session = (await backend.DiscoverAsync(default)).Fields.Single(field => field.Field.FieldName == "os_all_session_id_actual").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); _ = borrow.Bind([session]);
        var previous = owner.ReadInformation(); var active = true; duringRead = () => active = false;
        Assert.False((await borrow.CollectAsync([session], new Permit(() => active))).Committed); Assert.Same(previous, owner.ReadInformation());
        using var cancellation = new CancellationTokenSource(); duringRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CollectAsync([session], cancellation.Token));
        Assert.Same(previous, owner.ReadInformation());
    }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Permit(Func<bool>? active = null) : IMetricPublicationPermit
    { public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active?.Invoke() ?? true; public bool TryPublish(Action commit) { if (!IsActive) return false; commit(); return true; } }
}
