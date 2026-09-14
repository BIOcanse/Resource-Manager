namespace Resource_Manager_APP.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentVariableFactAttribute : FactAttribute
{
    public EnvironmentVariableFactAttribute(
        string variableName,
        string? expectedValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        var actual = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(actual)
            || (expectedValue is not null
                && !string.Equals(actual, expectedValue, StringComparison.Ordinal)))
        {
            Skip = expectedValue is null
                ? $"Set {variableName} to run this external-evidence test."
                : $"Set {variableName}={expectedValue} to run this external-evidence test.";
        }
    }
}
