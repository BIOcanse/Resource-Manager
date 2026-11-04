// Frontend mirror of the canonical src/Shared/Appearance/ui-styles.json catalog.
// Keep style and palette entries identical to the shared catalog.

import type { AppPalette, AppUiStyle } from "../types.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

export interface UiStyleDefinition {
  id: AppUiStyle;
  // The first palette is the style default.
  palettes: readonly AppPalette[];
}

// Catalog order controls options; a frontend point supplies the default style.
export const uiStyles: readonly UiStyleDefinition[] = [
  { id: "standard", palettes: ["system", "light", "dark", "lowContrast"] },
  { id: "classic", palettes: ["system", "light", "dark", "lowContrast"] },
  { id: "soft", palettes: ["system", "light", "dark"] },
  { id: "outline", palettes: ["system", "paper", "graphite", "blueprint"] },
  { id: "terminal", palettes: ["phosphor", "amber", "ice"] }
];

export interface UiAppearanceChoice {
  uiStyle: AppUiStyle;
  theme: AppPalette;
}

// Points supply default style/palette; an incompatible palette falls back to the selected style's first entry.
export function normalizeUiAppearance(uiStyle?: string | null, theme?: string | null): UiAppearanceChoice {
  const style = findUiStyle(uiStyle);
  const palette = style.palettes.find((candidate) => candidate === (theme ?? ResourceManager.FrontendPoints.Theme)) ?? style.palettes[0];
  return { uiStyle: style.id, theme: palette };
}

export function uiStylePalettes(uiStyle: AppUiStyle): readonly AppPalette[] {
  return findUiStyle(uiStyle).palettes;
}

function findUiStyle(uiStyle?: string | null): UiStyleDefinition {
  const declaredDefault = uiStyles.find(candidate => candidate.id === ResourceManager.FrontendPoints.UiStyle);
  if (!declaredDefault) throw new Error("Invalid frontend style point");
  return uiStyles.find((candidate) => candidate.id === uiStyle) ?? declaredDefault;
}
