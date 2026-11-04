using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class FirmwareProviderPlanCompiler(FirmwareIdentityReader firmwareIdentityReader)
{
    private static readonly string[] MechrevoUwAcpiFirmwareTokens =
    [
        "MECHREVO",
        "机械革命",
        "TONGFANG",
        "UNIWill",
        "UNIWILL"
    ];

    public CompiledFirmwareProviderPlan Compile()
    {
        var identity = firmwareIdentityReader.Read();
        return Compile(identity);
    }

    public static CompiledFirmwareProviderPlan Compile(FirmwareIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var notebookOemFanProvider = MatchesAny(identity.SearchText, MechrevoUwAcpiFirmwareTokens)
            ? NotebookOemFanProviderKind.MechrevoUwAcpi
            : NotebookOemFanProviderKind.None;

        return new CompiledFirmwareProviderPlan(identity, notebookOemFanProvider);
    }

    private static bool MatchesAny(string text, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
