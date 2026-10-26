import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");

const layout = readSource("styles/layout.css");
const theme = readSource("styles/theme.css");
const base = readSource("styles/base.css");
const documentAppearance = readSource("presentation/documentAppearance.ts");
const resourceTable = readSource("features/resourceTable/ResourceTable.tsx");
const resourceBreakdown = readSource("features/resourceBreakdown/ResourceBreakdown.tsx");
const dashboard = readSource("features/monitor/Dashboard.tsx");
const resourcePerformancePanel = readSource("features/resourceTable/ResourcePerformancePanel.tsx");
const monitorPage = readSource("features/monitor/MonitorPage.tsx");
const app = readSource("App.tsx");
const appFeedback = readSource("components/AppFeedback.tsx");
const modalFocus = readSource("interactions/modalFocus.ts");
const inlineEditorFocus = readSource("interactions/inlineEditorFocus.ts");
const softwareDetailModal = readSource("components/SoftwareDetailModal.tsx");
const standardSelect = readSource("components/StandardSelect.tsx");
const standardContextMenu = readSource("components/StandardContextMenu.tsx");
const responsive = readSource("styles/responsive.css");
const taskCenterDialog = readSource("ui/task-center/TaskCenterDialog.tsx");
const dialogPrimitive = readSource("ui/primitives/Dialog.tsx");
const tabsPrimitive = readSource("ui/primitives/Tabs.tsx");
const rovingFocus = readSource("ui/primitives/RovingFocus.ts");
const radioGroup = readSource("ui/primitives/RadioGroup.tsx");
const segmentedControl = readSource("ui/primitives/SegmentedControl.tsx");
const confirmDialog = readSource("ui/patterns/ConfirmDialog.tsx");
const pageBoundary = readSource("ui/patterns/PageBoundary.tsx");
const userDetailsDialog = readSource("components/UserDetailsDialog.tsx");
const metricModal = readSource("features/monitor/MetricModal.tsx");
const manualSoftwareModal = readSource("features/management/components/ManualSoftwareModal.tsx");
const managementPage = readSource("features/management/components/ManagementPage.tsx");
const cpuTopology = readSource("features/details/CpuTopologyDiagram.tsx");
const appRoutes = readSource("app/AppRoutes.tsx");
const appShell = readSource("app/AppShell.tsx");
const settingsControls = readSource("features/settings/components/SettingsControls.tsx");
const aiGatewayKeyManager = readSource("features/settings/components/AiGatewayKeyManager.tsx");
const settingsPage = readSource("features/settings/SettingsPage.tsx");
const controls = readSource("styles/controls.css");
const dividerConsumerStyles = [
  readSource("styles/browser-runtime.css"),
  readSource("styles/device-topology.css"),
  readSource("styles/feedback.css"),
  readSource("styles/management.css"),
  readSource("styles/monitor.css"),
  readSource("styles/panels.css"),
  readSource("styles/resources.css"),
  readSource("styles/responsive.css"),
  readSource("styles/settings.css"),
  readSource("styles/shell.css"),
  readSource("styles/software-detail-modal.css"),
  readSource("styles/task-center.css"),
  readSource("styles/user-details.css"),
  readSource("styles/context-menu.css"),
  readSource("features/deviceTopology/components/specialized-device-details.css")
].join("\n");
const resourceSegmentPaintPlane = readSource("features/resourceBreakdown/ResourceSegmentPaintPlane.tsx");
const themeConsumerStyles = [
  readSource("styles/settings.css"),
  readSource("styles/management.css"),
  readSource("styles/device-topology.css"),
  readSource("styles/software-detail-modal.css")
].join("\n");
const settingsSources = [
  readSource("features/settings/components/SystemIntegrationSettingsSection.tsx"),
  readSource("features/settings/components/DebugSettingsSection.tsx"),
  readSource("features/settings/components/EditableHotkeyEditor.tsx")
];

assert.doesNotMatch(theme, /--workspace-min-width/);
// classic 的配色在 theme.css，standard 的在 ui-styles/standard.css；分隔线一律写成 display-p3（theme-consistency 旅程按通道读取）。
const standardStyle = readSource("styles/ui-styles/standard.css");
assert.match(theme, /:root\[data-ui-style="classic"\]\[data-theme="dark"\]/);
assert.match(theme, /:root\[data-ui-style="classic"\]\[data-theme="lowContrast"\]/);
assert.match(theme, /--divider:\s*color\(display-p3/);
assert.match(theme, /--divider-soft:\s*color\(display-p3/);
assert.match(standardStyle, /:root\[data-ui-style="standard"\]\[data-theme="dark"\]/);
assert.match(standardStyle, /:root\[data-ui-style="standard"\]\[data-theme="lowContrast"\]/);
assert.match(standardStyle, /--divider:\s*color\(display-p3/);
assert.match(standardStyle, /--divider-soft:\s*color\(display-p3/);
assert.doesNotMatch(theme, /body\[data-theme=/);
assert.match(base, /html,\s*\nbody,\s*\n#root\s*\{[\s\S]*?background:\s*var\(--bg\);/);
assert.match(documentAppearance, /document\.documentElement/);
assert.match(documentAppearance, /document\.body\.removeAttribute\("data-theme"\)/);
assert.doesNotMatch(app, /document\.body\.dataset\.theme/);
assert.doesNotMatch(
  themeConsumerStyles,
  /var\(--(?:warning|text-secondary|border)\s*[,)]/,
  "Theme consumers must use defined canonical semantic tokens."
);
assert.doesNotMatch(
  dividerConsumerStyles,
  /border-(?:top|bottom):\s*[^;]*var\(--line(?:-soft)?\)/,
  "Internal top and bottom separators must not reuse structural border tokens."
);
assert.match(
  dividerConsumerStyles,
  /\.standard-context-menu-separator\s*\{[\s\S]*?background:\s*var\(--divider-soft\);/
);
assert.match(resourceSegmentPaintPlane, /const dividerColor = "var\(--divider\)";/);
assert.match(layout, /\.app-shell\s*\{[\s\S]*?overflow-x:\s*hidden;/);
assert.match(layout, /\.app-shell\s*>\s*\*,\s*\.page-stage\s*>\s*\*\s*\{[\s\S]*?width:\s*100%;[\s\S]*?min-width:\s*0;/);

for (const semanticMarker of [
  'role="treegrid"',
  'role="rowgroup"',
  'role="row"',
  'role="columnheader"',
  'role="gridcell"',
  "aria-level=",
  "aria-expanded=",
  "aria-rowcount=",
  "aria-rowindex=",
  "aria-colcount=",
  "aria-colindex=",
  "aria-sort="
]) {
  assert.ok(resourceTable.includes(semanticMarker), `Missing resource table marker: ${semanticMarker}`);
}
// 行可以展开（软件行下挂进程行），table 的行不允许 aria-expanded / aria-level，所以整张表是 treegrid。
assert.doesNotMatch(resourceTable, /role="table"|role="cell"|role="grid"(?!cell)/);
assert.match(resourceTable, /tabIndex=\{hasRow\(\) && row\(\)\?\.id === props\.activeRowId\(\) \? 0 : -1\}/);
assert.match(resourceTable, /event\.key === "ArrowUp"/);
assert.match(resourceTable, /event\.key === "ArrowDown"/);
assert.match(resourceTable, /event\.key === "Home"/);
assert.match(resourceTable, /event\.key === "End"/);
assert.match(resourceTable, /event\.key === "Enter"/);
assert.match(resourceTable, /data-resource-row-id=\{row\(\)\?\.id\}/);
assert.match(resourceTable, /element\?\.focus\(\{ preventScroll: true \}\)/);
assert.match(appFeedback, /<ConfirmDialog/);
assert.match(modalFocus, /document\.addEventListener\("keydown", handleKeyDown, true\)/);
assert.match(modalFocus, /event\.key === "Escape"/);
assert.match(modalFocus, /event\.key !== "Tab"/);
assert.match(modalFocus, /child\.inert = true/);
assert.match(modalFocus, /initialFocus/);
assert.match(modalFocus, /restoreFocusTarget\(opener, openerFocusKey\)/);
// Portal 里的对话框也要把应用根设为 inert：从遮罩一路往上到 body，每层的兄弟都设 inert。
assert.match(modalFocus, /kept !== document\.body/);
for (const modalConsumer of [
  softwareDetailModal,
  userDetailsDialog,
  metricModal,
  manualSoftwareModal,
  confirmDialog
]) {
  assert.match(modalConsumer, /<DialogRoot/);
}
assert.match(confirmDialog, /dismissOnBackdrop=\{false\}/);
assert.match(confirmDialog, /initialFocus=\{\(\) => cancelButton\}/);
assert.match(confirmDialog, /interface ConfirmDialogRequest[\s\S]*?title: string;/);
assert.doesNotMatch(confirmDialog.match(/interface ConfirmDialogRequest[\s\S]*?\n\}/)?.[0] ?? "", /id: string/);
assert.match(confirmDialog, /interface ConfirmDialogRecord extends ConfirmDialogRequest/);
assert.match(manualSoftwareModal, /dismissOnBackdrop=\{false\}/);
assert.match(resourceTable, /class="resource-row-actions"/);
assert.match(
  resourceTable,
  /event\.key === "ContextMenu"[\s\S]*?event\.shiftKey && event\.key === "F10"/
);
assert.match(
  resourceTable,
  /class="resource-row-actions"[\s\S]*?onKeyDown=\{\(event\)[\s\S]*?event\.stopPropagation\(\);[\s\S]*?onContextMenu=/
);
assert.match(resourceTable, /onContextMenu=\{\(event\) => \{\s*event\.preventDefault\(\);\s*event\.stopPropagation\(\);\s*openRowActions/);
assert.match(standardContextMenu, /event\.key === "ArrowDown" \|\| event\.key === "ArrowUp"/);
assert.match(standardContextMenu, /returnFocusTarget\?: HTMLElement \| null/);
assert.match(standardContextMenu, /interface ContextMenuFocusSession/);
assert.match(standardContextMenu, /closeMenu\("return-focus"\)/);
assert.match(standardContextMenu, /scheduleFocusRestore\(/);
// 菜单唯一的生产者保证至少一项可执行（目标必带软件标识，“转到详细信息”恒可用），
// “全部禁用只弹提示”的分支没有调用方能走到，已删除，不能再长回来。
assert.doesNotMatch(standardContextMenu, /onUnavailable|unavailableReason|hasEnabledMenuItem/);
const softwareContextMenu = readSource("components/SoftwareContextMenu.tsx");
assert.match(softwareContextMenu, /^\s+softwareId: string;\r?$/m);
assert.match(softwareContextMenu, /id: "details",\s*label: uiText\.contextMenu\.goToDetails,\s*separatorBefore: true,\s*onSelect:/);
assert.match(standardSelect, /event\.key === "Tab"/);
assert.match(standardSelect, /tabIndex=\{!props\.searchable && index === activeIndex\(\) \? 0 : -1\}/);
// 列表框里只放选项和选项组：组标题在 role="group" 里并给组命名，空状态在列表框外面。
assert.match(standardSelect, /role="group" aria-labelledby=\{`\$\{instanceId\}-group-\$\{run\.start\}`\}/);
assert.match(standardSelect, /id=\{`\$\{instanceId\}-group-\$\{run\.start\}`\} class="standard-select-group" role="presentation"/);
assert.match(standardSelect, /<\/div>\s*\{\/\* 空状态不是选项，放在列表框外面。 \*\/\}\s*<Show when=\{visibleOptions\(\)\.length === 0\}>/);
assert.match(
  settingsPage,
  /canRenderSettings = \(\) => props\.loadState === "ready"[\s\S]*?props\.loadState === "refreshing"[\s\S]*?props\.loadState === "stale"/,
  "an explicit refresh must keep the last-good settings visible"
);
assert.match(settingsPage, /aria-busy=\{props\.loadState === "loading" \|\| props\.loadState === "refreshing"\}/);
assert.match(settingsPage, /class="settings-readonly-boundary"[\s\S]*?disabled=\{!settingsReady\(\)\}/);
assert.match(settingsPage, /disabled=\{props\.saveState === "saving" \|\| props\.isDirty\}[\s\S]*?onClick=\{props\.onReload\}/);
assert.doesNotMatch(responsive, /\.window-drag-region\s*\{\s*display:\s*none;/);
assert.match(responsive, /\.window-drag-region\s*\{[\s\S]*?right:\s*144px;[\s\S]*?width:\s*32px;/);
assert.match(responsive, /\.shell-utility-actions\s*\{[\s\S]*?right:\s*108px;/);
assert.match(dialogPrimitive, /useModalFocus\(/);
assert.match(dialogPrimitive, /role="presentation"[\s\S]*?role="dialog"[\s\S]*?tabIndex=\{-1\}/);
assert.match(dialogPrimitive, /props\.dismissOnBackdrop !== false/);
assert.match(tabsPrimitive, /role="tablist"/);
assert.match(tabsPrimitive, /role="tab"/);
assert.match(tabsPrimitive, /role="tabpanel"/);
assert.match(tabsPrimitive, /hidden=\{!selected\(\)\}/);
assert.match(tabsPrimitive, /<Show when=\{selected\(\)\}>/);
assert.match(tabsPrimitive, /moveRovingFocus/);
assert.match(tabsPrimitive, /previousKeys: \["ArrowLeft"\]/);
assert.match(tabsPrimitive, /nextKeys: \["ArrowRight"\]/);
assert.match(rovingFocus, /event\.key === "Home"/);
assert.match(rovingFocus, /event\.key === "End"/);
assert.match(rovingFocus, /focus\(\{ preventScroll: true \}\)/);
assert.match(radioGroup, /role="radiogroup"/);
assert.match(radioGroup, /role="radio"/);
assert.match(radioGroup, /aria-checked=\{selected\(\)\}/);
// 唯一的 Tab 停靠项：选中且可用的项，否则第一个可用项（没有选中项时组也要能被 Tab 到）。
assert.match(radioGroup, /tabIndex=\{context\.tabStopValue\(\) === value \? 0 : -1\}/);
assert.match(radioGroup, /enabled\.find\(\(item\) => item\.value === props\.value\) \?\? enabled\[0\]/);
assert.match(segmentedControl, /<RadioGroupRoot/);
assert.match(settingsControls, /<PrimitiveSegmentedControl/);
assert.match(aiGatewayKeyManager, /<SegmentedControl/);
assert.match(resourceTable, /<SegmentedControl/);
assert.doesNotMatch(resourceTable, /resource-table-mode-switch" role="tablist"/);
// 二级分页条由外壳按分页树画（docs/navigation_tree_2026-10-25.md）：一组页签，内容区是它的面板；页面自己不画分页条。
assert.match(appShell, /<TabsList class="page-subnav-tabs" ariaLabel=\{props\.navigation\.label\(\)\}>/);
assert.match(appShell, /controls="pageStage"/);
assert.match(appShell, /role=\{secondary\(\) \? "tabpanel" : undefined\}/);
assert.doesNotMatch(managementPage, /page-subnav|<TabsRoot|role="tab"/);
assert.match(softwareDetailModal, /<TabsRoot/);
assert.match(pageBoundary, /<ErrorBoundary/);
assert.match(pageBoundary, /role="alert"/);
assert.match(pageBoundary, /tabIndex=\{-1\}/);
assert.match(pageBoundary, /<Show when=\{props\.onRetry\}>/);
assert.match(pageBoundary, /props\.resetKey/);
// 六个一级分页各有自己的错误边界。
assert.equal(appRoutes.match(/<PageBoundary name=/g)?.length, 7);
assert.match(appShell, /<PageBoundary name=\{uiText\.shell\.currentPage\} resetKey=\{props\.activePage\(\)\}>/);
assert.match(appShell, /document\.title = uiText\.shell\.documentTitle\(props\.pageTitle\(\), uiText\.shell\.productName\)/);
assert.match(appShell, /<h1[\s\S]*?id="activePageTitle"[\s\S]*?tabIndex=\{-1\}/);
assert.match(appShell, /pageHeading\?\.focus\(\{ preventScroll: true \}\)/);
assert.match(appShell, /aria-labelledby="activePageTitle"/);
const captureTimeElement = appShell.match(/<span[\s\S]*?id="captureTime"[\s\S]*?<\/span>/)?.[0] ?? "";
assert.ok(captureTimeElement, "The capture-time element must remain present.");
assert.doesNotMatch(captureTimeElement, /role=|aria-live=/,
  "The once-per-second capture clock must not be a live region.");
assert.match(taskCenterDialog, /visibility:\s*"diagnostics"/);
assert.match(taskCenterDialog, /props\.projection\.cancel\(item\.id\)/);
assert.doesNotMatch(taskCenterDialog, /onClose[\s\S]{0,120}cancel\(/);
assert.doesNotMatch(app, /document\.documentElement\.(lang|dir)/);
assert.match(settingsPage, /lang=\{text\(\)\.language\}/);
assert.match(settingsPage, /dir=\{isRightToLeftLanguage\(text\(\)\.language\)/);
assert.match(settingsPage, /<nav ref=\{nav\} class="settings-nav has-selection-indicator" aria-label=\{text\(\)\.navigationLabel\}>/);
assert.doesNotMatch(settingsPage, /<aside class="settings-nav"/);
assert.match(cpuTopology, /function CpuCoreSelectionButton/);
assert.match(cpuTopology, /class=\{props\.variant === "ring" \? "cpu-ring-core-main" : "cpu-core-main"\}/);
assert.match(cpuTopology, /aria-pressed=\{props\.selected\}/);
assert.doesNotMatch(cpuTopology, /role="button"/);
assert.match(cpuTopology, /disabled=\{!logical\.affinitySelectable\}/);
assert.match(
  cpuTopology,
  /<CpuCoreSelectionButton[\s\S]*?\/>\s*<Show when=\{editing\(\)\}>\s*<CpuScoreInput/,
  "CPU score inputs must be siblings of the core selection button"
);
assert.match(
  cpuTopology,
  /function CpuScoreInput[\s\S]*?<label class="cpu-score-field">\s*<span>\{uiText\.cpuTopology\.scoreInputLabel\}<\/span>\s*<input[\s\S]*?aria-label=\{uiText\.cpuTopology\.setCoreScore\(coreName\(props\.core\)\)\}/,
  "CPU score inputs keep a visible label and a per-core accessible name"
);

let settingsSwitchCount = 0;
for (const source of settingsSources) {
  for (const match of source.matchAll(/<SwitchControl\b([\s\S]*?)\/>/g)) {
    settingsSwitchCount += 1;
    assert.match(match[1], /\bariaLabel=/, "Every settings switch must have an accessible name.");
  }
}
assert.ok(settingsSwitchCount > 0, "The settings sources still render their switches through SwitchControl.");

assert.match(resourceTable, /type="search"[\s\S]*?aria-label=/);
assert.match(monitorPage, /<ObservationStateNotice[\s\S]*?label=\{uiText\.monitorPage\.metricCatalog\}[\s\S]*?presentation="blocking-only"/);
assert.doesNotMatch(monitorPage, /实时资源数据仍会继续显示，布局编辑暂不可用。/);
assert.match(monitorPage, /disabled=\{!dashboardAvailable\(\) \|\| monitor\.dashboardSaveState\(\) === "saving"\}/);
assert.match(monitorPage, /onCleanup\(monitor\.cancelAllMonitorEdits\)/);
assert.match(monitorPage, /onClick=\{monitor\.cancelDashboardEdit\}/);
assert.match(inlineEditorFocus, /firstInlineEditorControl/);
assert.match(inlineEditorFocus, /focusTarget\?\.focus\(\{ preventScroll: true \}\)/);
assert.match(monitorPage, /aria-label=\{uiText\.monitorPage\.cancelEditLabel\}/);
assert.match(resourceBreakdown, /aria-label=\{uiText\.resourceBreakdownView\.editorLabel\}/);
assert.match(resourceTable, /aria-label=\{uiText\.resourceTableView\.columnEditorLabel\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.addSlot\(ordinal\(\)\)\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.resizeSlot\(slotLabel\(\), metricLabel\(\)\)\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.replaceMetric\(slotLabel\(\), metricLabel\(\)\)\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.removeMetric\(slotLabel\(\), metricLabel\(\)\)\}/);
assert.match(metricModal, /role="combobox"/);
assert.match(metricModal, /role="listbox"/);
assert.match(metricModal, /role="option"/);
assert.match(metricModal, /aria-selected=\{isSelected\(metric\.id\)\}/);
assert.match(metricModal, /aria-multiselectable=\{props\.multiSelect \|\| undefined\}/);
assert.match(metricModal, /userFacingMetricGroup\(metric\.group\)/);
assert.match(metricModal, /resolveActiveDescendantTarget/);
assert.match(controls, /:where\(button:not\(:disabled\)\):hover/);
assert.doesNotMatch(controls, /^button:not\(:disabled\):hover/m,
  "The shared button hover must stay below component .x:hover specificity.");
assert.match(controls, /button:disabled:disabled[\s\S]*?--control-disabled-bg/);
assert.match(resourceTable, /observation=\{props\.metricObservation\}/);
assert.match(monitorPage, /metricObservation=\{monitor\.snapshotObservation\(\)\}/);
assert.match(resourcePerformancePanel, /class="resource-performance-summary"/);
assert.match(resourcePerformancePanel, /role="status"/);
assert.match(resourcePerformancePanel, /point\(\)\.displays\[item\.id\]/);
assert.doesNotMatch(
  monitorPage,
  /<ObservationStateBoundary[\s\S]{0,240}state=\{monitor\.catalogObservation\(\)\}/,
  "catalog availability must not gate the whole monitor page"
);

// 读屏不读没有角色的 div/span 上的 aria-label：带名字的通用元素必须同时给角色（group、region、img 等）。
// 交互协议引擎 interactions/protocol 是原样拷贝的外部代码，不在扫描范围内。
{
  const sourceRoot = fileURLToPath(new URL("../src/", import.meta.url));
  const pending = [sourceRoot];
  const unnamedRoles: string[] = [];
  const genericTag = /<(div|span|p|li|ul|strong|em|small|header|footer|canvas|svg|figure)\b((?:[^<>{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)>/gs;
  let visitedDirectories = 0;
  while (pending.length > 0) {
    const directory = pending.pop()!;
    visitedDirectories += 1;
    assert.ok(visitedDirectories < 2000, "source scan exceeded its directory limit");
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        if (entry.name !== "protocol" && entry.name !== "generated") pending.push(path);
        continue;
      }
      if (!entry.name.endsWith(".tsx")) continue;
      const source = readFileSync(path, "utf8");
      for (const match of source.matchAll(genericTag)) {
        if (/\baria-label(?:ledby)?=/.test(match[2]) && !/\brole=/.test(match[2])) {
          const line = source.slice(0, match.index).split("\n").length;
          unnamedRoles.push(`${relative(sourceRoot, path)}:${line} <${match[1]}>`);
        }
      }
    }
  }
  assert.deepEqual(unnamedRoles, [], "generic elements with aria-label need a role");
}
