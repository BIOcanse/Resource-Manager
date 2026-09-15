import { uiText } from "./i18n/appTextStore.ts";
import type { ManagementKind } from "./types.ts";

// 界面文案只能在渲染或调用时读取，不要在模块顶层求值固化，否则切换语言后不会更新。
export { uiText };

export function softwareKindLabel(kind: string | null | undefined) {
  const key = (kind ?? "") as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[key] ?? uiText.softwareKind.Other;
}

export function softwareDisplayKindLabel(kind: string | null | undefined, displayKind?: string | null) {
  const kindLabel = softwareKindLabel(kind);
  const displayText = displayKind?.trim();
  if (displayText === "其他软件" || displayText === "其他应用" || displayText === "运行中软件") {
    return uiText.softwareKind.Other;
  }

  return displayText || kindLabel;
}

export function managementKindLabel(kind: ManagementKind) {
  return kind === "Dependency" || kind === "Support"
    ? uiText.managementRole[kind]
    : uiText.softwareKind[kind];
}

export function managementRoleLabel(role: string | null | undefined) {
  return role === "Support"
    ? uiText.managementRole.Support
    : uiText.managementRole.Dependency;
}

export { applyLanguage, currentLanguage, currentSettingsText } from "./i18n/appTextStore.ts";
export {
  isRightToLeftLanguage,
  languageOptions,
  normalizeLanguageMode,
  resolveLanguageMode
} from "./i18n/settingsLanguages.ts";
export { fallbackSettingsText, loadSettingsText, loadingSettingsText } from "./i18n/settingsLoader.ts";
export type { CreditGroup, LanguageOption, SettingsTextBundle } from "./i18n/settingsTypes.ts";
export type { AppCopy } from "./i18n/copy/index.ts";
