import { For } from "solid-js";
import type { AppNotificationSettings } from "../../../types";
import type { NotificationKindKey, SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

type NotificationPath = keyof AppNotificationSettings;

/** 设置页列出的通知种类，次序照 docs/notifications_2026-10-29.md“设置在前端”。 */
const notificationKinds: ReadonlyArray<{ kind: NotificationKindKey; notify: NotificationPath; system: NotificationPath; inApp: NotificationPath }> = [
  { kind: "memoryProtection", notify: "memoryProtectionNotify", system: "memoryProtectionSystem", inApp: "memoryProtectionInApp" },
  { kind: "update", notify: "updateNotify", system: "updateSystem", inApp: "updateInApp" },
  { kind: "accessRequest", notify: "accessRequestNotify", system: "accessRequestSystem", inApp: "accessRequestInApp" }
];

/**
 * 设置页“通知”：每类一组三项。“弹出通知”是这一类的总开关，关掉时两条途径不弹；两条途径的开关变成不可改，
 * 显示的仍是各自保存的选择，总开关再打开时照原样生效。开关只改设置草稿，随设置页的保存写进前端偏好缓存。
 */
export function NotificationsSettingsSection(props: {
  notifications: AppNotificationSettings | undefined;
  text: SettingsTextBundle;
  onSwitchChange: (path: NotificationPath, enabled: boolean) => void;
}) {
  const text = () => props.text.notifications;
  const on = (path: NotificationPath) => (props.notifications?.[path] ?? "on") === "on";
  return <div class="settings-section-panel">
    <p>{text().intro}</p>
    <For each={notificationKinds}>{entry => {
      const kind = () => text().kinds[entry.kind];
      const row = (path: NotificationPath, title: () => string, description: () => string, disabled: () => boolean) =>
        <div class="settings-row">
          <div class="settings-row-copy">
            <strong>{title()}</strong>
            <span>{description()}</span>
          </div>
          <SwitchControl ariaLabel={text().switchLabel(kind().title, title())} checked={on(path)} disabled={disabled()}
            onChange={(checked) => props.onSwitchChange(path, checked)} />
        </div>;
      const channelOff = () => !on(entry.notify);
      return <section class="notification-kind-settings" aria-label={kind().title}>
        <h3>{kind().title}</h3>
        <p>{kind().description}</p>
        {row(entry.notify, () => text().notifyTitle, () => text().notifyDescription, () => false)}
        {row(entry.system, () => text().systemTitle, () => text().systemDescription, channelOff)}
        {row(entry.inApp, () => text().inAppTitle, () => text().inAppDescription, channelOff)}
      </section>;
    }}</For>
  </div>;
}
