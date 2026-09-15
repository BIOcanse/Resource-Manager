import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  browserRuntimeManagementSubpageId,
  isManagementInventorySubpage,
  managementInventoryKinds,
  migrationManagementSubpageId
} from "../src/management/managementNavigation.ts";

assert.equal(migrationManagementSubpageId, "Migration");
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
assert.equal(isManagementInventorySubpage(migrationManagementSubpageId), false);
assert.equal(isManagementInventorySubpage(browserRuntimeManagementSubpageId), false);
for (const kind of managementInventoryKinds) {
  assert.equal(isManagementInventorySubpage(kind), true);
}

const appShell = readSource("../src/app/AppShell.tsx");
const managementPage = readSource("../src/components/ManagementPage.tsx");
// 文案已经移到语言包里，中文基底是这项断言的来源。
const managementCopy = readSource("../src/i18n/copy/zh/software.ts");
const workspace = readSource("../src/pages/ManagementWorkspace.tsx");
const browserRuntimePage = readSource("../src/browserRuntimes/BrowserRuntimePage.tsx");
const migrationStore = readSource("../src/stores/migrationStore.ts");
const softwareActions = readSource("../src/app/useSoftwareActions.ts");
const refreshScheduler = readSource("../src/app/usePageRefreshScheduler.ts");
const managementSnapshotCache = readSource("../src/management/managementSnapshotCache.ts");
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
assert.match(browserRuntimePage, /<UserDetailsDialog/);
assert.match(browserRuntimePage, /runtimeDetailSections/);
assert.doesNotMatch(browserRuntimePage, /<code[^>]*runtimeDirectory/);
assert.match(workspace, /id="migrationWorkbenchPage"/);
assert.doesNotMatch(workspace, /<details|management-extra-panels/);
assert.doesNotMatch(migrationStore, /panelOpen|setPanelOpen/);
assert.equal(softwareActions.match(/openManagementSubpage\("Migration"\)/g)?.length, 2);
assert.match(refreshScheduler, /options\.frontendWork\.isNeeded\(frontendWorkIds\.managementInventory\)/);
assert.doesNotMatch(refreshScheduler, /setInterval|scheduleNextRefresh/);
assert.match(refreshScheduler, /browserRuntimeManagementSubpageId/);
assert.match(refreshScheduler, /frontendWorkIds\.managementBrowserRuntimes/);
assert.match(refreshScheduler, /options\.management\.activeSubpage\(\) === "Migration"/);
assert.match(managementSnapshotCache, /resource-manager:management-snapshot:v3/);
assert.match(managementSnapshotCache, /const cacheVersion = 3/);
assert.match(managementSnapshotCache, /map\(withoutIssueState\)/);
assert.match(managementSnapshotCache, /issues: undefined/);
assert.doesNotMatch(managementSnapshotCache, /management-snapshot:v[12]/);
assert.match(responsiveCss, /body\[data-page="components"\] \.app-shell > #componentsPage/);
assert.match(responsiveCss, /body\[data-page="components"\] \.app-shell > #browserRuntimePage/);
assert.match(responsiveCss, /body\[data-page="components"\] \.app-shell > #migrationWorkbenchPage/);
assert.match(managementPage, /createEffect\(on\(\(\) => props\.activeKind, \(\) => setSearchQuery\(""\)/);
assert.match(managementPage, /normalizedSearchQuery\(\)[\s\S]*?uiText\.management\.noSearchResults/);
assert.match(managementPage, /uiText\.management\.clearSearch/);
assert.match(managementCopy, /noSearchResults: \(query: string\)/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
