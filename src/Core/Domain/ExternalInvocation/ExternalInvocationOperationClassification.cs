using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.ExternalInvocation;

[JsonConverter(typeof(JsonStringEnumConverter<ExternalInvocationOperationCategory>))]
public enum ExternalInvocationOperationCategory : byte
{
    Query = 0,
    Command = 1,
    Analysis = 2,
    Planning = 3,
    Task = 4,
    Coordination = 5,
    StreamingProxy = 6,
    PlatformAction = 7
}

[JsonConverter(typeof(JsonStringEnumConverter<ExternalInvocationRiskLevel>))]
public enum ExternalInvocationRiskLevel : byte
{
    Low = 0,
    Medium = 1,
    High = 2
}

public static class ExternalInvocationOperationClassificationRules
{
    public static bool IsRiskValid(
        ExternalInvocationOperationCategory category,
        ExternalInvocationRiskLevel riskLevel)
    {
        if (!Enum.IsDefined(category) || !Enum.IsDefined(riskLevel))
        {
            return false;
        }

        if (category == ExternalInvocationOperationCategory.PlatformAction)
        {
            return riskLevel == ExternalInvocationRiskLevel.High;
        }

        return riskLevel != ExternalInvocationRiskLevel.Low
            || category is ExternalInvocationOperationCategory.Query
                or ExternalInvocationOperationCategory.Analysis
                or ExternalInvocationOperationCategory.Planning;
    }
}
