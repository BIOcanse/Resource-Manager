import { retryableLocaleImport } from "../retryableLocaleImport.ts";
import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { createAppCopy } from "./index.ts";
import type { AppCopy } from "./index.ts";
import { createLanguageBundleLoader } from "../languageBundleLoader.ts";

// 中文与英文随主包发出，其余已开放语言的独立文案包按需动态载入并缓存。
// 这里登记的语言必须与 settingsLanguages.ts 的 languageOptions 一致：可选即可载入。
type AppCopyModule = { default: AppCopy };
type AppCopyLoader = () => Promise<AppCopyModule>;

const localeLoaders = {
  "zh-TW": retryableLocaleImport(() => import("./locales/zh-TW.ts")),
  "ja-JP": retryableLocaleImport(() => import("./locales/ja-JP.ts")),
  "ko-KR": retryableLocaleImport(() => import("./locales/ko-KR.ts")),
  "fr-FR": retryableLocaleImport(() => import("./locales/fr-FR.ts")),
  "de-DE": retryableLocaleImport(() => import("./locales/de-DE.ts")),
  "es-ES": retryableLocaleImport(() => import("./locales/es-ES.ts")),
  "ru-RU": retryableLocaleImport(() => import("./locales/ru-RU.ts"))
} satisfies Partial<Record<ConcreteAppLanguageMode, AppCopyLoader>>;

type LazyLanguage = keyof typeof localeLoaders;

const bundles = createLanguageBundleLoader<AppCopy>((language) => {
  if (!isLazyLanguage(language)) return Promise.resolve(createAppCopy(language));
  return localeLoaders[language]().then((module) => module.default).catch((error: unknown) => {
    console.error(`[i18n] 语言包载入失败：${language}`, error);
    throw error;
  });
});

/** 该语言的文案包是否已经随主包在内存里（中文、英文基底，或已载入过的语言）。 */
export function appCopyReady(language: ConcreteAppLanguageMode) {
  return !isLazyLanguage(language) || bundles.isReady(language);
}

/** 只返回指定语言；失败不缓存，调用方可明确重试。 */
export function loadAppCopy(language: ConcreteAppLanguageMode): Promise<AppCopy> {
  return bundles.load(language);
}

function isLazyLanguage(language: ConcreteAppLanguageMode): language is LazyLanguage {
  return language in localeLoaders;
}
