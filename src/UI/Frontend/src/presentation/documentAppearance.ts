import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import { setInteractionAnimationMode, setInteractionGlobalOverrides } from "../interactions/solid/lodestone.ts";
import type { AnimationStyleDefinition } from "./animationStyle.ts";
import type { UiAppearanceChoice } from "./uiStyles.ts";

// The root owns appearance publication: data-ui-style selects the style and data-theme its palette.
export function publishCommittedDocumentAppearance(appearance: UiAppearanceChoice) {
  const root = document.documentElement;
  root.dataset.uiStyle = appearance.uiStyle;
  root.dataset.theme = appearance.theme;

  // Clear duplicate appearance attributes so only the root owns them.
  root.removeAttribute("data-preboot-theme");
  document.body.removeAttribute("data-theme");
}

// Publish the effective animation style to CSS, the Lodestone layer and mode, and native window transitions.
// body[data-animations] selects CSS tokens; Lodestone receives the complete matching parameter layer.
// The native shell disables minimize/maximize/restore transitions for disabled animation styles;
// other styles follow Windows animation settings.
// Replace the layer before the mode: disabling settles active motion; reenabling uses the new parameters.
export function publishAnimationStyle(style: AnimationStyleDefinition) {
  document.body.dataset.animations = style.id;
  setInteractionGlobalOverrides(style.lodestone);
  setInteractionAnimationMode(style.engineMode);
  getHostMessageTransport()?.postMessage({ type: "shell.animationStyle", style: style.id });
}

// Canvas consumers subscribe here for style, palette, and system light/dark changes.
// Colors come from CSS variables; notifications only request a redraw.
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
