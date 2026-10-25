// 界面风格与配色目录的前端副本。唯一来源是 src/Shared/Appearance/ui-styles.json，
// tests/uiStyleCatalogContract.test.ts 校验两边一致。规则见 docs/ui_styles.md。

import type { AppPalette, AppUiStyle } from "../types.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

export interface UiStyleDefinition {
  id: AppUiStyle;
  // 第一个是该风格的默认配色。
  palettes: readonly AppPalette[];
}

// 目录顺序用于列出选项；默认风格由前端点声明。
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

// 缺省风格/配色来自点；切换风格时，不适用的配色取该风格目录的首项。
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
