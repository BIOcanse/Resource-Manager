// The build plugin supplies the compiled locale URL. Keep normal Vite imports
// (including their preload graph) for the first request; only an explicit retry
// uses a new module URL because browsers remember failed dynamic imports.
export function retryableLocaleImport<T>(initial: () => Promise<T>, compiledUrl?: string) {
  let failures = 0;
  return async (): Promise<T> => {
    try {
      if (failures === 0 || !compiledUrl) return await initial();
      const url = new URL(compiledUrl);
      url.searchParams.set("localeRetry", String(failures));
      return await import(/* @vite-ignore */ url.href) as T;
    } catch (error) {
      failures++;
      throw error;
    }
  };
}
