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
  "Adapted",
  "Controlled",
  "Unconfirmed",
  "Game",
  "HighPerformance",
  "Other"
]);
assert.equal(managementInventoryKinds.includes("DependencySupport" as never), false);
assert.equal(isManagementInventorySubpage(browserRuntimeManagementSubpageId), false);
for (const kind of managementInventoryKinds) {
  assert.equal(isManagementInventorySubpage(kind), true);
}

const appShell = readSource("../src/app/AppShell.tsx");
const managementPage = readSource("../src/features/management/components/ManagementPage.tsx");
// 文案已经移到语言包里，中文基底是这项断言的来源。
const managementCopy = readSource("../src/i18n/copy/zh/software.ts");
const workspace = readSource("../src/features/management/ManagementWorkspace.tsx");
const browserRuntimePage = readSource("../src/features/browserRuntimes/BrowserRuntimePage.tsx");
const softwareActions = readSource("../src/app/useSoftwareActions.ts");
const refreshScheduler = readSource("../src/app/usePageRefreshScheduler.ts");
const managementSnapshotCache = readSource("../src/features/management/managementSnapshotCache.ts");
const responsiveCss = readSource("../src/styles/responsive.css");

assert.match(appShell, /ManagementSubpageBar/);
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
assert.match(managementPage, /const componentNameSet = new Set\(roleComponents/);
assert.match(browserRuntimePage, /<UserDetailsDialog/);
assert.match(browserRuntimePage, /runtimeDetailSections/);
assert.doesNotMatch(browserRuntimePage, /<code[^>]*runtimeDirectory/);
assert.doesNotMatch(workspace, /MigrationPanel|migrationWorkbenchPage/);
assert.doesNotMatch(workspace, /<details|management-extra-panels/);
assert.doesNotMatch(softwareActions, /openManagementSubpage\("Migration"\)|migrationRestoreCommand/);
assert.match(refreshScheduler, /options\.frontendWork\.isNeeded\(frontendWorkIds\.managementInventory\)/);
assert.doesNotMatch(refreshScheduler, /setInterval|scheduleNextRefresh/);
assert.match(refreshScheduler, /browserRuntimeManagementSubpageId/);
assert.match(refreshScheduler, /frontendWorkIds\.managementBrowserRuntimes/);
assert.doesNotMatch(refreshScheduler, /management\.activeSubpage\(\) === "Migration"/);
assert.match(managementSnapshotCache, /resource-manager:management-snapshot:v3/);
assert.match(managementSnapshotCache, /const cacheVersion = 3/);
assert.match(managementSnapshotCache, /map\(withoutIssueState\)/);
assert.match(managementSnapshotCache, /issues: undefined/);
assert.doesNotMatch(managementSnapshotCache, /management-snapshot:v[12]/);
assert.match(responsiveCss, /body\[data-page="components"\] \.app-shell > #componentsPage/);
assert.match(responsiveCss, /body\[data-page="components"\] \.app-shell > #browserRuntimePage/);
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
