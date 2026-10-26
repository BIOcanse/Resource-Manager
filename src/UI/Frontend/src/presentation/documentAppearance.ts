import { setInteractionAnimationMode, setInteractionGlobalOverrides } from "../interactions/solid/lodestone.ts";
import type { AnimationStyleDefinition } from "./animationStyle.ts";
import type { UiAppearanceChoice } from "./uiStyles.ts";

// 根元素是外观的唯一发布点：data-ui-style 是界面风格，data-theme 是该风格下的配色。
export function publishCommittedDocumentAppearance(appearance: UiAppearanceChoice) {
  const root = document.documentElement;
  root.dataset.uiStyle = appearance.uiStyle;
  root.dataset.theme = appearance.theme;

  // Remove attributes emitted by builds that predate the single root owner.
  root.removeAttribute("data-preboot-theme");
  document.body.removeAttribute("data-theme");
}

// 实际生效的动画风格（自动关闭选过之后）的唯一发布点，三部分一起换（docs/animation_style_2026-10-26.md）：
// body[data-animations] 是风格标识，CSS 动效令牌按它取；磁石全局层整层换成这种风格的一组；磁石的动画档位。
// 先换全局层再换档位：换到无动画时正在走的运动当场到终态，换回会动的风格时新的运动已按新参数走。
export function publishAnimationStyle(style: AnimationStyleDefinition) {
  document.body.dataset.animations = style.id;
  setInteractionGlobalOverrides(style.lodestone);
  setInteractionAnimationMode(style.engineMode);
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
