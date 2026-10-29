using System.Text.Json.Serialization;

namespace ResourceManager.App.Application.Notifications;

// Immutable compiled backend freedom point notifications.retained_count. Bounds reject oversized owner lists.
public sealed record NotificationOptions(
    [property: JsonPropertyName("retained_count")] int RetainedCount)
{
    public const int MaximumRetainedCount = 1024;

    public NotificationOptions Validate()
    {
        if (RetainedCount is < 1 or > MaximumRetainedCount)
            throw new InvalidDataException("Invalid notification retained count.");
        return this;
    }
}
