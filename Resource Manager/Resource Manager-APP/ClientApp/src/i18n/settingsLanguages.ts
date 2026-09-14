import type { AppLanguageMode } from "../types";
import type { ConcreteAppLanguageMode, LanguageOption } from "./settingsTypes";

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
  { id: "es-MX", label: "Spanish (Mexico)", nativeLabel: "Español (México)" },
  { id: "pt-BR", label: "Portuguese (Brazil)", nativeLabel: "Português (Brasil)" },
  { id: "pt-PT", label: "Portuguese (Portugal)", nativeLabel: "Português (Portugal)" },
  { id: "ru-RU", label: "Russian", nativeLabel: "Русский" },
  { id: "uk-UA", label: "Ukrainian", nativeLabel: "Українська" },
  { id: "pl-PL", label: "Polish", nativeLabel: "Polski" },
  { id: "tr-TR", label: "Turkish", nativeLabel: "Türkçe" },
  { id: "it-IT", label: "Italian", nativeLabel: "Italiano" },
  { id: "nl-NL", label: "Dutch", nativeLabel: "Nederlands" },
  { id: "sv-SE", label: "Swedish", nativeLabel: "Svenska" },
  { id: "fi-FI", label: "Finnish", nativeLabel: "Suomi" },
  { id: "da-DK", label: "Danish", nativeLabel: "Dansk" },
  { id: "nb-NO", label: "Norwegian Bokmal", nativeLabel: "Norsk bokmål" },
  { id: "cs-CZ", label: "Czech", nativeLabel: "Čeština" },
  { id: "hu-HU", label: "Hungarian", nativeLabel: "Magyar" },
  { id: "ro-RO", label: "Romanian", nativeLabel: "Română" },
  { id: "el-GR", label: "Greek", nativeLabel: "Ελληνικά" },
  { id: "he-IL", label: "Hebrew", nativeLabel: "עברית" },
  { id: "ar-SA", label: "Arabic", nativeLabel: "العربية" },
  { id: "hi-IN", label: "Hindi", nativeLabel: "हिन्दी" },
  { id: "id-ID", label: "Indonesian", nativeLabel: "Bahasa Indonesia" },
  { id: "vi-VN", label: "Vietnamese", nativeLabel: "Tiếng Việt" },
  { id: "th-TH", label: "Thai", nativeLabel: "ไทย" }
];

export const supportedLanguageIds = new Set<AppLanguageMode>(languageOptions.map((option) => option.id));

export function normalizeLanguageMode(language?: string | null): AppLanguageMode {
  const value = (language ?? "").trim() as AppLanguageMode;
  return supportedLanguageIds.has(value) ? value : "system";
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
  const normalized = language.replace("_", "-");
  const exact = normalized as AppLanguageMode;
  if (exact !== "system" && supportedLanguageIds.has(exact)) {
    return exact as ConcreteAppLanguageMode;
  }

  const prefix = normalized.split("-")[0]?.toLowerCase();
  switch (prefix) {
    case "zh":
      return normalized.toLowerCase().includes("tw") || normalized.toLowerCase().includes("hk")
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
    case "pt":
      return "pt-BR";
    case "ru":
      return "ru-RU";
    case "uk":
      return "uk-UA";
    case "pl":
      return "pl-PL";
    case "tr":
      return "tr-TR";
    case "it":
      return "it-IT";
    case "nl":
      return "nl-NL";
    case "sv":
      return "sv-SE";
    case "fi":
      return "fi-FI";
    case "da":
      return "da-DK";
    case "nb":
    case "no":
      return "nb-NO";
    case "cs":
      return "cs-CZ";
    case "hu":
      return "hu-HU";
    case "ro":
      return "ro-RO";
    case "el":
      return "el-GR";
    case "he":
      return "he-IL";
    case "ar":
      return "ar-SA";
    case "hi":
      return "hi-IN";
    case "id":
      return "id-ID";
    case "vi":
      return "vi-VN";
    case "th":
      return "th-TH";
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
