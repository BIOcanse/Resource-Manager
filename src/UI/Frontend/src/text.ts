import { uiText } from "./i18n/appTextStore.ts";
import type { ManagementKind } from "./types.ts";

// Read localized copy at render or call time; module-level evaluation would freeze the language.
export { uiText };

export function softwareKindLabel(kind: string | null | undefined) {
  const key = (kind ?? "") as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[key] ?? uiText.softwareKind.other;
}

// displayKind is a backend display-group ID, distinct from localized wording;
// unknown groups fall back to the kind label.
export function softwareDisplayKindLabel(kind: string | null | undefined, displayKind?: string | null) {
  const group = (displayKind ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group] ?? softwareKindLabel(kind);
}

export function managementKindLabel(kind: ManagementKind) {
  return kind === "Dependency" || kind === "Support"
    ? uiText.managementRole[kind === "Dependency" ? "dependency" : "support"]
    : uiText.softwareKind[kind];
}

export function managementRoleLabel(role: string | null | undefined) {
  return role === "support"
    ? uiText.managementRole.support
    : uiText.managementRole.dependency;
}

export { applyLanguage, currentLanguage, currentSettingsText, languageLoadError } from "./i18n/appTextStore.ts";
export {
  isRightToLeftLanguage,
  languageOptions,
  normalizeLanguageMode,
  resolveLanguageMode
} from "./i18n/settingsLanguages.ts";
export { fallbackSettingsText, loadSettingsText, loadingSettingsText } from "./i18n/settingsLoader.ts";
export type { CreditGroup, LanguageOption, SettingsTextBundle } from "./i18n/settingsTypes.ts";
export type { AppCopy } from "./i18n/copy/index.ts";
