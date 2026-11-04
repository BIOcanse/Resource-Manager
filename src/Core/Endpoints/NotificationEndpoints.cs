using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Notifications;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    // Each caller receives only the kinds whose registered scope its key, or its keyless local status, holds.
    // A request without an authorization context (or a kind this build does not register) shows nothing.
    internal static NotificationList VisibleNotifications(NotificationList list, TrustedAccessAuthorizationContext? access)
        => NativeNotificationProjection.Visible(list, access);

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
                if (sent is not null && NativeNotificationProjection.SameSequences(sent, visible.Notifications))
                    continue;
                sent = visible.Notifications;
                await writer.WriteAsync(subscription.Id, visible, cancellationToken);
            }
        });
}
