using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", (INotificationOwner notifications, HttpContext context) =>
            Results.Ok(VisibleNotifications(notifications.Read(),
                context.Items.TryGetValue(typeof(TrustedAccessAuthorizationContext), out var value)
                    ? value as TrustedAccessAuthorizationContext : null)));
        return app;
    }

    // Each caller receives only the kinds whose registered scope its key, or its keyless local status, holds.
    // A request without an authorization context (or a kind this build does not register) shows nothing.
    internal static NotificationList VisibleNotifications(NotificationList list, TrustedAccessAuthorizationContext? access)
    {
        if (access is null) return new([]);
        var visible = list.Notifications
            .Where(item => NotificationKinds.ScopeOf(item.Kind) is { } scope && access.Holds(scope))
            .ToArray();
        return visible.Length == list.Notifications.Count ? list : new(visible);
    }

    private static CompiledFrontendSubscription CompileNotificationSubscription(
        ParsedFrontendSubscription subscription,
        INotificationOwner notifications)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            IReadOnlyList<NotificationItem>? sent = null;
            await foreach (var list in notifications.SubscribeAsync(cancellationToken))
            {
                var visible = VisibleNotifications(list, subscription.Access);
                // A change among kinds this caller cannot read is no change for this caller.
                if (sent is not null && sent.Select(static item => item.Sequence)
                        .SequenceEqual(visible.Notifications.Select(static item => item.Sequence)))
                    continue;
                sent = visible.Notifications;
                await writer.WriteAsync(subscription.Id, visible, cancellationToken);
            }
        });
}
