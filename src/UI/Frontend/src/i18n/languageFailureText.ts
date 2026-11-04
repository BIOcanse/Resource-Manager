import type { ConcreteAppLanguageMode } from "./settingsTypes.ts";

// Kept in the main bundle so failed locale downloads cannot hide recovery text.
const copy: Partial<Record<ConcreteAppLanguageMode, { message: string; retry: string }>> = {
  "zh-CN": { message: "界面语言加载失败，仍使用上一次完整加载的语言。", retry: "重试" },
  "zh-TW": { message: "介面語言載入失敗，仍使用上一次完整載入的語言。", retry: "重試" },
  "en-US": { message: "Could not load the interface language. The last fully loaded language remains active.", retry: "Retry" },
  "ja-JP": { message: "表示言語を読み込めませんでした。最後に読み込みが完了した言語を引き続き使用します。", retry: "再試行" },
  "ko-KR": { message: "인터페이스 언어를 불러오지 못했습니다. 마지막으로 완전히 불러온 언어를 계속 사용합니다.", retry: "다시 시도" },
  "fr-FR": { message: "Impossible de charger la langue de l’interface. La dernière langue entièrement chargée reste active.", retry: "Réessayer" },
  "de-DE": { message: "Die Sprache der Oberfläche konnte nicht geladen werden. Die zuletzt vollständig geladene Sprache bleibt aktiv.", retry: "Erneut versuchen" },
  "es-ES": { message: "No se pudo cargar el idioma de la interfaz. Sigue activo el último idioma cargado por completo.", retry: "Reintentar" },
  "ru-RU": { message: "Не удалось загрузить язык интерфейса. Используется последний полностью загруженный язык.", retry: "Повторить" }
};

export function languageFailureText(language: ConcreteAppLanguageMode) {
  return copy[language]!;
}
