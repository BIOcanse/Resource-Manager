import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

/*
 * 选择项的样子只有两套（用户 10-25「二级分页选择各有各的」）：分页是 styles/page-tabs.css 的 .page-tab，
 * 分段选择是 styles/segmented.css 的 .segmented / .segment。SegmentedControl 自己加类；直接写 RadioGroupRoot、
 * TabsTrigger 或旧类名的地方要自己带上，不然会掉回全局按钮的样子（风扇三选一合入时就漏了，5328f620）。
 * 这里在源码里查：
 * - 直接写的 RadioGroupRoot 带 segmented，它的每个 RadioGroupItem 带 segment；竖排的卡片式选择不是分段，列在下面并写明原因；
 * - 每个 TabsTrigger 带 page-tab；
 * - 静态 class 里写了旧的分段类、分页类的元素也带着对应的标准类。
 */

const src = fileURLToPath(new URL("../src/", import.meta.url));
const cardChoices = new Map([
  ["metric-source-choices", "选指标弹窗里竖排的来源卡片，每项带说明，不是分段"],
  ["control-access-level-choices", "设置里竖排的控制权限卡片，每项带说明，不是分段"]
]);
const legacySegmentItems = ["settings-segment", "resource-table-mode-button", "optimization-mode", "device-scope-tab"];
const legacySegmentGroups = ["settings-segmented-control", "resource-table-mode-switch", "optimization-mode-group", "optimization-report-filter", "settings-hotkey-relation", "device-scope-tabs"];
const legacyPageTabs = ["shell-page", "page-subnav-tab", "settings-nav-item", "ui-tabs-trigger"];

/** src 下的全部 .tsx，显式栈遍历，带节点上限。 */
function sourceFiles(): string[] {
  const files: string[] = [];
  const stack = [src];
  let visited = 0;
  while (stack.length > 0) {
    const directory = stack.pop()!;
    visited += 1;
    assert.ok(visited < 5000, "source tree walk exceeded 5000 directories");
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) stack.push(path);
      else if (name.endsWith(".tsx")) files.push(path);
    }
  }
  return files;
}

/** 从 start（指向 `<`）起的开标签文本：花括号里的 `>` 不算结束。 */
function openingTag(text: string, start: number): string {
  let depth = 0;
  let at = start;
  while (at < text.length) {
    const char = text[at];
    if (char === "{") depth += 1;
    else if (char === "}") depth -= 1;
    else if (char === ">" && depth === 0) return text.slice(start, at + 1);
    at += 1;
  }
  // 不是标签（比较运算等）：读到文件尾，里面不会有 class。
  return text.slice(start);
}

/** 开标签里 class 的文本：静态字符串，或花括号表达式原文（只用来找类名）。 */
function classText(tag: string): string | null {
  const match = /\sclass=(?:"([^"]*)"|\{)/.exec(tag);
  if (!match) return null;
  if (match[1] !== undefined) return match[1];
  let depth = 0;
  let at = match.index + match[0].length - 1;
  const begin = at;
  while (at < tag.length) {
    if (tag[at] === "{") depth += 1;
    else if (tag[at] === "}") {
      depth -= 1;
      if (depth === 0) return tag.slice(begin, at + 1);
    }
    at += 1;
  }
  return null;
}

const tokens = (value: string) => new Set(value.split(/[^A-Za-z0-9_-]+/).filter(Boolean));
const failures: string[] = [];
let radioRoots = 0;
let tabTriggers = 0;

for (const file of sourceFiles()) {
  const text = readFileSync(file, "utf8");
  const where = (index: number) => `${relative(src, file)}:${text.slice(0, index).split("\n").length}`;
  const tags = [...text.matchAll(/<([A-Za-z][A-Za-z0-9.]*)[\s>]/g)];
  let segmentedRoot = false;
  for (const found of tags) {
    const name = found[1];
    // SegmentedControl 自己加类：直接用的和改名导入的（设置里的 PrimitiveSegmentedControl）、包它的设置控件都不查。
    if (name.endsWith("SegmentedControl")) continue;
    const tag = openingTag(text, found.index!);
    const value = classText(tag);
    const present = tokens(value ?? "");
    if (name === "RadioGroupRoot") {
      radioRoots += 1;
      const card = [...cardChoices.keys()].find((choice) => present.has(choice));
      segmentedRoot = present.has("segmented");
      if (!segmentedRoot && !card) failures.push(`${where(found.index!)} RadioGroupRoot without segmented (class: ${value ?? "none"})`);
      continue;
    }
    if (name === "RadioGroupItem" && segmentedRoot && !present.has("segment")) {
      failures.push(`${where(found.index!)} RadioGroupItem in a segmented group without segment`);
    }
    if (name === "TabsTrigger") {
      tabTriggers += 1;
      if (!present.has("page-tab")) failures.push(`${where(found.index!)} TabsTrigger without page-tab`);
    }
    if (value === null || value.startsWith("{")) continue;
    if (legacySegmentItems.some((legacy) => present.has(legacy)) && !present.has("segment")) failures.push(`${where(found.index!)} "${value}" lacks segment`);
    if (legacySegmentGroups.some((legacy) => present.has(legacy)) && !present.has("segmented")) failures.push(`${where(found.index!)} "${value}" lacks segmented`);
    if (legacyPageTabs.some((legacy) => present.has(legacy)) && !present.has("page-tab")) failures.push(`${where(found.index!)} "${value}" lacks page-tab`);
  }
}

assert.ok(radioRoots >= 4, `expected the known RadioGroupRoot sites, found ${radioRoots}`);
assert.ok(tabTriggers >= 3, `expected the known TabsTrigger sites, found ${tabTriggers}`);
assert.deepEqual(failures, [], `Selection items outside the two standard looks:\n${failures.join("\n")}`);
console.log(`PASS selection looks: ${radioRoots} radio groups, ${tabTriggers} tab triggers`);
