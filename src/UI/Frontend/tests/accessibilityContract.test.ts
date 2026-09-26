import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");

const layout = readSource("styles/layout.css");
const theme = readSource("styles/theme.css");
const base = readSource("styles/base.css");
const documentTheme = readSource("presentation/documentTheme.ts");
const resourceTable = readSource("features/resourceTable/ResourceTable.tsx");
const resourceBreakdown = readSource("features/resourceBreakdown/ResourceBreakdown.tsx");
const dashboard = readSource("features/monitor/Dashboard.tsx");
const resourcePerformancePanel = readSource("components/ResourcePerformancePanel.tsx");
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
const manualSoftwareModal = readSource("components/ManualSoftwareModal.tsx");
const managementPage = readSource("features/management/components/ManagementPage.tsx");
const cpuTopology = readSource("components/CpuTopologyDiagram.tsx");
const appRoutes = readSource("app/AppRoutes.tsx");
const appShell = readSource("app/AppShell.tsx");
const settingsControls = readSource("features/settings/components/SettingsControls.tsx");
const aiGatewayKeyManager = readSource("components/AiGatewayKeyManager.tsx");
const settingsPage = readSource("features/settings/SettingsPage.tsx");
const controls = readSource("styles/controls.css");
const dividerConsumerStyles = [
  readSource("styles/browser-runtime.css"),
  readSource("styles/device-topology.css"),
  readSource("styles/feedback.css"),
  readSource("styles/management.css"),
  readSource("styles/migration.css"),
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
assert.match(theme, /:root\[data-theme="dark"\]/);
assert.match(theme, /:root\[data-theme="lowContrast"\]/);
assert.match(theme, /--divider:\s*color\(display-p3/);
assert.match(theme, /--divider-soft:\s*color\(display-p3/);
assert.doesNotMatch(theme, /body\[data-theme=/);
assert.match(base, /html,\s*\nbody,\s*\n#root\s*\{[\s\S]*?background:\s*var\(--bg\);/);
assert.match(documentTheme, /document\.documentElement/);
assert.match(documentTheme, /document\.body\.removeAttribute\("data-theme"\)/);
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
assert.match(layout, /\.app-shell\s*>\s*\*\s*\{[\s\S]*?width:\s*100%;[\s\S]*?min-width:\s*0;/);

for (const semanticMarker of [
  'role="table"',
  'role="rowgroup"',
  'role="row"',
  'role="columnheader"',
  'role="cell"',
  "aria-rowcount=",
  "aria-rowindex=",
  "aria-colcount=",
  "aria-colindex=",
  "aria-sort="
]) {
  assert.ok(resourceTable.includes(semanticMarker), `Missing resource table marker: ${semanticMarker}`);
}
assert.doesNotMatch(resourceTable, /role="grid"|role="gridcell"/);
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
assert.match(modalFocus, /focusTarget\.focus\(\{ preventScroll: true \}\)/);
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
assert.match(standardContextMenu, /hasEnabledMenuItem\(props\.model\.items\) \? props\.model : null/);
assert.match(standardContextMenu, /props\.onUnavailable\?\.\(model\.unavailableReason/);
assert.match(standardSelect, /event\.key === "Tab"/);
assert.match(standardSelect, /tabIndex=\{!props\.searchable && index\(\) === activeIndex\(\) \? 0 : -1\}/);
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
assert.match(radioGroup, /tabIndex=\{selected\(\) && !disabled\(\) \? 0 : -1\}/);
assert.match(segmentedControl, /<RadioGroupRoot/);
assert.match(settingsControls, /<PrimitiveSegmentedControl/);
assert.match(aiGatewayKeyManager, /<SegmentedControl/);
assert.match(resourceTable, /<SegmentedControl/);
assert.doesNotMatch(resourceTable, /resource-table-mode-switch" role="tablist"/);
assert.match(managementPage, /<nav class="management-tabs" aria-label=\{uiText\.management\.categoryNav\}>/);
assert.match(managementPage, /aria-current=\{props\.activeSubpage === kind\.id \? "page" : undefined\}/);
assert.doesNotMatch(managementPage, /<TabsRoot|role="tab"|role="tabpanel"/);
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
assert.match(settingsPage, /<nav class="settings-nav" aria-label=\{text\(\)\.navigationLabel\}>/);
assert.doesNotMatch(settingsPage, /<aside class="settings-nav"/);
assert.match(cpuTopology, /function CpuCoreSelectionButton/);
assert.match(cpuTopology, /class=\{props\.variant === "ring" \? "cpu-ring-core-main" : "cpu-core-main"\}/);
assert.match(cpuTopology, /aria-pressed=\{props\.selected\}/);
assert.doesNotMatch(cpuTopology, /role="button"/);
assert.match(cpuTopology, /disabled=\{!logical\.affinitySelectable\}/);
assert.match(
  cpuTopology,
  /<CpuCoreSelectionButton[\s\S]*?\/>\s*<Show when=\{editing\(\)\}>\s*<input/,
  "CPU score inputs must be siblings of the core selection button"
);

for (const source of settingsSources) {
  for (const match of source.matchAll(/<label class="settings-switch">([\s\S]*?)<\/label>/g)) {
    assert.match(match[1], /<input[\s\S]*?aria-label=/, "Every settings switch must have an accessible name.");
  }
}

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
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.addMetric\(slotLabel\(\)\)\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.replaceMetric\(slotLabel\(\), metricLabel\(\)\)\}/);
assert.match(dashboard, /aria-label=\{uiText\.dashboard\.removeMetric\(slotLabel\(\), metricLabel\(\)\)\}/);
assert.match(metricModal, /role="combobox"/);
assert.match(metricModal, /role="listbox"/);
assert.match(metricModal, /role="option"/);
assert.match(metricModal, /aria-selected=\{props\.selectedMetricId === metric\.id\}/);
assert.match(metricModal, /userFacingMetricGroup\(metric\.group\)/);
assert.match(metricModal, /resolveActiveDescendantTarget/);
assert.match(controls, /button:not\(:disabled\):hover/);
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
