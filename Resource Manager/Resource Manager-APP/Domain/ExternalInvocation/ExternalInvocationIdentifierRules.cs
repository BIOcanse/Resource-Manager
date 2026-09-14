namespace ResourceManager.App.Domain.ExternalInvocation;

public static class ExternalInvocationIdentifierRules
{
    public static bool IsValidModuleId(string? value)
        => value is { Length: > 0 and <= 64 }
            && IsValidSegment(value);

    public static bool IsValidOperationId(string? value)
    {
        if (value is not { Length: > 0 and <= 192 })
        {
            return false;
        }

        var segments = value.Split('.');
        return segments.Length >= 3
            && segments.All(IsValidSegment);
    }

    public static bool IsValidContractInterfaceName(
        string? value,
        ExternalInvocationOperationCategory category)
    {
        if (value is not { Length: > 2 and <= 128 }
            || value[0] != 'I'
            || value[1] is < 'A' or > 'Z'
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        var requiredSuffix = category switch
        {
            ExternalInvocationOperationCategory.Query => "Queries",
            ExternalInvocationOperationCategory.Command => "Commands",
            ExternalInvocationOperationCategory.Analysis => "Analyzer",
            ExternalInvocationOperationCategory.Planning => "Planner",
            ExternalInvocationOperationCategory.Task => "TaskCommands",
            ExternalInvocationOperationCategory.Coordination => "Coordinator",
            ExternalInvocationOperationCategory.StreamingProxy => "Proxy",
            ExternalInvocationOperationCategory.PlatformAction => "Gateway",
            _ => null
        };
        return requiredSuffix is not null
            && value.EndsWith(requiredSuffix, StringComparison.Ordinal);
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0
            || segment[0] is < 'a' or > 'z'
            || segment[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in segment)
        {
            var validWordCharacter = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9';
            if (validWordCharacter)
            {
                previousWasHyphen = false;
                continue;
            }

            if (character != '-' || previousWasHyphen)
            {
                return false;
            }

            previousWasHyphen = true;
        }

        return true;
    }
}
