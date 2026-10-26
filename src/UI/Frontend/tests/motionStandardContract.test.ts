// 动效标准（docs/ui_interaction_kit.md 第 3 节）在样式表里的落点：
// 过渡只写在 motion.css；颜色反馈不按动画档门控（无动画、超高性能下瞬时变色），只有位移类留在 normal；
// 右键菜单和下拉一样有小弹层入场，页内页签内容淡入。
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src");
const stripComments = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, "");
const motionCss = stripComments(readFileSync(join(srcRoot, "styles/motion.css"), "utf8"))
  .replace(/@keyframes[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, "");

type Rule = { selectors: string[]; declarations: Map<string, string> };
const rules: Rule[] = [...motionCss.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((match) => {
  const declarations = new Map<string, string>();
  for (const part of match[2].split(";")) {
    const colon = part.indexOf(":");
    if (colon >= 0) declarations.set(part.slice(0, colon).trim(), part.slice(colon + 1).replace(/\s+/g, " ").trim());
  }
  return { selectors: match[1].split(",").map((selector) => selector.trim()), declarations };
});

function declarationsOf(selector: string): Map<string, string> {
  const found = rules.filter((rule) => rule.selectors.includes(selector));
  assert.ok(found.length > 0, `motion.css has no rule for ${selector}`);
  const merged = new Map<string, string>();
  for (const rule of found) for (const [key, value] of rule.declarations) merged.set(key, value);
  return merged;
}

// 门控在 normal 下的规则只能写位移类（transform、box-shadow 抬起）和过渡，不写颜色。
const colorProperties = ["background", "background-color", "border-color", "color", "fill", "opacity"];
for (const rule of rules) {
  if (!rule.selectors.some((selector) => selector.includes('data-animations="normal"'))) continue;
  for (const property of colorProperties) {
    assert.ok(!rule.declarations.has(property),
      `${rule.selectors.join(", ")} gates the colour ${property} to normal; none/ultra must still show it`);
  }
}

// 卡片和设置行的悬停色各档都有，悬停背景和按钮一样是 fast。
for (const selector of [".management-card:hover", ".settings-row:hover"]) {
  assert.ok(declarationsOf(selector).has("background"), `${selector} keeps its hover background ungated`);
}
for (const selector of [".management-card", ".settings-row"]) {
  assert.match(declarationsOf(selector).get("transition") ?? "", /background var\(--motion-fast\) var\(--ease-standard\)/,
    `${selector} hover background uses --motion-fast`);
}

// 不是 button 的悬停、拖动反馈也有 fast 颜色过渡。
for (const selector of [".resource-table-head-cell", ".resource-table-column-resizer::after", ".cpu-core", ".cpu-ccd", ".settings-credit-link"]) {
  assert.match(declarationsOf(selector).get("transition") ?? "", /background var\(--motion-fast\) var\(--ease-standard\)/,
    `${selector} has the fast colour transition`);
}
assert.equal(declarationsOf(".fan-curve-point").get("transition"), "fill var(--motion-fast) var(--ease-standard)");

// 开关的滑块位置和轨道、滑块颜色都由交互协议的运行时按开度带（styles/settings.css），motion.css 不给它们写过渡。
for (const selector of [".settings-switch .settings-switch-track", ".settings-switch .settings-switch-knob"]) {
  assert.ok(!rules.some((rule) => rule.selectors.includes(selector) && rule.declarations.has("transition")),
    `${selector} follows the switch openness; a CSS transition would lag behind it`);
}

// 小弹层入场：下拉和两种右键菜单同一条。
for (const selector of [".standard-select-popover", ".standard-context-menu", ".disk-usage-context-menu"]) {
  assert.equal(declarationsOf(selector).get("animation"), "motion-rise-in var(--motion-fast) var(--ease-decel)",
    `${selector} enters like the other small popovers`);
}
assert.equal(declarationsOf(".ui-tabs-panel").get("animation"), "motion-fade-in var(--motion-fast) var(--ease-standard)");

// 监视卡片：等首个读数的占位条呼吸，读数到了数值淡入（分区入场的节奏）；之后的更新不做过渡。
// 槽内按钮淡入；卡片只过渡真会变的属性（拖动源的半透明、阴影、悬停边框），没有不生效的 padding、transform。
assert.equal(declarationsOf(".metric-pending-bar").get("animation"), "motion-pending-pulse 1200ms ease-in-out infinite");
assert.equal(declarationsOf(".metric-value:not(.pending)").get("animation"), "motion-fade-in var(--motion-medium) var(--ease-standard)");
assert.ok(!rules.some((rule) => rule.selectors.some((selector) => /^\.metric-value(?![:\w-])/.test(selector)) && rule.declarations.has("transition")),
  "card readings change in place; a transition would blur a value that updates every second");

// 组件样式不写过渡；持续循环的加载转圈是唯一写在组件里的动画。
const cssFiles: string[] = [];
const pending = [srcRoot];
for (let guard = 0; pending.length > 0 && guard < 10_000; guard += 1) {
  const directory = pending.pop()!;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) pending.push(path);
    else if (entry.name.endsWith(".css")) cssFiles.push(path);
  }
}
for (const path of cssFiles) {
  const name = relative(srcRoot, path).replace(/\\/g, "/");
  if (name === "styles/motion.css" || name === "styles.css") continue;
  const text = stripComments(readFileSync(path, "utf8"));
  assert.doesNotMatch(text, /(^|[;{\s])transition(-[a-z]+)?\s*:/, `${name} writes a transition; motion.css owns them`);
  if (name !== "styles/task-center.css") {
    assert.doesNotMatch(text, /(^|[;{\s])animation(-[a-z]+)?\s*:/, `${name} writes an animation; motion.css owns them`);
  }
}

console.log(`motionStandardContract: ${rules.length} motion.css rules, ${cssFiles.length} style sheets checked`);
