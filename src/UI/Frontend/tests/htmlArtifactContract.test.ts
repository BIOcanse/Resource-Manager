import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const sourceHtml = readFileSync(new URL("../index.html", import.meta.url), "utf8");
const managedRoot = fileURLToPath(new URL("../../../../src/Core/wwwroot/", import.meta.url));
const managedHtml = readFileSync(join(managedRoot, "index.html"), "utf8");

assertHtmlShell(sourceHtml, "source");
assertHtmlShell(managedHtml, "managed");

const sourceModules = moduleScriptSources(sourceHtml);
assert.deepEqual(sourceModules, ["/src/main.tsx"]);

const managedModules = moduleScriptSources(managedHtml);
assert.equal(managedModules.length, 1);
assert.match(managedModules[0], /^\/assets\/index-[A-Za-z0-9_-]+\.js$/);
assert.doesNotMatch(
  managedHtml,
  /@vite\/client|\bsrc=["']\/src\/|\blocalhost\b|127\.0\.0\.1|file:\/\//i);

const managedAssets = [...managedHtml.matchAll(/\b(?:src|href)=["']([^"']+)["']/gi)]
  .map((match) => match[1])
  .filter((value) => value.startsWith("/assets/"));
assert.ok(managedAssets.length >= 2, "Managed HTML must reference its JS and CSS assets.");
assert.equal(new Set(managedAssets).size, managedAssets.length);
for (const asset of managedAssets) {
  assert.match(asset, /^\/assets\/[A-Za-z0-9._-]+$/);
  const path = join(managedRoot, asset.slice(1));
  assert.ok(existsSync(path), `Managed HTML asset is missing: ${asset}`);
  assert.ok(statSync(path).size > 0, `Managed HTML asset is empty: ${asset}`);
}

const managedModulePath = join(managedRoot, managedModules[0].slice(1));
const managedModule = readFileSync(managedModulePath, "utf8");
const managedJavaScript = readdirSync(join(managedRoot, "assets"))
  .filter((name) => name.endsWith(".js"))
  .map((name) => readFileSync(join(managedRoot, "assets", name), "utf8"))
  .join("\n");
assert.doesNotMatch(
  managedModule,
  /resourceDangerLineTitle|onAdaptedResourceDangerLinesChange|资源安全线|Resource Safety Limits/,
  "Built UI must not expose actionable controls for retired resource-movement settings.");
assert.doesNotMatch(
  managedModule,
  /GPU0.{0,160}GPU1.{0,160}GPU2.{0,160}GPU3/s,
  "Built UI must not contain the former static exact-GPU option inventory.");
assert.match(managedModule, /processCapabilities/);
assert.match(managedModule, /targetInventory/);
assert.match(managedModule, /D3D11/);
assert.match(
  managedModule,
  /host\.backend-session\.request/,
  "Built UI must request the Native UI verified backend session projection.");
assert.match(
  managedModule,
  /local-system\.status\.v1/,
  "Built UI must contain the strict local-system response decoder.");
assert.match(
  managedJavaScript,
  /device-topology\.state\.v3/,
  "Built UI must contain the strict device-topology response decoder.");
assert.match(
  managedJavaScript,
  /\/api\/device-topology\/state/,
  "Built UI must contain the device-topology domain route.");
assert.match(
  managedJavaScript,
  /device-topology-panel/,
  "Built UI must contain the device-topology surface.");
assert.doesNotMatch(
  managedJavaScript,
  /data-frontend-mark|data-frontend-partitions|visible-regions|ResourceManagerVisibleRegion/,
  "Built UI must not contain the retired resource Mark control plane.");
assert.match(
  managedJavaScript,
  /data-frontend-visibility-surface/,
  "Built UI must preserve direct frontend visibility occupancy.");
assert.match(
  managedJavaScript,
  /data-frontend-visibility-demands/,
  "Built UI must bind visible surfaces to frontend work demand.");
assert.match(
  managedJavaScript,
  /resource-breakdown\.layout-settle/,
  "Built UI must contain the first TaskRegistry-managed UI transition.");
assert.match(
  managedJavaScript,
  /External durable tasks are projections/,
  "Built UI must preserve the TaskRegistry external-owner boundary.");

function assertHtmlShell(html: string, label: string) {
  assert.match(html, /^\s*<!doctype html>/i, `${label} HTML must declare HTML5.`);
  assert.match(html, /<html\b[^>]*\blang=["']zh-CN["']/i);
  assert.match(html, /<meta\b[^>]*\bcharset=["']utf-8["']/i);
  assert.match(
    html,
    /<meta\b[^>]*\bname=["']viewport["'][^>]*\bcontent=["']width=device-width, initial-scale=1["']/i);
  assert.equal(
    [...html.matchAll(/\bid=["']root["']/gi)].length,
    1,
    `${label} HTML must contain exactly one root ID.`);
  assert.match(html, /<div\b[^>]*\bid=["']root["'][^>]*>\s*<\/div>/i);
  assert.equal((html.match(/<body\b/gi) ?? []).length, 1);
  assert.equal((html.match(/<\/body>/gi) ?? []).length, 1);
  assert.match(html, /document\.documentElement\.dataset\.theme\s*=\s*theme/,
    `${label} HTML must publish its preboot palette through the root theme owner.`);
  assert.doesNotMatch(html, /prebootTheme|data-preboot-theme/,
    `${label} HTML must not retain a second preboot theme attribute.`);
}

function moduleScriptSources(html: string) {
  return [...html.matchAll(/<script\b[^>]*>/gi)]
    .map((match) => match[0])
    .filter((tag) => /\btype=["']module["']/i.test(tag))
    .map((tag) => tag.match(/\bsrc=["']([^"']+)["']/i)?.[1] ?? "");
}
