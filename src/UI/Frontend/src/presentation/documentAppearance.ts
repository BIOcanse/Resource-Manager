import { setInteractionGlobalOverrides } from "../interactions/solid/lodestone.ts";
import { applyMotionFeel } from "./motionFeel.ts";
import type { MotionFeelDefinition } from "./motionFeel.ts";
import type { UiAppearanceChoice } from "./uiStyles.ts";

// 根元素是外观的唯一发布点：data-ui-style 是界面风格，data-theme 是该风格下的配色，data-motion-feel 是手感。
// 手感的两部分在这里一起换：CSS 动效令牌（按 data-motion-feel）和磁石全局层的覆盖。
export function publishCommittedDocumentAppearance(appearance: UiAppearanceChoice, feel: MotionFeelDefinition) {
  const root = document.documentElement;
  root.dataset.uiStyle = appearance.uiStyle;
  root.dataset.theme = appearance.theme;
  applyMotionFeel(root, feel.id);
  setInteractionGlobalOverrides(feel.lodestone);

  // Remove attributes emitted by builds that predate the single root owner.
  root.removeAttribute("data-preboot-theme");
  document.body.removeAttribute("data-theme");
}

// 画布这类自己取色的组件统一从这里订阅外观变化：风格、配色，以及跟随系统时 Windows 的浅色/深色切换。
// 颜色本身一律从 CSS 变量读，这里只通知"该重画了"。
export function observeDocumentAppearance(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);
  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-ui-style", "data-theme"]
  });
  const systemScheme = window.matchMedia?.("(prefers-color-scheme: dark)");
  systemScheme?.addEventListener("change", onChange);
  return () => {
    observer.disconnect();
    systemScheme?.removeEventListener("change", onChange);
  };
}
