import type { AppLanguageMode } from "../types.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import type { ConcreteAppLanguageMode, LanguageOption } from "./settingsTypes.ts";

// Offer languages with complete copy bundles.
// AppLanguageMode retains all persisted language IDs.

export const languageOptions: LanguageOption[] = [
  { id: "system", label: "System language", nativeLabel: "跟随系统 / System" },
  { id: "zh-CN", label: "Simplified Chinese", nativeLabel: "简体中文" },
  { id: "zh-TW", label: "Traditional Chinese", nativeLabel: "繁體中文" },
  { id: "en-US", label: "English", nativeLabel: "English" },
  { id: "ja-JP", label: "Japanese", nativeLabel: "日本語" },
  { id: "ko-KR", label: "Korean", nativeLabel: "한국어" },
  { id: "fr-FR", label: "French", nativeLabel: "Français" },
  { id: "de-DE", label: "German", nativeLabel: "Deutsch" },
  { id: "es-ES", label: "Spanish", nativeLabel: "Español" },
  { id: "ru-RU", label: "Russian", nativeLabel: "Русский" }
];

export const supportedLanguageIds = new Set<AppLanguageMode>(languageOptions.map((option) => option.id));
const canonicalLanguageIds = new Map(languageOptions.map((option) => [option.id.toLowerCase(), option.id]));

export function normalizeLanguageMode(language?: string | null): AppLanguageMode {
  const value = (language ?? "").trim().replaceAll("_", "-").toLowerCase();
  return canonicalLanguageIds.get(value) ?? ResourceManager.FrontendPoints.Language;
}

export function resolveLanguageMode(language?: string | null, candidates: readonly string[] = getBrowserLanguages()): ConcreteAppLanguageMode {
  const normalized = normalizeLanguageMode(language);
  if (normalized !== "system") {
    return normalized;
  }

  for (const candidate of candidates) {
    const matched = matchLanguage(candidate);
    if (matched) {
      return matched;
    }
  }

  return "zh-CN";
}

export function isRightToLeftLanguage(language: ConcreteAppLanguageMode) {
  return language === "ar-SA" || language === "he-IL";
}

function matchLanguage(language: string): ConcreteAppLanguageMode | null {
  const normalized = language.trim().replaceAll("_", "-");
  const exact = canonicalLanguageIds.get(normalized.toLowerCase());
  if (exact && exact !== "system") {
    return exact as ConcreteAppLanguageMode;
  }

  const prefix = normalized.split("-")[0]?.toLowerCase();
  switch (prefix) {
    case "zh":
      return /(?:^|-)(?:tw|hk|hant)(?:-|$)/i.test(normalized)
        ? "zh-TW"
        : "zh-CN";
    case "en":
      return "en-US";
    case "ja":
      return "ja-JP";
    case "ko":
      return "ko-KR";
    case "fr":
      return "fr-FR";
    case "de":
      return "de-DE";
    case "es":
      return "es-ES";
    case "ru":
      return "ru-RU";
    // Use English for system languages without a complete copy bundle.

    case "pt":
    case "uk":
    case "pl":
    case "tr":
    case "it":
    case "nl":
    case "sv":
    case "fi":
    case "da":
    case "nb":
    case "no":
    case "nn":
    case "cs":
    case "hu":
    case "ro":
    case "el":
    case "he":
    case "iw":
    case "ar":
    case "hi":
    case "id":
    case "in":
    case "vi":
    case "th":
      return "en-US";
    default:
      return null;
  }
}

function getBrowserLanguages() {
  if (typeof navigator === "undefined") {
    return [];
  }

  return navigator.languages?.length ? navigator.languages : [navigator.language].filter(Boolean);
}
