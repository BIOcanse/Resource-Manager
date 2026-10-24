import type { ConcreteAppLanguageMode } from "./settingsTypes.ts";

// Share in-flight/successful loads; a failed request must remain retryable.
export function createLanguageBundleLoader<T>(load: (language: ConcreteAppLanguageMode) => Promise<T>) {
  const cache = new Map<ConcreteAppLanguageMode, Promise<T>>();
  const ready = new Set<ConcreteAppLanguageMode>();
  return {
    isReady: (language: ConcreteAppLanguageMode) => ready.has(language),
    load(language: ConcreteAppLanguageMode): Promise<T> {
      const cached = cache.get(language);
      if (cached) return cached;
      const pending = Promise.resolve().then(() => load(language)).then((value) => {
        ready.add(language);
        return value;
      }).catch((error: unknown) => {
        cache.delete(language);
        throw error;
      });
      cache.set(language, pending);
      return pending;
    }
  };
}
