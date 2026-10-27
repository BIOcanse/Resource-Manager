using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace Resource_Manager_APP.Tests;

public sealed class RuntimeWmiOwnerLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirmwareVariantQualificationReadsFixedDiscriminatorOnceIncludingUnknown(bool available)
    {
        var queries = 0; var identities = 0; var registers = 0;
        byte? feature = available ? (byte)0x80 : null;
        UniwillGpuPowerHardware Create() => new((_, _) =>
        {
            queries++;
            return new([new() { ["PNPDeviceID"] = @"ACPI\INOU0000\1" }], null);
        }, () =>
        {
            identities++;
            return new FirmwareIdentitySnapshot(null, null, "TUXEDO", null, null, "PHxARX1_PHxAQF1");
        }, address =>
        {
            Assert.Equal((ushort)0x0456, address);
            registers++;
            return feature;
        });
        using var hardware = Create();
        Assert.Equal(available, hardware.IsBudgetFirmwareConfirmed);
        feature = 0x80;
        for (var index = 0; index < 100; index++) Assert.Equal(available, hardware.IsBudgetFirmwareConfirmed);
        Assert.Equal(1, queries); Assert.Equal(1, identities); Assert.Equal(1, registers);
        using var nextOwner = Create();
        Assert.True(nextOwner.IsBudgetFirmwareConfirmed);
        Assert.Equal(2, queries); Assert.Equal(2, identities); Assert.Equal(2, registers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MachineIdentityGuidAndDeclaredFallbackAreResolvedOncePerExistingReaderOwner(bool available)
    {
        var guidReads = 0; var nameReads = 0;
        string? guid = available ? "original-guid" : null;
        var reader = new DeviceTopologyCacheIdentity.Reader(() => { guidReads++; return guid; },
            () => { nameReads++; return "original-machine"; });
        Assert.Equal(0, guidReads);
        var first = reader.Read();
        Assert.Equal(available ? "machine-guid" : "machine-name", first.Source);
        guid = "replacement-guid";
        for (var index = 0; index < 100; index++) Assert.Same(first, reader.Read());
        Assert.Equal(1, guidReads); Assert.Equal(available ? 0 : 1, nameReads);
        var nextOwner = new DeviceTopologyCacheIdentity.Reader(() => { guidReads++; return guid; },
            () => throw new InvalidOperationException("Guid is now present."));
        Assert.NotEqual(first.Sha256, nextOwner.Read().Sha256);
        Assert.Equal(2, guidReads);
    }

    [Fact]
    public void CpuIdentityOwnerResolvesOnceEvenWhenConsumerPassesAnotherName()
    {
        var calls = 0;
        var reader = new WindowsCpuIdentityReader(queryIdentity: name => { calls++; return "model:" + name; });
        Assert.Equal("model:first", reader.Resolve("first"));
        for (var index = 0; index < 100; index++) Assert.Equal("model:first", reader.Resolve("later"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ControlChassisKinds.Unknown)]
    [InlineData(ControlChassisKinds.Portable)]
    public void ControlRedetectionDoesNotRepeatFixedChassisDeclaration(string kind)
    {
        var calls = 0;
        var reader = new WindowsChassisKindReader(() => { calls++; return kind; });
        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(kind, reader.Read());
            reader.ResetDetection();
        }
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirmwareAcpiAbsenceAndFailureAreProbedOncePerHardwareOwner(bool failed)
    {
        var calls = 0;
        using var hardware = new UniwillGpuPowerHardware((_, _) =>
        {
            calls++;
            return new([], failed ? "unavailable" : null);
        });
        for (var index = 0; index < 100; index++)
            Assert.Equal(failed ? (bool?)null : false, hardware.HasAcpiDevice());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OperatingSystemOwnerReportsOnlyActualQueriesAndRetainsFailedFixedFields()
    {
        var calls = 0;
        using var owner = new WindowsOperatingSystemInformationOwner(() => new(TimeSpan.FromSeconds(1), 1),
            new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(1), (_, _, _) =>
            {
                calls++;
                return new([], "unavailable");
            }));
        var reported = new List<WindowsOperatingSystemInformationFields>();
        for (var index = 0; index < 100; index++)
            await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Build
                | WindowsOperatingSystemInformationFields.Uptime, sourceQueriesCompleted: reported.Add);
        Assert.Equal(1, calls);
        Assert.Equal(WindowsOperatingSystemInformationFields.Build | WindowsOperatingSystemInformationFields.Uptime, reported[0]);
        Assert.All(reported.Skip(1), fields => Assert.Equal(WindowsOperatingSystemInformationFields.Uptime, fields));
        var build = new FieldReference(WindowsOperatingSystemInformationOwner.MachineIdentity, "os_all_build_declared");
        Assert.Null(owner.CreateBackend().ReadCached(build).Value);
        using var borrow = owner.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([build]));
        Assert.Equal(FieldCollectionSourceKind.CurrentValue, binding.SourceKind);
    }
}
