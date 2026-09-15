import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { createAppCopy } from "./index.ts";
import type { AppCopy } from "./index.ts";

// 中文与英文是随主包一起发出的基底，其余已交付的语言各自一份完整文案包，按需动态载入并缓存。
// 这里登记的语言必须与 settingsLanguages.ts 的 languageOptions 一致：可选即可载入。
type AppCopyModule = { default: AppCopy };
type AppCopyLoader = () => Promise<AppCopyModule>;

const localeLoaders = {
  "ja-JP": () => import("./locales/ja-JP.ts"),
  "ko-KR": () => import("./locales/ko-KR.ts"),
  "fr-FR": () => import("./locales/fr-FR.ts"),
  "de-DE": () => import("./locales/de-DE.ts"),
  "es-ES": () => import("./locales/es-ES.ts"),
  "ru-RU": () => import("./locales/ru-RU.ts")
} satisfies Partial<Record<ConcreteAppLanguageMode, AppCopyLoader>>;

type LazyLanguage = keyof typeof localeLoaders;

const cache = new Map<ConcreteAppLanguageMode, Promise<AppCopy>>();

/** 该语言的文案包是否已经随主包在内存里（中文、英文基底，或已载入过的语言）。 */
export function appCopyReady(language: ConcreteAppLanguageMode) {
  return !isLazyLanguage(language) || cache.has(language);
}

/** 取某个语言的完整文案包；载入失败时使用英文基底，并把失败写进日志。 */
export function loadAppCopy(language: ConcreteAppLanguageMode): Promise<AppCopy> {
  if (!isLazyLanguage(language)) {
    return Promise.resolve(createAppCopy(language));
  }

  const cached = cache.get(language);
  if (cached) {
    return cached;
  }

  const pending = localeLoaders[language]()
    .then((module) => module.default)
    .catch((error: unknown) => {
      console.error(`[i18n] 语言包载入失败：${language}`, error);
      return createAppCopy("en-US");
    });
  cache.set(language, pending);
  return pending;
}

function isLazyLanguage(language: ConcreteAppLanguageMode): language is LazyLanguage {
  return language in localeLoaders;
}
