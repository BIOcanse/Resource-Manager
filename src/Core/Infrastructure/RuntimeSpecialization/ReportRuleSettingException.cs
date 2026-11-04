namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

// A user report-rule value the plan compiler rejects. The settings save returns
// FailureCode so the settings page can say which rule, which field and why.
public sealed class ReportRuleSettingException(string rule, string field, string reason)
    : Exception($"Report rule '{rule}' field '{field}' is invalid: {reason}.")
{
    public string Rule { get; } = rule;

    public string Field { get; } = field;

    public string Reason { get; } = reason;

    public string FailureCode => $"report-rule-invalid:{Rule}:{Field}:{Reason}";
}
