import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createCreditGroups } from "../src/i18n/settingsCredits.ts";
import { createSettingsLocale } from "../src/i18n/settingsLocaleFactory.ts";

const notices = new URL("../../ThirdPartyNotices/", import.meta.url);
const inventory = readFileSync(new URL("README.md", notices), "utf8");
const lock = JSON.parse(readFileSync(new URL("../package-lock.json", import.meta.url), "utf8"));
const allFrontendNotices = readFileSync(new URL("FrontendDependencyAcknowledgements.md", notices), "utf8");
for (const [path, item] of Object.entries(lock.packages)) {
  if (!path) continue;
  const name = path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length);
  const entry = item as { version: string; license?: string };
  assert.ok(allFrontendNotices.includes(`| ${name} | ${entry.version} |`), `Missing acknowledgement: ${path}`);
}
const managed = JSON.parse(readFileSync(new URL("../src/i18n/managedDependencyAcknowledgements.json", import.meta.url), "utf8")) as Array<{ name: string; version: string; authors: string; license: string; kind: string }>;
const actualManaged = new Set<string>();
for (const path of ["../../obj/project.assets.json", "../../../../src/UI/Core/obj/project.assets.json", "../../Shared/obj/project.assets.json", "../../Launcher/obj/project.assets.json", "../../../Resource Manager-APP.Tests/obj/project.assets.json",
  "../../../Resource Manager-AdapterSdk/csharp/ResourceManager.Adapter.Abstractions/obj/project.assets.json",
  "../../../Resource Manager-AdapterSdk/csharp/ResourceManager.Adapter.SharedMemory/obj/project.assets.json"]) {
  const assets = JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
  for (const [key, library] of Object.entries(assets.libraries)) {
    if ((library as { type: string }).type === "package") actualManaged.add(key.toLowerCase());
  }
  for (const framework of Object.values(assets.project.frameworks)) {
    for (const pack of (framework as { downloadDependencies?: Array<{ name: string; version: string }> }).downloadDependencies ?? []) {
      const version = pack.version.match(/^\[([^,]+),\s*\1\]$/)?.[1];
      assert.ok(version, `Unresolved runtime pack version: ${pack.name}`);
      actualManaged.add(`${pack.name}/${version}`.toLowerCase());
    }
  }
}
const acknowledged = new Set(managed.map((item) => `${item.name}/${item.version}`.toLowerCase()));
assert.deepEqual([...actualManaged].filter((id) => !acknowledged.has(id)), [], "Every resolved package must be acknowledged");
assert.ok(managed.every((item) => item.kind === "publish tool" || actualManaged.has(`${item.name}/${item.version}`.toLowerCase())), "Only explicitly identified publish tools may be absent from a plain build graph");
assert.ok(managed.every((item) => item.authors && item.license));
const managedNotices = readFileSync(new URL("ManagedDependencyAcknowledgements.md", notices), "utf8");
for (const item of managed) assert.ok(managedNotices.includes(`| ${item.name}/${item.version} |`), item.name);
const frontendNotices = new Map([
  ["lucide-solid", "Lucide.LICENSE.txt"],
  ["solid-js", "SolidJS.LICENSE.txt"],
  ["seroval", "Seroval.LICENSE.txt"],
  ["seroval-plugins", "SerovalPlugins.LICENSE.txt"],
  ["csstype", "CSSType.LICENSE.txt"]
]);

const runtimeDependencies = Object.entries(lock.packages)
  .filter(([path, item]) => path.startsWith("node_modules/") && !(item as { dev?: boolean }).dev)
  .map(([path]) => path.slice("node_modules/".length));
assert.deepEqual(runtimeDependencies.sort(), [...frontendNotices.keys()].sort(),
  "Review notices when the frontend production dependency closure changes");

for (const [name, file] of frontendNotices) {
  const version = lock.packages[`node_modules/${name}`].version;
  assert.ok(inventory.includes(`| ${name} | ${version} |`), `${name} version is absent from notices`);
  const original = readFileSync(new URL(`../node_modules/${name}/LICENSE`, import.meta.url), "utf8");
  const distributed = readFileSync(new URL(file, notices), "utf8");
  assert.equal(distributed.replaceAll("\r\n", "\n"), original.replaceAll("\r\n", "\n"),
    `${name} license or attribution was truncated or changed`);
}

const lucide = readFileSync(new URL("Lucide.LICENSE.txt", notices), "utf8");
assert.match(lucide, /ISC License/);
assert.match(lucide, /MIT License/);
assert.match(lucide, /Cole Bemis/);
assert.match(inventory, /SQLitePCLRaw\.core \| 3\.0\.3 \| Apache-2\.0/);

for (const language of ["zh-CN", "en-US"] as const) {
  const copy = createSettingsLocale(language);
  const groups = createCreditGroups(copy);
  assert.equal(groups.length, 6);
  assert.ok(groups.every((group) => group.title && group.items.length > 0));
  const all = groups.flatMap((group) => group.items);
  assert.equal(new Set(all.map((item) => item.name)).size, all.length);
  assert.ok(all.every((item) => item.role && item.note));
  assert.ok(all.every((item) => item.links.length > 0));
  assert.ok(all.every((item) => item.name !== "Kings"));
  assert.ok(all.flatMap(item => item.links).every(link => !/star|点星|點星/i.test(link.label)));
  for (const name of ["Khronos / Vulkan-Headers / Valve / LunarG", "Mesa / WGL contributors",
    "UL Solutions / 3DMark Steel Nomad", "GCC / MinGW-w64 / winpthreads / WinLibs"]) {
    assert.ok(all.some((item) => item.name === name), `Missing upstream thanks: ${name}`);
  }
  assert.match(all.find((item) => item.name === "Resource Manager")!.note, /Apache-2\.0/);
  assert.ok(all.find((item) => item.name === "Resource Manager")!.links.some((link) => link.href.endsWith("/LICENSE")));
  const runtime = groups.find((group) => group.title === copy.credits.groups.runtime)!;
  assert.ok(runtime.items.some((item) => item.name === "Lucide / Feather"));
  assert.ok(runtime.items.some((item) => item.name === "Microsoft WebView2"));
  assert.match(runtime.items.find((item) => item.name.includes("SQLitePCLRaw"))!.note, /Apache-2\.0/);
  const build = groups.find((group) => group.title === copy.credits.groups.build)!;
  assert.ok(build.items.some((item) => item.name === "Vite / vite-plugin-solid"));
  assert.ok(build.items.some((item) => item.name === "Zig"));
  const external = groups.find((group) => group.title === copy.credits.groups.hardware)!;
  assert.ok(external.items.some((item) => item.name === "Windows Performance Toolkit"));
  assert.ok(external.items.some((item) => item.name === "MSI Afterburner"));
  for (const item of all) {
    for (const link of item.links) {
      assert.equal(new URL(link.href).protocol, "https:");
    }
  }
}
process.stdout.write("PASS credits: dependency closure, full licenses, two locales, six groups and links\n");
