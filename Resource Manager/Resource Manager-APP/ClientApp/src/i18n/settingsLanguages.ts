import type { AppLanguageMode } from "../types.ts";
import type { ConcreteAppLanguageMode, LanguageOption } from "./settingsTypes.ts";

// 可选界面语言 = 已经有完整文案包的语言。`AppLanguageMode` 仍保留全部 31 个 id
// （它是与后端一致的持久化取值域），这里只列出当前交付的语言；其余语言的文案包
// 完成后，把对应行加回来并在 copy/appCopyLoader.ts 注册即可，不需要改其他地方。
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
    case "ru":
      return "ru-RU";
    // 这些语言的完整文案包还没做（见 global_interface_language 的 S4），系统语言是它们时
    // 落到英文基底而不是中文，避免给非中文用户显示中文。放开某个语言时把它移到上面即可。
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
    case "cs":
    case "hu":
    case "ro":
    case "el":
    case "he":
    case "ar":
    case "hi":
    case "id":
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
