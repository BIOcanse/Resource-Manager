import { createEffect } from "solid-js";
import type { Accessor } from "solid-js";
import type { ProductUpdateState } from "../features/settings/productUpdatePresentation.ts";
import type { FrontendNotifications } from "../preferences/frontendPreferences.ts";

/**
 * 界面内提示一次的规则：发现的版本非空、与记下的已提示版本不同，且界面内提示开着时提示。
 * 关着时不提示也不记下，之后打开开关，当前发现的版本照样提示一次。
 */
export function shouldNoticeUpdate(availableVersion: string | null | undefined,
  notifications: Pick<FrontendNotifications, "updateInApp" | "updateInAppNotifiedVersion">): availableVersion is string {
  return notifications.updateInApp === "on"
    && typeof availableVersion === "string" && availableVersion.length > 0
    && availableVersion !== notifications.updateInAppNotifiedVersion;
}

/**
 * 界面运行期间：状态里出现未提示过的新版本时提示一次并把版本记进偏好缓存。
 * 界面关着时发现的版本，下次打开时推送先发当前状态，照样提示一次。
 */
export function bindProductUpdateNotice(options: {
  readonly status: Accessor<ProductUpdateState | null>;
  /** 偏好缓存里已保存的提示分区；读到之前为 undefined，此时不判断。 */
  readonly notifications: Accessor<FrontendNotifications | undefined>;
  readonly record: (version: string) => Promise<void>;
  readonly show: (state: ProductUpdateState & { availableVersion: string }) => void;
}): void {
  // 记下版本的写入完成前，状态的其他字段变化不能让同一版本再提示一次。
  let shownVersion: string | null = null;
  createEffect(() => {
    const state = options.status();
    const notifications = options.notifications();
    const version = state?.availableVersion;
    if (!state || !notifications || !shouldNoticeUpdate(version, notifications) || version === shownVersion) return;
    shownVersion = version;
    options.show({ ...state, availableVersion: version });
    void options.record(version).catch(() => undefined);
  });
}
