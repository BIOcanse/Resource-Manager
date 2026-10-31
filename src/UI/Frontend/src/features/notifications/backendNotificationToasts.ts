import { createEffect, onCleanup, type Accessor } from "solid-js";
import type { CurrentValueSource } from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { bindPushSource } from "../../frontendRuntime/push/usePushSource.ts";
import type { AppNotificationSettings } from "../../types.ts";
import {
  knownBackendNotification, type BackendNotificationList, type KnownBackendNotification
} from "../../data/notifications/backendNotifications.ts";

/**
 * 一次订阅里处理到哪条通知（docs/notifications_2026-10-29.md“前端”）：
 * - 第一帧只记下当前最新序号，不补显示旧通知；
 * - 之后每条序号更大的通知按种类交给 show，不认识的种类或事实写错的只跳过；
 * - 列表最新序号比记下的小，说明通知文件重建过：这一帧也只记下，不补显示。
 * 记录只在内存里：界面关着时什么也不做，重新订阅时又从第一帧开始。
 */
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

/** 每类通知在设置里的“是否弹”和“软件内提示”两项（docs/notifications_2026-10-29.md“设置在前端”）。 */
const inAppSwitches: Record<KnownBackendNotification["kind"], readonly [keyof AppNotificationSettings, keyof AppNotificationSettings]> = {
  "memory-protection.action": ["memoryProtectionNotify", "memoryProtectionInApp"],
  "update-available": ["updateNotify", "updateInApp"],
  "access-request": ["accessRequestNotify", "accessRequestInApp"],
  "crash-report": ["crashReportNotify", "crashReportInApp"]
};

/** 这一类的两项都开着时界面里提示；偏好缓存还没读到时不提示。 */
export function showsInApp(kind: KnownBackendNotification["kind"], settings: AppNotificationSettings | undefined): boolean {
  return settings !== undefined && inAppSwitches[kind].every(path => settings[path] === "on");
}

/**
 * 界面打开时（active）订阅通知流。订阅时同步交回的是上次订阅缓存的值，不算这次订阅的第一帧；
 * 后端在订阅时重放一次当前列表，那一帧才是。传输、解码或会话失败后等重新订阅的重放，同样从第一帧开始。
 */
export function bindBackendNotificationToasts(options: {
  readonly source: CurrentValueSource<BackendNotificationList>;
  readonly active: Accessor<boolean>;
  /** 已保存的通知设置（不是设置页草稿），在决定的那一刻读取。 */
  readonly settings: Accessor<AppNotificationSettings | undefined>;
  readonly show: (notification: KnownBackendNotification) => void;
}): void {
  // 关着的种类也照样前移记录：之后打开开关，不补显示关着期间的通知。
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
