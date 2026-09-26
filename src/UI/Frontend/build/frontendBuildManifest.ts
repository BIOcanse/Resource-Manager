import { createHash } from "node:crypto";
import type { OutputBundle, OutputChunk, Plugin } from "vite";

export const frontendBuildManifestContract = "resource-manager-frontend-assets-v1";

export interface FrontendBuildManifest {
  contract: typeof frontendBuildManifestContract;
  buildIdentity: string;
  entryScripts: string[];
  stylesheets: string[];
}

interface ViteOutputChunk extends OutputChunk {
  viteMetadata?: {
    importedCss?: ReadonlySet<string>;
  };
}

export function createFrontendBuildManifest(
  entryScripts: readonly string[],
  stylesheets: readonly string[]
): FrontendBuildManifest {
  const normalizedScripts = normalizeAssetPaths(entryScripts);
  const normalizedStylesheets = normalizeAssetPaths(stylesheets);
  if (normalizedScripts.length === 0) {
    throw new Error("The frontend build has no entry script.");
  }

  const canonical = canonicalManifestText(normalizedScripts, normalizedStylesheets);
  return {
    contract: frontendBuildManifestContract,
    buildIdentity: `sha256:${createHash("sha256").update(canonical, "utf8").digest("hex")}`,
    entryScripts: normalizedScripts,
    stylesheets: normalizedStylesheets
  };
}

export function frontendBuildManifestPlugin(): Plugin {
  return {
    name: "resource-manager-frontend-build-manifest",
    apply: "build",
    enforce: "post",
    generateBundle(_options, bundle) {
      const manifest = createManifestFromBundle(bundle);
      this.emitFile({
        type: "asset",
        fileName: "frontend-build.json",
        source: `${JSON.stringify(manifest, null, 2)}\n`
      });
    }
  };
}

function createManifestFromBundle(bundle: OutputBundle): FrontendBuildManifest {
  const entryChunks = Object.values(bundle)
    .filter((output): output is OutputChunk => output.type === "chunk" && output.isEntry);
  const entryScripts = entryChunks.map((chunk) => chunk.fileName);
  const stylesheets = collectEntryStylesheets(bundle, entryChunks);
  return createFrontendBuildManifest(entryScripts, stylesheets);
}

function collectEntryStylesheets(
  bundle: OutputBundle,
  entryChunks: readonly OutputChunk[]
): string[] {
  const stylesheets = new Set<string>();
  const pending = entryChunks.map((chunk) => chunk.fileName);
  const visited = new Set<string>();

  while (pending.length > 0) {
    const fileName = pending.shift()!;
    if (visited.has(fileName)) {
      continue;
    }
    visited.add(fileName);

    const output = bundle[fileName];
    if (!output || output.type !== "chunk") {
      continue;
    }

    const chunk = output as ViteOutputChunk;
    for (const stylesheet of chunk.viteMetadata?.importedCss ?? []) {
      stylesheets.add(stylesheet);
    }
    for (const importedChunk of chunk.imports) {
      pending.push(importedChunk);
    }
  }

  return [...stylesheets];
}

function normalizeAssetPaths(paths: readonly string[]): string[] {
  const normalized = paths.map((path) => {
    const value = path.replaceAll("\\", "/").replace(/^\/+/, "");
    if (!value || value.split("/").some((segment) => segment === "." || segment === "..")) {
      throw new Error(`Invalid frontend asset path: ${path}`);
    }
    return `/${value}`;
  });

  return [...new Set(normalized)].sort((left, right) => left.localeCompare(right, "en"));
}

function canonicalManifestText(
  entryScripts: readonly string[],
  stylesheets: readonly string[]
): string {
  return [
    frontendBuildManifestContract,
    ...entryScripts.map((path) => `script:${path}`),
    ...stylesheets.map((path) => `style:${path}`),
    ""
  ].join("\n");
}
