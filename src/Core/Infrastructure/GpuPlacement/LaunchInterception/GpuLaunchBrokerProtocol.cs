using System.Text;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public static class GpuLaunchBrokerProtocol
{
    public const string ContentType = "application/vnd.resource-manager.gpu-launch.v1+text";

    public static string Serialize(GpuStartupPlacementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["version"] = "1",
            ["decision"] = decision.Decision,
            ["startupProviders"] = string.Join(',', decision.StartupProviders),
            ["status"] = decision.Status,
            ["message"] = decision.Message,
            ["executablePath"] = decision.ExecutablePath,
            ["softwareId"] = decision.SoftwareId,
            ["processKey"] = decision.ProcessKey,
            ["startupTargetGpu"] = decision.StartupTargetGpu,
            ["assignedPositionId"] = decision.AssignedPositionId,
            ["policyPath"] = decision.PolicyPath,
            ["targetAdapterName"] = decision.TargetAdapterName,
            ["targetLuid"] = decision.TargetLuid
        };

        var output = new StringBuilder(512);
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            output.Append(key)
                .Append('=')
                .Append(Sanitize(value))
                .Append('\n');
        }
        return output.ToString();
    }

    public static bool TryParseExecutionReport(
        string? protocol,
        out GpuLaunchExecutionReport? report,
        out string error)
    {
        report = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(protocol) || protocol.Length > 64 * 1024)
        {
            error = "invalid report body";
            return false;
        }

        var fields = protocol
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Split('=', 2))
            .Where(static parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(static parts => parts[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last()[1].Trim(),
                StringComparer.Ordinal);
        if (!fields.TryGetValue("executablePath", out var executablePath)
            || string.IsNullOrWhiteSpace(executablePath)
            || !fields.TryGetValue("outcome", out var outcome)
            || !GpuLaunchExecutionOutcomes.Known.Contains(outcome))
        {
            error = "missing or unsupported report fields";
            return false;
        }

        int? processId = null;
        if (fields.TryGetValue("processId", out var processIdText)
            && int.TryParse(processIdText, out var parsedProcessId)
            && parsedProcessId > 0)
        {
            processId = parsedProcessId;
        }
        fields.TryGetValue("softwareId", out var softwareId);
        fields.TryGetValue("processKey", out var processKey);
        fields.TryGetValue("message", out var message);
        fields.TryGetValue("startupTargetGpu", out var startupTargetGpu);
        fields.TryGetValue("assignedPositionId", out var assignedPositionId);
        fields.TryGetValue("targetAdapterName", out var targetAdapterName);
        report = new GpuLaunchExecutionReport(
            executablePath,
            softwareId,
            processKey,
            processId,
            outcome,
            message ?? string.Empty,
            startupTargetGpu,
            assignedPositionId,
            targetAdapterName,
            DateTimeOffset.UtcNow);
        return true;
    }

    private static string Sanitize(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
