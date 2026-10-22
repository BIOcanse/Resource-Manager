import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createCreditGroups } from "../src/i18n/settingsCredits.ts";
import { createSettingsLocale } from "../src/i18n/settingsLocaleFactory.ts";

const appRoot = new URL("../../../../src/Core/", import.meta.url);
const notices = new URL("ThirdPartyNotices/", appRoot);
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
const assetsMapPath = process.env.RM_MANAGED_ASSETS_MAP;
const assetsMap = assetsMapPath === undefined ? undefined : (() => {
  assert.ok(isAbsolute(assetsMapPath), "Explicit managed-assets map must be absolute");
  return JSON.parse(readFileSync(assetsMapPath, "utf8")) as { schema: string; projects: Array<{ projectPath: string; assetsPath: string; sha256: string }> };
})();
if (assetsMap) {
  assert.equal(assetsMap.schema, "rm/frontend-credit-assets-map/v1");
  assert.equal(assetsMap.projects.length, 11, "The complete original eleven-project inventory is required");
  assert.equal(new Set(assetsMap.projects.map(row => resolve(row.projectPath).toLowerCase())).size, 11);
}
for (const [path, project] of [
  ["obj/project.assets.json", "ResourceManager.App.csproj"],
  ["../../src/UI/Core/obj/project.assets.json", "../../src/UI/Core/ResourceManager.NativeUi.csproj"],
  ["../../src/Shared/obj/project.assets.json", "../../src/Shared/ResourceManager.Shared.csproj"],
  ["../../src/UI/Launcher/obj/project.assets.json", "../../src/UI/Launcher/ResourceManager.Launcher.csproj"],
  ["../../tests/App/obj/project.assets.json", "../../tests/App/Resource Manager-APP.Tests.csproj"],
  ["../../src/Installer/obj/project.assets.json", "../../src/Installer/ResourceManager.Installer.csproj"],
  ["../../src/Updater/obj/project.assets.json", "../../src/Updater/ResourceManager.Updater.csproj"],
  ["../../src/Updater/WinUI/obj/project.assets.json", "../../src/Updater/WinUI/ResourceManager.UpdateManager.csproj"],
  ["../../src/Uninstaller/obj/project.assets.json", "../../src/Uninstaller/ResourceManager.Uninstaller.csproj"],
  ["../../sdk/csharp/ResourceManager.Adapter.Abstractions/obj/project.assets.json", "../../sdk/csharp/ResourceManager.Adapter.Abstractions/ResourceManager.Adapter.Abstractions.csproj"],
  ["../../sdk/csharp/ResourceManager.Adapter.SharedMemory/obj/project.assets.json", "../../sdk/csharp/ResourceManager.Adapter.SharedMemory/ResourceManager.Adapter.SharedMemory.csproj"]
]) {
  const expectedProject = fileURLToPath(new URL(project, appRoot));
  const mapped = assetsMap?.projects.find(row => resolve(row.projectPath).toLowerCase() === resolve(expectedProject).toLowerCase());
  if (assetsMap) assert.ok(mapped, `Missing explicit assets for ${expectedProject}`);
  if (mapped) {
    assert.ok(isAbsolute(mapped.projectPath) && isAbsolute(mapped.assetsPath), "Mapped project and assets must both be absolute");
    assert.match(mapped.sha256, /^[0-9a-f]{64}$/i);
  }
  const assetsBytes = readFileSync(mapped?.assetsPath ?? new URL(path, appRoot));
  if (mapped) assert.equal(createHash("sha256").update(assetsBytes).digest("hex").toUpperCase(), mapped.sha256.toUpperCase(), "Mapped actual assets hash changed");
  const assets = JSON.parse(assetsBytes.toString("utf8"));
  assert.ok(isAbsolute(assets.project.restore.projectPath), "Assets must identify their absolute original project");
  assert.equal(resolve(assets.project.restore.projectPath).toLowerCase(), resolve(expectedProject).toLowerCase(), "Assets must belong to the exact original project");
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
  assert.ok(all.every((item) => item.name !== atob("S2luZ3M=")));
  assert.ok(all.flatMap(item => item.links).every(link => !/star|点星|點星/i.test(link.label)));
  // 同一项里的链接文字必须能区分（不能出现“官网 官网”）。
  for (const item of all) {
    assert.equal(new Set(item.links.map((link) => link.label)).size, item.links.length,
      `${language}: duplicate link labels in ${item.name}`);
  }
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
  assert.ok(external.items.some((item) => item.name === "PawnIO / PawnIOLib"));
  assert.match(external.items.find((item) => item.name === "PawnIO.Modules")!.note, /LGPL-2\.1/);
  assert.match(external.items.find((item) => item.name === "LibreHardwareMonitorLib")!.note, /MPL-2\.0/);
  // 核心库的传递依赖各按自己的许可致谢，不并入 LHM/MPL。
  assert.match(external.items.find((item) => item.name === "DiskInfoToolkit / RAMSPDToolkit-NDD / BlackSharp.Core")!.note, /MPL-2\.0/);
  assert.match(external.items.find((item) => item.name === "HidSharp")!.note, /Apache-2\.0/);
  assert.match(external.items.find((item) => item.name === "Mono.Posix.NETStandard")!.note, /MIT/);
  // 只用核心的依赖：包装程序（GUI、外部工具、厂商 SDK）不再出现在致谢里。
  for (const name of ["Windows Performance Toolkit", "MSI Afterburner", "LatencyMon", "Intel PCM",
    "Notebook FanControl", "AMD Ryzen Master Monitoring SDK", "OpenHardwareMonitor"]) {
    assert.ok(!all.some((item) => item.name === name), `Removed wrapper still credited: ${name}`);
  }
  for (const item of all) {
    for (const link of item.links) {
      assert.equal(new URL(link.href).protocol, "https:");
    }
  }
}
process.stdout.write("PASS credits: dependency closure, full licenses, two locales, six groups and links\n");
