// 资源条的绘制和交互合同，按问题分段，各段开头写着它防的是什么。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  enteringSources,
  fitCanvasLabel,
  leavingSegments,
  transitionFrame,
  type ResourcePaintSegment
} from "../src/features/resourceBreakdown/resourceSegmentPainting.ts";

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src");
const read = (path: string) => readFileSync(resolve(srcRoot, path), "utf8");
const stripComments = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");

// ---- 配色 ----
// 分段颜色只有一处来源（theme.css 的 --resource-* 变量）；分段上的字对比度够、八个区分色两两分得开、
// 系统/驱动保留和共享部分各自看得出；两种配色都画分隔线。
const theme = stripComments(read("styles/theme.css"));
const rootBlock = /:root\s*\{([\s\S]*?)\n\}/.exec(theme)?.[1] ?? "";
const token = (name: string) => {
  const match = new RegExp(`${name}:\\s*rgb\\((\\d+) (\\d+) (\\d+)(?: \\/ ([\\d.]+))?\\);`).exec(rootBlock);
  assert.ok(match, `theme.css :root must define ${name} as rgb()`);
  return { rgb: [Number(match[1]), Number(match[2]), Number(match[3])].map((value) => value / 255), alpha: match[4] === undefined ? 1 : Number(match[4]) };
};
const linear = (value: number) => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
const luminance = ([r, g, b]: number[]) => 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
const contrast = (a: number[], b: number[]) => {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high + 0.05) / (low + 0.05);
};
const oklab = ([r, g, b]: number[]) => {
  const [lr, lg, lb] = [linear(r), linear(g), linear(b)];
  const l = Math.cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
  const m = Math.cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
  const s = Math.cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
  return [0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s];
};
const distance = (a: number[], b: number[]) => Math.hypot(...oklab(a).map((value, index) => value - oklab(b)[index]));
const over = (base: number[], layer: { rgb: number[]; alpha: number }) =>
  base.map((value, index) => value * (1 - layer.alpha) + layer.rgb[index] * layer.alpha);

const text = token("--resource-segment-text").rgb;
const kinds = ["system", "adapted", "other"].map((name) => [name, token(`--resource-kind-${name}`).rgb] as const);
const distinct = Array.from({ length: 8 }, (_, index) => [`segment-${index + 1}`, token(`--resource-segment-${index + 1}`).rgb] as const);
const reserved = token("--resource-reserved").rgb;
for (const [name, color] of [...kinds, ...distinct, ["reserved", reserved] as const]) {
  assert.ok(contrast(color, text) >= 4.5, `segment label on ${name} must reach 4.5:1 (${contrast(color, text).toFixed(2)})`);
}
for (let left = 0; left < distinct.length; left++) {
  for (let right = left + 1; right < distinct.length; right++) {
    const gap = distance(distinct[left][1], distinct[right][1]);
    assert.ok(gap >= 0.1, `${distinct[left][0]} and ${distinct[right][0]} are too alike (OKLab ${gap.toFixed(3)})`);
  }
}
for (const [name, color] of kinds) {
  assert.ok(distance(color, reserved) >= 0.1, `system/driver reserved must not look like the ${name} kind`);
  const shared = over(color, token("--resource-shared-tint"));
  assert.ok(distance(color, shared) >= 0.08, `the shared part of a ${name} segment must stand out from the rest of it`);
  for (const [otherName, otherColor] of kinds) {
    if (otherName !== name) {
      assert.ok(distance(shared, otherColor) >= 0.05, `the shared part of a ${name} segment must not look like a ${otherName} segment`);
    }
  }
}

// 颜色只在 CSS 变量里：两份组件源码不写颜色字面量。
for (const path of ["features/resourceBreakdown/ResourceBreakdown.tsx", "features/resourceBreakdown/ResourceSegmentPaintPlane.tsx"]) {
  assert.doesNotMatch(stripComments(read(path)), /color\(display-p3|rgba?\(\s*\d|#[0-9a-f]{3,8}\b/i, `${path} hard-codes a colour; use a --resource-* token`);
}
const breakdown = read("features/resourceBreakdown/ResourceBreakdown.tsx");
const identity = read("features/resourceBreakdown/resourceProcessIdentity.ts");
assert.match(identity, /segment\.displayKind === "system-residual"/, "system/driver reserved segments are recognised by their typed display kind");
assert.match(breakdown, /import \{[^}]*isReservedSegment[^}]*\} from "\.\/resourceProcessIdentity\.ts"/, "one owner decides the reserved treatment");
assert.match(breakdown, /hatched: isReservedSegment\(item\.segment\)/);
assert.match(breakdown, /hatched: isReservedProcess\(software, item\.segment\)/, "every process under the reserved entry is hatched");
const plane = read("features/resourceBreakdown/ResourceSegmentPaintPlane.tsx");
assert.doesNotMatch(plane, /barColor !== "distinct"/, "dividers are drawn in the distinct colour mode too");

// ---- 过渡中离开和新来的段 ----
// 离开的段收向左边仍在的那一段的新右端，新来的段从它的旧右端长出来，过渡的每一帧条上都不留洞。
const segment = (key: string, left: number, right: number): ResourcePaintSegment => ({ key, left, right, color: "var(--x)" });
const from = [segment("a", 0, 20), segment("gone", 20, 50), segment("b", 50, 60)];
const to = [segment("a", 0, 25), segment("b", 25, 35)];
const leaving = leavingSegments(from, to);
assert.deepEqual(leaving.map((item) => [item.source.key, item.anchor]), [["gone", 25]]);
const fromByKey = new Map(from.map((item) => [item.key, item]));
for (const eased of [0, 0.25, 0.5, 0.75, 1]) {
  const frame = transitionFrame(fromByKey, leaving, to, eased);
  const byKey = new Map(frame.map((item) => [item.key, item]));
  assert.ok(Math.abs(byKey.get("gone")!.left - byKey.get("a")!.right) < 1e-9, `leaving segment stays attached to its left neighbour at ${eased}`);
  assert.ok(Math.abs(byKey.get("gone")!.right - byKey.get("b")!.left) < 1e-9, `no gap opens after the leaving segment at ${eased}`);
}
assert.equal(transitionFrame(fromByKey, leaving, to, 1).find((item) => item.key === "gone")!.right
  - transitionFrame(fromByKey, leaving, to, 1).find((item) => item.key === "gone")!.left, 0, "it has collapsed at the end");

// 软件回来：b 后面新来 c 和 d，右边的 e 让开；每一帧从 0 到最右端连成一片。
const before = [segment("b", 0, 30), segment("e", 30, 50)];
const after = [segment("b", 0, 20), segment("c", 20, 35), segment("d", 35, 40), segment("e", 40, 70)];
const entering = enteringSources(before, after);
assert.deepEqual([...entering.values()].map((item) => [item.key, item.left, item.right]), [["c", 30, 30], ["d", 30, 30]]);
const enteringFrom = new Map([...entering, ...before.map((item) => [item.key, item] as const)]);
for (const eased of [0, 0.25, 0.5, 0.75, 1]) {
  const frame = transitionFrame(enteringFrom, leavingSegments(before, after), after, eased);
  for (let index = 1; index < frame.length; index++) {
    assert.ok(Math.abs(frame[index].left - frame[index - 1].right) < 1e-9, `${frame[index].key} is attached to ${frame[index - 1].key} at ${eased}`);
  }
}

// ---- 标签 ----
// 放不下时用省略号，截得只剩一两个字就不画。
const width = (value: string) => value.length * 7;
assert.equal(fitCanvasLabel(width, "Microsoft Edge", 200, 3), "Microsoft Edge");
assert.equal(fitCanvasLabel(width, "Microsoft Edge", 50, 3), "Micros…");
assert.equal(fitCanvasLabel(width, "Microsoft Edge", 20, 3), "", "a label cut to fewer than three characters is not drawn");
assert.equal(fitCanvasLabel(width, "Microsoft Edge", 77, 3), "Microsoft…", "no space is left before the ellipsis");

// ---- 焦点和高对比 ----
// 键盘焦点有外框；Windows 高对比下焦点、悬停、选中改用系统颜色的外框。
const resources = stripComments(read("styles/resources.css"));
assert.match(resources, /\.resource-bar-track:focus-visible,\s*\.resource-process-track:focus-visible\s*\{\s*outline:\s*var\(--focus-ring\);/,
  "keyboard focus on a resource track draws the shared focus ring");
// resources.css 有不止一段强制颜色规则（资源表也有自己的一段），取写资源条的那一段。
const forcedBlocks = [...resources.matchAll(/@media \(forced-colors: active\) \{([\s\S]*?)\n\}/g)].map((match) => match[1]);
const forcedBarBlocks = forcedBlocks.filter((block) => block.includes(".resource-bar-track"));
assert.equal(forcedBarBlocks.length, 1, "the resource bar forced-colour rules sit in one block");
const forced = forcedBarBlocks[0];
assert.match(forced, /\.resource-bar-track:focus-visible[\s\S]*?outline: 2px solid Highlight/);
assert.match(forced, /\.resource-segment-interaction-proxy\.pointer-active\s*\{[^}]*outline:/);
assert.match(forced, /\.resource-segment\.precision-selected[\s\S]*?outline: 3px solid Highlight/);

// ---- 提示 ----
// 名字一行（前面一个和段同色的色块）、读数各一行、只属于这一段的归因说明在下面；左对齐。出现时淡入，无动画、超高性能下直接出现。
assert.match(breakdown, /class="resource-tooltip-swatch"/);
assert.match(breakdown, /class="resource-tooltip-basis"/);
assert.match(resources, /\.resource-tooltip\s*\{[^}]*text-align:\s*left;/);
const motion = stripComments(read("styles/motion.css"));
assert.match(motion, /\.resource-tooltip\s*\{\s*animation:\s*motion-fade-in var\(--motion-fast\) var\(--ease-standard\);/);
assert.match(motion, /body\[data-animations="none"\] \*,[\s\S]*?animation-duration:\s*0s !important;/, "the reduced modes show the tooltip without the fade");
// 提示挂在页面最顶层（共用的 AnchoredLayer：Portal 到 body，position: fixed，和下拉框弹出层同一层），不在资源条那一行里：下一行盖不住它。
// 整条的口径只说一次（条名后面的问号和轨道的 aria-description），各段的提示不再重复它。
const overlay = read("features/resourceBreakdown/ResourceTooltipOverlay.tsx");
assert.match(overlay, /<AnchoredLayer [^>]*class="resource-tooltip"/);
assert.match(read("ui/primitives/AnchoredLayer.tsx"), /<Portal>/);
assert.match(resources, /\.resource-tooltip\s*\{[^}]*position:\s*fixed;[^}]*z-index:\s*1200;/);
assert.doesNotMatch(breakdown, /<span\s+class="resource-tooltip"/, "no tooltip is rendered inside a bar row any more");
// 两个浮层同一套外观：提示和下拉框弹出层用同样的表面、细边框和阴影令牌，深色配色下也和后面的卡片分得开；各风格不再单独改提示。
const tooltipRule = /\.resource-tooltip\s*\{([^}]*)\}/.exec(resources)?.[1] ?? "";
const popoverRule = /\.standard-select-popover\s*\{([^}]*)\}/.exec(stripComments(read("styles/controls.css")))?.[1] ?? "";
for (const property of ["border", "background", "color", "box-shadow", "border-radius"]) {
  const value = (rule: string) => new RegExp(`(?:^|;)\\s*${property}:\\s*([^;]+);`).exec(rule)?.[1]?.trim();
  assert.equal(value(tooltipRule), value(popoverRule), `tooltip ${property} matches the dropdown popover`);
}
for (const style of ["outline", "terminal", "soft"]) {
  assert.doesNotMatch(read(`styles/ui-styles/${style}.css`), /\.resource-tooltip\s*\{/, `${style} does not restyle the tooltip apart from the popover`);
}
assert.equal((breakdown.match(/<ResourceTooltipOverlay /g) ?? []).length, 2, "software and process bars both use the overlay");
assert.match(breakdown, /aria-description=\{props\.bar\.basis \|\| undefined\}/);
assert.doesNotMatch(breakdown, /notes: \[bar\.basis/, "the bar-wide basis is not repeated in each segment");

console.log("resourceBarPaint: ok");
