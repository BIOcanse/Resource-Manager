// 进场、退场的呈现层（src/presentation/nodeAnimation.ts，docs/animation_style_2026-10-26.md「总原则」）的静态合同：
// 1. 幽灵去掉身份类的 data 属性，只留样式表按它们选样式的那几个：样式表里每个按 data 属性选的选择器，
//    要么是文档或滚动区一级的状态（不会出现在幽灵里），要么在幽灵保留的名单里，否则幽灵会丢样式。
// 2. 进场、退场的画面只有这一处：组件不自己写退场副本或进场状态（不再有 .content-transition-leaving、.revealing、dialogEntrance）。
// 3. 动画类别（「总原则」的两类表）：呈现层只做进场退场。它的两张槽位表只写进场退场，和 motion.css 用到的槽位名一一对应；
//    它用的磁石定义（components/entrance.ts）声明进场退场，而且只有它用；它不用任何平面移动的定义。
//    反过来，平面移动的定义只能经 useInteraction 挂在已经在场的节点上（solid/lodestone.ts 的参数类型只收平面移动）。
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { entranceDefinitions } from "../src/interactions/components/entrance.ts";

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = resolve(here, "../src");
const owner = readFileSync(join(srcRoot, "presentation/nodeAnimation.ts"), "utf8");
const kept = new Set([...(owner.match(/GHOST_STYLE_DATA_ATTRIBUTES[^=]*=\s*new Set\(\[([\s\S]*?)\]\)/)?.[1] ?? "").matchAll(/"([\w-]+)"/g)].map((match) => match[1]));
assert.ok(kept.size > 0, "the ghost style attribute list is readable");

/** 文档、页面或滚动区一级的状态（写在 body、html 或滚动区上），以及呈现层自己的标记：幽灵里不会有，不必保留。 */
const documentLevel = new Set(["data-ui-style", "data-animations", "data-theme", "data-page", "data-font-smoothing", "data-window-resizing",
  "data-edge-band", "data-exit-ghost", "data-exit-slot", "data-enter-slot"]);

const files: string[] = [];
const stack = [srcRoot];
while (stack.length > 0) {
  const directory = stack.pop()!;
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) stack.push(path);
    else files.push(path);
  }
}
const styleAttributes = new Set<string>();
for (const path of files.filter((file) => file.endsWith(".css"))) {
  const text = readFileSync(path, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
  for (const match of text.matchAll(/\[(data-[\w-]+)/g)) styleAttributes.add(match[1]);
}
for (const attribute of styleAttributes) {
  assert.ok(documentLevel.has(attribute) || kept.has(attribute),
    `${attribute} selects styles but a ghost would drop it: keep it in GHOST_STYLE_DATA_ATTRIBUTES or mark it document-level here`);
}

const sources = files.filter((file) => /\.(ts|tsx)$/.test(file) && !file.replaceAll("\\", "/").endsWith("presentation/nodeAnimation.ts"));
for (const path of sources) {
  const text = readFileSync(path, "utf8");
  for (const retired of ["content-transition-leaving", "dialogEntrance", "revealingRowIds", "motion-row-reveal"]) {
    assert.ok(!text.includes(retired), `${path} still uses ${retired}; entrance and exit visuals belong to presentation/nodeAnimation.ts`);
  }
  assert.ok(!/setAttribute\(\s*["']data-exit-ghost/.test(text), `${path} makes its own exit ghost`);
}
const slotTable = (name: string) => {
  const body = owner.match(new RegExp(`export const ${name} = \\{([^}]*)\\}`))?.[1];
  assert.ok(body, `${name} is readable in presentation/nodeAnimation.ts`);
  return new Map([...body.matchAll(/(\w+):\s*"([^"]+)"/g)].map((match) => [match[1], match[2]]));
};
const motionCss = readFileSync(join(srcRoot, "styles/motion.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
for (const [table, attribute] of [["ENTER_SLOTS", "data-enter-slot"], ["EXIT_SLOTS", "data-exit-slot"]] as const) {
  const slots = slotTable(table);
  for (const [slot, kind] of slots) assert.equal(kind, "进场退场", `${table}.${slot} is an entrance or exit slot`);
  const styled = new Set([...motionCss.matchAll(new RegExp(`\\[${attribute}="(\\w+)"\\]`, "g"))].map((match) => match[1]));
  assert.deepEqual([...styled].sort(), [...slots.keys()].sort(), `motion.css styles exactly the ${table} of presentation/nodeAnimation.ts`);
}
for (const [part, definition] of Object.entries(entranceDefinitions)) {
  assert.equal(definition.motion, "进场退场", `the ${part} entrance declares the entrance and exit kind`);
}
const componentImports = [...owner.matchAll(/from "\.\.\/interactions\/components\/(\w+)\.ts"/g)].map((match) => match[1]);
assert.deepEqual([...new Set(componentImports)].sort(), ["common", "entrance"], "the presentation owner uses only the entrance definitions");
for (const path of sources) {
  assert.ok(!/components\/entrance\.ts"/.test(readFileSync(path, "utf8")) || path.replaceAll("\\", "/").endsWith("interactions/components/entrance.ts"),
    `${path} uses an entrance definition; entrances belong to presentation/nodeAnimation.ts`);
}
console.log(`PASS node animation contract: ${styleAttributes.size} style data attributes, ${kept.size} kept on ghosts, ${sources.length} sources`);
