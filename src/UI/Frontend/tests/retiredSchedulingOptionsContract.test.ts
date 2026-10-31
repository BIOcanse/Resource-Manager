// RM 自调度和它触发的两个前端选项（按自调度自动关闭动画、刷新自动档的后台档位）在 2026-10-29 由用户删除：
// 前端源码、原生壳（src/UI/Core）和测试里不再有它们的订阅、解码、设置或文案。动画和刷新只按用户的手动设置。
// 旧缓存里保存的自动关闭开关只由 Migration frontend-preferences-self-scheduling-options-v1 在读入时丢弃。
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { extname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";
import { pointPreferences, resolveFrontendRefresh } from "../src/preferences/frontendPreferences.ts";

const frontendRoot = fileURLToPath(new URL("../", import.meta.url));
const repositoryRoot = join(frontendRoot, "../../..");
const thisFile = fileURLToPath(import.meta.url);

// Assembled from parts so that this file does not itself contain the retired names.
const retired: readonly { name: string; pattern: RegExp }[] = [
  { name: "self-scheduling endpoint", pattern: new RegExp(["resource-manager", "scheduling"].join("\\/")) },
  { name: "self-scheduling source or type", pattern: new RegExp(["self", "Scheduling"].join(""), "i") },
  { name: "auto-disable point", pattern: new RegExp(["Auto", "Disable", "Animations"].join("")) },
  { name: "auto background point", pattern: new RegExp(["Auto", "Background", "Preset"].join(""), "i") },
  { name: "background refresh profile", pattern: new RegExp(["background", "Optimized"].join("")) }
];
// The cache field name may appear only where the read migration drops it and where its tests prove that.
const cacheField = ["auto", "Disable", "Animations"].join("");
const cacheFieldOwners = new Set([
  "src/UI/Frontend/src/preferences/frontendPreferences.ts",
  "src/UI/Core/Preferences/FrontendPreferencesOwner.cs",
  "src/UI/Frontend/tests/frontendPreferences.test.ts",
  "src/UI/Frontend/tests/browser/animationStyleJourney.ts",
  "tests/App/FrontendPreferencesCacheTests.cs"
]);
const scannedExtensions = new Set([".ts", ".tsx", ".js", ".mjs", ".cjs", ".json", ".css", ".cs", ".zig"]);

// Explicit stack, no recursion.
function collectFiles(start: string): string[] {
  const files: string[] = [];
  const pending = [start];
  while (pending.length > 0) {
    const current = pending.pop()!;
    if (statSync(current).isFile()) {
      files.push(current);
      continue;
    }
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (entry.name === "node_modules" || entry.name === "bin" || entry.name === "obj") continue;
      const path = join(current, entry.name);
      if (entry.isDirectory()) pending.push(path);
      else if (scannedExtensions.has(extname(entry.name))) files.push(path);
    }
  }
  return files;
}

const files = [
  ...collectFiles(join(frontendRoot, "src")),
  ...collectFiles(join(frontendRoot, "tests")),
  ...collectFiles(join(repositoryRoot, "src/UI/Core")),
  join(repositoryRoot, "tests/App/FrontendPreferencesCacheTests.cs"),
  join(frontendRoot, "package.json"),
  join(frontendRoot, "preferences.points.json")
].filter((path) => path !== thisFile);
assert.ok(files.length > 100, `the scan found only ${files.length} files`);

const findings: string[] = [];
for (const path of files) {
  const text = readFileSync(path, "utf8");
  const name = relative(repositoryRoot, path).replaceAll("\\", "/");
  for (const { name: label, pattern } of retired) {
    if (pattern.test(text)) findings.push(`${name}: ${label}`);
  }
  if (text.includes(cacheField) && !cacheFieldOwners.has(name)) findings.push(`${name}: retired cache field outside its read migration`);
}
assert.deepEqual(findings, [], "retired self-scheduling names remain");

// The two points are gone from the frontend point catalog; the generated values no longer carry them.
const points = ResourceManager.FrontendPoints as unknown as Record<string, unknown>;
assert.equal(["Auto", "Disable", "Animations"].join("") in points, false);
assert.equal(["Auto", "Background", "Preset"].join("") in points, false);
assert.equal(cacheField in pointPreferences().appearance, false);

// The refresh plan is the manual setting only: one resolved context, and automatic resolves to the automatic foreground preset.
const plan = resolveFrontendRefresh(pointPreferences().refresh);
assert.deepEqual(Object.keys(plan).sort(), ["defaults", "presets", "resolved"]);
for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
  assert.equal(plan.resolved[key], plan.presets[key][ResourceManager.FrontendPoints.AutoForegroundPreset]);
}

// The settings page has no control or copy for the two options.
const appearanceSection = readFileSync(join(frontendRoot, "src/features/settings/components/AppearanceSettingsSection.tsx"), "utf8");
assert.doesNotMatch(appearanceSection, /SwitchControl/, "the appearance section has no switch left");
const settingsTypes = readFileSync(join(frontendRoot, "src/i18n/settingsTypes.ts"), "utf8");
assert.doesNotMatch(settingsTypes, new RegExp(`${cacheField}(Title|Description)`));

console.log(`Retired self-scheduling options: ${files.length} files hold no reference; the refresh plan is the manual setting only.`);
