import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  browserRuntimeManagementSubpageId,
  isManagementInventorySubpage,
  managementInventoryKinds
} from "../src/features/management/managementNavigation.ts";

assert.equal(browserRuntimeManagementSubpageId, "BrowserRuntime");
assert.deepEqual(managementInventoryKinds, [
  "Dependency",
  "Support",
  "adapted",
  "controlled",
  "Unconfirmed",
  "Unclassified",
  "game",
  "high_performance",
  "other"
]);
assert.equal(managementInventoryKinds.includes("DependencySupport" as never), false);
assert.equal(isManagementInventorySubpage(browserRuntimeManagementSubpageId), false);
for (const kind of managementInventoryKinds) {
  assert.equal(isManagementInventorySubpage(kind), true);
}

const appShell = readSource("../src/app/AppShell.tsx");
const managementNavigation = readSource("../src/app/navigation/managementNavigation.ts");
const managementPage = readSource("../src/features/management/components/ManagementPage.tsx");
const managementItems = readSource("../src/features/management/managementItems.ts");
// 文案已经移到语言包里，中文基底是这项断言的来源。
const managementCopy = readSource("../src/i18n/copy/zh/software.ts");
const workspace = readSource("../src/features/management/ManagementWorkspace.tsx");
const browserRuntimePage = readSource("../src/features/browserRuntimes/BrowserRuntimePage.tsx");
const softwareActions = readSource("../src/app/useSoftwareActions.ts");
const app = readSource("../src/App.tsx");
const responsiveCss = readSource("../src/styles/responsive.css");

// 二级分页条由外壳按分页树画（docs/navigation_tree_2026-10-25.md），组件与软件页只登记清单，不自己画分页条。
assert.match(appShell, /<SecondaryTabs page=\{page\.id\} navigation=\{page\.secondary!\} \/>/);
assert.match(managementNavigation, /export function createManagementNavigation/);
assert.match(managementNavigation, /ids: \[\.\.\.managementInventoryKinds, browserRuntimeManagementSubpageId\]/);
assert.doesNotMatch(managementPage, /ManagementSubpageBar/);
assert.match(workspace, /when=\{activeInventoryKind\(\)\}/);
assert.match(workspace, /<BrowserRuntimePage/);
assert.match(workspace, /FrontendVisibilityDemandBinding/);
assert.match(workspace, /frontendVisibilitySurface/);
assert.match(workspace, /managementBrowserRuntimes/);
assert.doesNotMatch(
  browserRuntimePage,
  /useFrontendVisibilityDemand|FrontendVisibilityDemandBinding|frontendVisibilitySurface/);
assert.match(workspace, /sharedBrowserRuntimeComponentId/);
// 依赖列表去重要用该角色的全部组件名（含只在运行时管理里列的共享运行时），
// 不能只用过滤后的卡片，否则同名软件记录会带着后端原名回到依赖列表。
assert.match(managementItems, /const componentNameSet = new Set\(roleComponents/);
assert.match(browserRuntimePage, /<UserDetailsDialog/);
assert.match(browserRuntimePage, /runtimeDetailSections/);
assert.doesNotMatch(browserRuntimePage, /<code[^>]*runtimeDirectory/);
assert.doesNotMatch(workspace, /MigrationPanel|migrationWorkbenchPage/);
assert.doesNotMatch(workspace, /<details|management-extra-panels/);
assert.doesNotMatch(softwareActions, /openManagementSubpage\("Migration"\)|migrationRestoreCommand/);
assert.match(app, /frontendWorkIds\.managementInventory/);
assert.match(app, /frontendWorkIds\.managementBrowserRuntimes/);
assert.match(responsiveCss, /body\[data-page="components"\] \.page-stage > #componentsPage/);
assert.match(responsiveCss, /body\[data-page="components"\] \.page-stage > #browserRuntimePage/);
assert.match(managementPage, /createEffect\(on\(\(\) => props\.activeKind, \(\) => setSearchQuery\(""\)/);
assert.match(managementPage, /normalizedSearchQuery\(\)[\s\S]*?uiText\.management\.noSearchResults/);
assert.match(managementPage, /uiText\.management\.clearSearch/);
assert.match(managementCopy, /noSearchResults: \(query: string\)/);

// 从卡片提示跳过来也好，自己在列表里点也好，安装一律先开标准获取弹窗。
// 安装器已经在缓存里不是省掉确认的理由 —— 厂商、条款、是否提权只在那个弹窗里说过。
const managementStore = readSource("../src/stores/managementStore.ts");
const installComponentBody = managementStore.slice(
  managementStore.indexOf("async function installComponent("),
  managementStore.indexOf("async function confirmAcquisition("));
assert.ok(installComponentBody.length > 0, "找不到 installComponent");
assert.doesNotMatch(
  installComponentBody,
  /runComponentInstall/,
  "installComponent 不能直接开装，必须先 setAcquisitionRequest 让用户确认");
assert.match(installComponentBody, /installerAvailable[\s\S]*?setAcquisitionRequest/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
