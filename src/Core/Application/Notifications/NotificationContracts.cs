using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Application.Notifications;

// One delivered notification. It carries only the kind and that kind's facts; consumers choose their own text.
// Sequence orders and identifies notifications; OccurredAt is UTC and only for display.
public sealed record NotificationItem(long Sequence, string Kind, DateTimeOffset OccurredAt, JsonElement Facts);

// The owner's whole retained list, oldest first. GET and every subscription frame carry this shape.
public sealed record NotificationList(IReadOnlyList<NotificationItem> Notifications);

public interface INotificationOwner
{
    NotificationList Read();

    // The current list first, then the whole list after every change.
    IAsyncEnumerable<NotificationList> SubscribeAsync(CancellationToken cancellationToken);

    NotificationItem Post(string kind, JsonElement facts);
}

// The kind table of docs/notifications_2026-10-29.md. Each kind needs the scope of the endpoint its facts came from,
// so moving a fact into the notification stream never widens who can read it.
public sealed record NotificationKind(string Name, string Scope);

public static class NotificationKinds
{
    public static NotificationKind MemoryProtectionAction { get; } = new("memory-protection.action", AccessKeyScopes.ProcessesRead);
    public static NotificationKind CpuMonopolyProtectionAction { get; } = new("cpu-monopoly-protection.action", AccessKeyScopes.ProcessesRead);
    public static NotificationKind UpdateAvailable { get; } = new("update-available", AccessKeyScopes.Read);
    public static NotificationKind AccessRequest { get; } = new("access-request", AccessKeyScopes.KeysManage);
    public static NotificationKind CrashReport { get; } = new("crash-report", AccessKeyScopes.Read);
    public static NotificationKind DeviceRestartAction { get; } = new("device-restart.action", AccessKeyScopes.ProcessesRead);

    public static IReadOnlyList<NotificationKind> All { get; } = [MemoryProtectionAction, CpuMonopolyProtectionAction, UpdateAvailable, AccessRequest, CrashReport, DeviceRestartAction];

}

public sealed record MemoryProtectionActionNotificationFacts(
    string ActionId,
    string TargetKind,
    string? Name,
    int ProcessId,
    int RecordCount,
    ulong BytesWritten);

public sealed record UpdateAvailableNotificationFacts(string Version);
public sealed record CpuMonopolyProtectionActionNotificationFacts(string ActionId, string Name, int ProcessId,
    string Outcome, string? Reason);

public sealed record AccessRequestNotificationFacts(string RequestId, string Name, string ObservedExecutablePath);

// Posted once by the service's startup discovery when it registered new crash records; program and role are the newest one's.
public sealed record CrashReportNotificationFacts(int Count, string LatestReportId, string Program, string Role);

public sealed record DeviceRestartNotificationFacts(long Sequence, string ReportEntryId, string DeviceInstanceId, uint ResultCode, uint Detail);

// Producers turn their typed facts into the facts object of one notification.
public static class NotificationFacts
{
    public static JsonElement Of(DeviceRestartNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.DeviceRestartNotificationFacts);
    public static JsonElement Of(MemoryProtectionActionNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.MemoryProtectionActionNotificationFacts);

    public static JsonElement Of(UpdateAvailableNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.UpdateAvailableNotificationFacts);

    public static JsonElement Of(CpuMonopolyProtectionActionNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.CpuMonopolyProtectionActionNotificationFacts);

    public static JsonElement Of(AccessRequestNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.AccessRequestNotificationFacts);

    public static JsonElement Of(CrashReportNotificationFacts facts)
        => JsonSerializer.SerializeToElement(facts, NotificationFactsJsonContext.Default.CrashReportNotificationFacts);

    public static UpdateAvailableNotificationFacts? ReadUpdateAvailable(JsonElement facts)
    {
        try { return facts.Deserialize(NotificationFactsJsonContext.Default.UpdateAvailableNotificationFacts); }
        catch (JsonException) { return null; }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(MemoryProtectionActionNotificationFacts))]
[JsonSerializable(typeof(UpdateAvailableNotificationFacts))]
[JsonSerializable(typeof(CpuMonopolyProtectionActionNotificationFacts))]
[JsonSerializable(typeof(AccessRequestNotificationFacts))]
[JsonSerializable(typeof(CrashReportNotificationFacts))]
[JsonSerializable(typeof(DeviceRestartNotificationFacts))]
internal sealed partial class NotificationFactsJsonContext : JsonSerializerContext;
