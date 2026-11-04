namespace ResourceManager.App.Domain.Settings;

public static class AppRealtimeActionBudgetModes
{
    public const string Fixed = "fixed";
    public const string Percent = "percent";

    public static bool IsSupported(string? mode) => mode is Fixed or Percent;

    public static string Normalize(string? mode) => IsSupported(mode) ? mode! : Fixed;
}
