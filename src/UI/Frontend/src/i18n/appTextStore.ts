import { batch, createSignal } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import { createAppCopy } from "./copy/index.ts";
import { loadAppCopy } from "./copy/appCopyLoader.ts";
import type { AppCopy } from "./copy/index.ts";
import { isRightToLeftLanguage, resolveLanguageMode } from "./settingsLanguages.ts";
import { fallbackSettingsText, loadSettingsText } from "./settingsLoader.ts";
import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import type { ConcreteAppLanguageMode, SettingsTextBundle } from "./settingsTypes.ts";

// Resolve the selected language, load its copy and replace it atomically.

// Clone each bundle because the store mutates its input.
const [copy, setCopy] = createStore<AppCopy>(cloneCopy(createAppCopy("zh-CN")));
const [settingsText, setSettingsText] = createSignal<SettingsTextBundle>(fallbackSettingsText);
const [activeLanguage, setActiveLanguage] = createSignal<ConcreteAppLanguageMode>("zh-CN");
export interface LanguageLoadError {
  readonly language: ConcreteAppLanguageMode;
  readonly cause: unknown;
}
const [languageLoadError, setLanguageLoadError] = createSignal<LanguageLoadError | null>(null);
export { languageLoadError };

export const uiText = copy;

export function currentLanguage() {
  return activeLanguage();
}

export function currentSettingsText() {
  return settingsText();
}

interface LanguageBundleLoaders {
  readonly appCopy: (language: ConcreteAppLanguageMode) => Promise<AppCopy>;
  readonly settingsText: (language: ConcreteAppLanguageMode) => Promise<SettingsTextBundle>;
}

export function createLanguageApplier(
  loaders: LanguageBundleLoaders,
  commit: (
    language: ConcreteAppLanguageMode,
    nextCopy: AppCopy,
    nextSettingsText: SettingsTextBundle
  ) => void,
  onError: (error: LanguageLoadError | null) => void = () => undefined
) {
  let appliedLanguage: ConcreteAppLanguageMode | null = null;
  let pendingRequest: object;

  return (language?: string | null): Promise<void> => {
    const resolved = resolveLanguageMode(language);
    // Cancel pending loads even when reselecting the active language.
    const request = {};
    pendingRequest = request;
    onError(null);
    if (resolved === appliedLanguage) {
      return Promise.resolve();
    }

    // Commit both bundles together to prevent mixed languages.
    return Promise.all([
      loaders.appCopy(resolved),
      loaders.settingsText(resolved)
    ]).then(([nextCopy, nextSettingsText]) => {
      if (pendingRequest !== request) {
        return;
      }
      commit(resolved, nextCopy, nextSettingsText);
      appliedLanguage = resolved;
    }).catch((cause: unknown) => {
      if (pendingRequest === request) {
        onError({ language: resolved, cause });
      }
      throw cause;
    });
  };
}

function applyLanguageBundle(
  language: ConcreteAppLanguageMode,
  nextCopy: AppCopy,
  nextSettingsText: SettingsTextBundle
) {
  batch(() => {
    setCopy(reconcile(cloneCopy(nextCopy)));
    setSettingsText(nextSettingsText);
    setActiveLanguage(language);
    applyDocumentLanguage(language);
    notifyShellLanguage(language);
  });
}

const requestLanguage = createLanguageApplier(
  { appCopy: loadAppCopy, settingsText: loadSettingsText },
  applyLanguageBundle,
  setLanguageLoadError
);

export function applyLanguage(language?: string | null): Promise<void> {
  return requestLanguage(language);
}

// Send the resolved language to the shell for runtime language changes.
// The shell reads saved settings independently before the frontend starts.
function notifyShellLanguage(language: ConcreteAppLanguageMode) {
  getHostMessageTransport()?.postMessage({ type: "shell.language", language });
}

// Copy plain objects while sharing immutable strings and functions.
// Traverse the copy tree iteratively, creating containers before copying leaves.

function cloneCopy<T>(value: T): T {
  if (!isPlainContainer(value)) {
    return value;
  }

  const root = createShell(value);
  const pending: Array<[unknown, unknown]> = [[value, root]];
  while (pending.length > 0) {
    const [source, target] = pending.pop() as [
      Record<string, unknown>,
      Record<string, unknown>
    ];
    for (const [key, item] of Object.entries(source)) {
      if (isPlainContainer(item)) {
        const shell = createShell(item);
        target[key] = shell;
        pending.push([item, shell]);
      } else {
        target[key] = item;
      }
    }
  }

  return root as T;
}

function isPlainContainer(value: unknown): value is Record<string, unknown> {
  return Array.isArray(value) || (!!value && typeof value === "object");
}

function createShell(value: Record<string, unknown>): Record<string, unknown> {
  return (Array.isArray(value) ? [] : {}) as Record<string, unknown>;
}

function applyDocumentLanguage(language: ConcreteAppLanguageMode) {
  if (typeof document === "undefined") {
    return;
  }

  document.documentElement.lang = language;
  document.documentElement.dir = isRightToLeftLanguage(language) ? "rtl" : "ltr";
}
