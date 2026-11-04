import { createEffect, onCleanup, type Accessor } from "solid-js";
import type { CurrentValueSource } from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { bindPushSource } from "../../frontendRuntime/push/usePushSource.ts";
import type { AppNotificationSettings } from "../../types.ts";
import {
  knownBackendNotification, type BackendNotificationList, type KnownBackendNotification
} from "../../data/notifications/backendNotifications.ts";

/** Baseline the first frame without replaying old notifications. Advance by sequence thereafter; a decreased maximum resets the baseline. Keep progress only for the current subscription. */
export function createBackendNotificationFollower(show: (notification: KnownBackendNotification) => void) {
  let handled: number | null = null;
  return {
    accept(list: BackendNotificationList): void {
      const latest = list.notifications.length === 0 ? 0 : list.notifications[list.notifications.length - 1]!.sequence;
      if (handled === null || latest < handled) {
        handled = latest;
        return;
      }
      for (const notification of list.notifications) {
        if (notification.sequence <= handled) continue;
        handled = notification.sequence;
        const known = knownBackendNotification(notification);
        if (known) show(known);
      }
    },
    reset(): void { handled = null; }
  };
}

/** Each notification kind has enable and in-app preferences. */
const inAppSwitches: Record<KnownBackendNotification["kind"], readonly [keyof AppNotificationSettings, keyof AppNotificationSettings]> = {
  "memory-protection.action": ["memoryProtectionNotify", "memoryProtectionInApp"],
  "update-available": ["updateNotify", "updateInApp"],
  "access-request": ["accessRequestNotify", "accessRequestInApp"],
  "crash-report": ["crashReportNotify", "crashReportInApp"],
  "cpu-monopoly-protection.action": ["cpuMonopolyProtectionNotify", "cpuMonopolyProtectionInApp"]
};

/** Show notices only when both saved preferences are enabled; wait for preference loading. */
export function showsInApp(kind: KnownBackendNotification["kind"], settings: AppNotificationSettings | undefined): boolean {
  return settings !== undefined && inAppSwitches[kind].every(path => settings[path] === "on");
}

/** Only backend replay establishes the first frame; ignore synchronously cached values and reset after subscription failures. */
export function bindBackendNotificationToasts(options: {
  readonly source: CurrentValueSource<BackendNotificationList>;
  readonly active: Accessor<boolean>;
  /** Read saved notification preferences at decision time. */
  readonly settings: Accessor<AppNotificationSettings | undefined>;
  readonly show: (notification: KnownBackendNotification) => void;
}): void {
  // Advance past disabled kinds so enabling them does not replay old notices.
  const follower = createBackendNotificationFollower(notification => {
    if (showsInApp(notification.kind, options.settings())) options.show(notification);
  });
  createEffect(() => {
    if (!options.active()) return;
    follower.reset();
    let subscribed = false;
    const unsubscribe = bindPushSource(options.source, { active: true }, (snapshot) => {
      if (!subscribed) return;
      if (snapshot.status === "ready" && snapshot.data) follower.accept(snapshot.data);
      else if (snapshot.status !== "loading" && snapshot.status !== "refreshing") follower.reset();
    });
    subscribed = true;
    onCleanup(() => {
      unsubscribe();
      follower.reset();
    });
  });
}
