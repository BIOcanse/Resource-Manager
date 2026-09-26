import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const readSource = (relativePath: string) =>
  readFileSync(new URL(`../src/${relativePath}`, import.meta.url), "utf8");

const appShell = readSource("app/AppShell.tsx");
const optimization = readSource("components/OptimizationPage.tsx");
const details = readSource("components/DetailsPage.tsx");
const settings = readSource("features/settings/SettingsPage.tsx");
const topology = readSource("components/DeviceTopologyView.tsx");
const aiGateway = readSource("components/AiGatewayKeyManager.tsx");
const cpuTopology = readSource("components/CpuTopologyDiagram.tsx");
const softwareDetail = readSource("components/SoftwareDetailModal.tsx");
const hotkeyEditor = readSource("features/settings/components/EditableHotkeyEditor.tsx");
const segmentedControl = readSource("ui/primitives/SegmentedControl.tsx");
const radioGroup = readSource("ui/primitives/RadioGroup.tsx");
const tabs = readSource("ui/primitives/Tabs.tsx");

// 顶栏一级分页：监视控制台、组件与软件、性能优化、磁盘占用、控制面、详细信息、设置。
assert.equal(appShell.match(/aria-current=/g)?.length, 7);
assert.equal(optimization.match(/<SegmentedControl/g)?.length, 1);
assert.match(optimization, /aria-pressed=\{selectedOptimizationMode\(\) === "normal"\}/);
assert.match(optimization, /aria-pressed=\{optimizationModeHasDomain\(selectedOptimizationMode\(\), domain\)\}/);
assert.match(details, /<TabsRoot/);
assert.equal(details.match(/<TabsPanel/g)?.length, 4);
assert.match(settings, /aria-current=\{props\.activeSection === section/);
assert.match(topology, /<SegmentedControl/);
assert.equal(topology.match(/aria-pressed=/g)?.length, 1);
assert.match(aiGateway, /<SegmentedControl/);
assert.match(cpuTopology, /aria-pressed=\{item\.id === core\(\)\?\.id\}/);
assert.match(cpuTopology, /aria-pressed=\{isSelected\(selected\(\), "ccd", ccd\.id\)\}/);
assert.match(cpuTopology, /aria-pressed=\{props\.selected\}/);
assert.match(cpuTopology, /aria-pressed=\{isSelected\(selected\(\), "logical", String\(logical\.id\)\)\}/);
assert.doesNotMatch(cpuTopology, /role="button"/);
assert.match(settings, /<nav class="settings-nav"/);
assert.match(softwareDetail, /role="listbox"/);
assert.match(softwareDetail, /role="option"/);
assert.match(softwareDetail, /aria-selected=\{props\.selectedProcessKey === process\.processKey\}/);
assert.match(softwareDetail, /<TabsRoot/);
assert.match(hotkeyEditor, /<RadioGroupRoot/);
assert.equal(hotkeyEditor.match(/<RadioGroupItem/g)?.length, 2);
assert.match(segmentedControl, /<RadioGroupRoot/);
assert.match(radioGroup, /role="radiogroup"/);
assert.match(radioGroup, /role="radio"/);
assert.match(radioGroup, /aria-checked=/);
assert.match(tabs, /role="tablist"/);
assert.match(tabs, /role="tab"/);
assert.match(tabs, /role="tabpanel"/);
