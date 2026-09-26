import { uiText } from "./i18n/appTextStore.ts";
import type { ManagementKind } from "./types.ts";

// 界面文案只能在渲染或调用时读取，不要在模块顶层求值固化，否则切换语言后不会更新。
export { uiText };

export function softwareKindLabel(kind: string | null | undefined) {
  const key = (kind ?? "") as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[key] ?? uiText.softwareKind.Other;
}

// displayKind 是后端把若干 kind 合并后的展示分组标识（见后端 SoftwareDisplayKinds），
// 不是措辞；认不出时回落到 kind 自己的文案。
export function softwareDisplayKindLabel(kind: string | null | undefined, displayKind?: string | null) {
  const group = (displayKind ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group] ?? softwareKindLabel(kind);
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
