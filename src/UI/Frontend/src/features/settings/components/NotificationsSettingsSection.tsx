import { For } from "solid-js";
import type { AppNotificationSettings } from "../../../types";
import type { NotificationKindKey, SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

type NotificationPath = keyof AppNotificationSettings;

/** Keep notification-kind order stable. Kinds without system preferences support in-app notices only. */
const notificationKinds: ReadonlyArray<{ kind: NotificationKindKey; notify: NotificationPath; system?: NotificationPath; inApp: NotificationPath }> = [
  { kind: "memoryProtection", notify: "memoryProtectionNotify", system: "memoryProtectionSystem", inApp: "memoryProtectionInApp" },
  { kind: "cpuMonopolyProtection", notify: "cpuMonopolyProtectionNotify", inApp: "cpuMonopolyProtectionInApp" },
  { kind: "update", notify: "updateNotify", system: "updateSystem", inApp: "updateInApp" },
  { kind: "accessRequest", notify: "accessRequestNotify", system: "accessRequestSystem", inApp: "accessRequestInApp" },
  { kind: "crashReport", notify: "crashReportNotify", system: "crashReportSystem", inApp: "crashReportInApp" }
];

/** The master switch gates both routes while retaining their saved choices. Persist changes through the shared draft. */
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
            <div class="settings-row-title">
              <strong>{title()}</strong>
              <HelpHint label={text().switchLabel(kind().title, title())}>{description()}</HelpHint>
            </div>
          </div>
          <SwitchControl ariaLabel={text().switchLabel(kind().title, title())} checked={on(path)} disabled={disabled()}
            onChange={(checked) => props.onSwitchChange(path, checked)} />
        </div>;
      const channelOff = () => !on(entry.notify);
      return <section class="notification-kind-settings" aria-label={kind().title}>
        <h3>{kind().title}</h3>
        <p>{kind().description}</p>
        {row(entry.notify, () => text().notifyTitle, () => text().notifyDescription, () => false)}
        {entry.system ? row(entry.system, () => text().systemTitle, () => text().systemDescription, channelOff) : null}
        {row(entry.inApp, () => text().inAppTitle, () => text().inAppDescription, channelOff)}
      </section>;
    }}</For>
  </div>;
}
