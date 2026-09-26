namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerProcessGrades
{
    public const int Level4 = -4;
    public const int Level3 = -3;
    public const int Level2 = -2;
    public const int Level1 = -1;
    public const int Normal = 0;
    public const int A1 = 1;
}

public sealed record HostManagerLegacyPendingChange(
    string TargetId,
    int FromGrade,
    int ToGrade,
    int ConsecutiveDecisionCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
