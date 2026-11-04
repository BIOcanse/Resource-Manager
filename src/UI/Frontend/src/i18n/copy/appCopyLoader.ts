import { retryableLocaleImport } from "../retryableLocaleImport.ts";
import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { createAppCopy } from "./index.ts";
import type { AppCopy } from "./index.ts";
import { createLanguageBundleLoader } from "../languageBundleLoader.ts";

// Bundle Chinese and English eagerly; load and cache other languages on demand.
// Every selectable language must have a loader.
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

/** Whether the requested copy is already bundled or loaded. */
export function appCopyReady(language: ConcreteAppLanguageMode) {
  return !isLazyLanguage(language) || bundles.isReady(language);
}

/** Load the requested language; failed loads remain explicitly retryable. */
export function loadAppCopy(language: ConcreteAppLanguageMode): Promise<AppCopy> {
  return bundles.load(language);
}

function isLazyLanguage(language: ConcreteAppLanguageMode): language is LazyLanguage {
  return language in localeLoaders;
}
