import { dirname, posix, resolve } from "node:path";
import type { Plugin } from "vite";

// Bind explicitly marked locale imports to the chunk already emitted by Vite.
// Never fetch raw TypeScript or duplicate locale entry points in the build.
export function retryableLocaleImportsPlugin(): Plugin {
  const targets = new Map<string, string>();
  return {
    name: "resource-manager-retryable-locale-imports",
    apply: "build",
    transform(source, id) {
      if (!/\/i18n\/(?:copy\/appCopyLoader|settingsLoader)\.ts$/u.test(id.replaceAll("\\", "/"))) return;
      const code = source.replace(/retryableLocaleImport\(\(\) => import\("([^"]+)"\)\)/gu, (_match, path: string) => {
        const marker = `__RM_LOCALE_CHUNK_${targets.size}__`;
        targets.set(marker, resolve(dirname(id), path).replaceAll("\\", "/"));
        return `retryableLocaleImport(() => import(${JSON.stringify(path)}), ${JSON.stringify(marker)})`;
      });
      return { code, map: null };
    },
    generateBundle(_options, bundle) {
      const locales = new Map(Object.values(bundle)
        .filter(output => output.type === "chunk" && output.facadeModuleId)
        .map(output => [output.type === "chunk" ? output.facadeModuleId?.replaceAll("\\", "/") : "", output.fileName]));
      for (const output of Object.values(bundle)) {
        if (output.type !== "chunk") continue;
        for (const [marker, target] of targets) {
          if (!output.code.includes(marker)) continue;
          const locale = locales.get(target);
          if (!locale) this.error(`No compiled locale chunk for ${target}`);
          let relative = posix.relative(posix.dirname(output.fileName), locale!);
          if (!relative.startsWith(".")) relative = `./${relative}`;
          output.code = output.code.replace(new RegExp('["\'`]' + marker + '["\'`]', "g"),
            `new URL(${JSON.stringify(relative)},import.meta.url).href`);
        }
      }
    }
  };
}
