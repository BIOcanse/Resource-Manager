// 界面风格与配色目录的前端副本。唯一来源是 src/Shared/Appearance/ui-styles.json，
// tests/uiStyleCatalogContract.test.ts 校验两边一致。规则见 docs/ui_styles.md。

import type { AppPalette, AppUiStyle } from "../types.ts";

export interface UiStyleDefinition {
  id: AppUiStyle;
  // 第一个是该风格的默认配色。
  palettes: readonly AppPalette[];
}

// 第一个是默认风格。
export const uiStyles: readonly UiStyleDefinition[] = [
  { id: "standard", palettes: ["system", "light", "dark", "lowContrast"] },
  { id: "soft", palettes: ["system", "light", "dark"] },
  { id: "outline", palettes: ["system", "paper", "graphite", "blueprint"] },
  { id: "terminal", palettes: ["phosphor", "amber", "ice"] }
];

export interface UiAppearanceChoice {
  uiStyle: AppUiStyle;
  theme: AppPalette;
}

// 和后端归一化同一条规则：风格不认识就用默认风格；配色不在该风格里就用该风格的默认配色。
export function normalizeUiAppearance(uiStyle?: string | null, theme?: string | null): UiAppearanceChoice {
  const style = findUiStyle(uiStyle);
  const palette = style.palettes.find((candidate) => candidate === theme) ?? style.palettes[0];
  return { uiStyle: style.id, theme: palette };
}

export function uiStylePalettes(uiStyle: AppUiStyle): readonly AppPalette[] {
  return findUiStyle(uiStyle).palettes;
}

function findUiStyle(uiStyle?: string | null): UiStyleDefinition {
  return uiStyles.find((candidate) => candidate.id === uiStyle) ?? uiStyles[0];
}
