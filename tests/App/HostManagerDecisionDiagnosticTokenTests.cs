using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDecisionDiagnosticTokenTests
{
    [Fact]
    public void HostManagerSchedulingAuthorityAvailabilityHasExplicitUniqueTokens()
        => AssertTokens<HostManagerSchedulingAuthorityAvailability>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void HostManagerCpuOccupancySourceHasExplicitUniqueTokens()
        => AssertTokens<HostManagerCpuOccupancySource>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeComputeScoringOutputKindHasExplicitUniqueTokens()
        => AssertTokens<NativeComputeScoringOutputKind>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeMemoryModeHasExplicitUniqueTokens()
        => AssertTokens<NativeMemoryMode>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeMemoryGradeSetHasExplicitUniqueTokens()
        => AssertTokens<NativeMemoryGradeSet>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorSnapshotFlagsHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorSnapshotFlags>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorReasonHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorReason>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorSnapshotRowKindHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorSnapshotRowKind>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorProcessGradeHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorProcessGrade>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorSnapshotRowFlagsHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorSnapshotRowFlags>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorInputValidityHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorInputValidity>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void HostManagerNonAdaptedMemoryProcessActionHasExplicitUniqueTokens()
        => AssertTokens<HostManagerNonAdaptedMemoryProcessAction>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorRuntimeStateHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorRuntimeState>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeComputeScoringRuntimeStateHasExplicitUniqueTokens()
        => AssertTokens<NativeComputeScoringRuntimeState>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void NativeSmartCoordinatorSoftwareKindHasExplicitUniqueTokens()
        => AssertTokens<NativeSmartCoordinatorSoftwareKind>(HostManagerDecisionDiagnosticTokens.Token);

    [Fact]
    public void UndefinedValuesPublishNumericTextWithoutGuessingNames()
    {
        Assert.Equal("-128", HostManagerDecisionDiagnosticTokens.Token((NativeSmartCoordinatorProcessGrade)(-128)));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((NativeMemoryMode)255));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((NativeSmartCoordinatorSnapshotRowKind)255));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((NativeComputeScoringOutputKind)255));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((HostManagerSchedulingAuthorityAvailability)255));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((HostManagerCpuOccupancySource)255));
        Assert.Equal("255", HostManagerDecisionDiagnosticTokens.Token((HostManagerNonAdaptedMemoryProcessAction)255));
        Assert.Throws<InvalidDataException>(() => HostManagerDecisionDiagnosticTokens.Token((NativeSmartCoordinatorRuntimeState)255));
        Assert.Throws<InvalidDataException>(() => HostManagerDecisionDiagnosticTokens.Token((NativeComputeScoringRuntimeState)255));
        Assert.Throws<InvalidDataException>(() => HostManagerDecisionDiagnosticTokens.Token((NativeSmartCoordinatorSoftwareKind)255));
    }

    private static void AssertTokens<T>(Func<T, string> token) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var tokens = values.Select(token).ToArray();
        Assert.Equal(values.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        // A numeric fallback must never make a newly defined enum member look covered.
        Assert.All(tokens, value => Assert.Matches("^[a-z][a-z0-9_]*$", value));
    }
}
