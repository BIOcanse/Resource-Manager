// 进场、退场的呈现层（src/presentation/nodeAnimation.ts，docs/animation_style_2026-10-26.md「总原则」）的静态合同：
// 1. 幽灵去掉身份类的 data 属性，只留样式表按它们选样式的那几个：样式表里每个按 data 属性选的选择器，
//    要么是文档或滚动区一级的状态（不会出现在幽灵里），要么在幽灵保留的名单里，否则幽灵会丢样式。
// 2. 进场、退场的画面只有这一处：组件不自己写退场副本或进场状态（不再有 .content-transition-leaving、.revealing、dialogEntrance）。
// 3. 动画类别（「总原则」的两类表）：呈现层只做进场退场。它的两张槽位表只写进场退场，和 motion.css 用到的槽位名一一对应；
//    它用的磁石定义（components/entrance.ts 的进场、components/reflow.ts 的重排）声明进场退场，而且只有它用；它不用任何平面移动的定义。
//    反过来，平面移动的定义只能经 useInteraction 挂在已经在场的节点上（solid/lodestone.ts 的参数类型只收平面移动）。
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = resolve(here, "../src");
const owner = readFileSync(join(srcRoot, "presentation/nodeAnimation.ts"), "utf8");
const kept = new Set([...(owner.match(/GHOST_STYLE_DATA_ATTRIBUTES[^=]*=\s*new Set\(\[([\s\S]*?)\]\)/)?.[1] ?? "").matchAll(/"([\w-]+)"/g)].map((match) => match[1]));
assert.ok(kept.size > 0, "the ghost style attribute list is readable");

/** 文档、页面或滚动区一级的状态（写在 body、html 或滚动区上），以及呈现层自己的标记：幽灵里不会有，不必保留。 */
const documentLevel = new Set(["data-ui-style", "data-animations", "data-theme", "data-page", "data-font-smoothing", "data-window-resizing",
  "data-edge-band", "data-exit-ghost", "data-exit-slot", "data-enter-slot", "data-reflow-copy"]);

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
// 槽位动画按事件数，不在换页的任务里查整棵子树：呈现层不调用 getAnimations；它等的关键帧和 motion.css 里槽位规则用的一致。
assert.doesNotMatch(owner, /getAnimations\(/, "the presentation owner counts its slot animations by events instead of querying a subtree");
const keyframeSet = (name: string) => new Set([...(owner.match(new RegExp(`export const ${name}[^=]*=\\s*new Set\\(\\[([^\\]]*)\\]\\)`))?.[1] ?? "")
  .matchAll(/"([\w-]+)"/g)].map((match) => match[1]));
const slotRules = [...motionCss.matchAll(/([^{}]+)\{([^{}]*)\}/g)];
for (const [name, marker] of [["EXIT_KEYFRAMES", "[data-exit-ghost]"], ["ENTER_KEYFRAMES", "[data-enter-slot"]] as const) {
  const used = new Set(slotRules.filter((rule) => rule[1].includes(marker)).flatMap((rule) =>
    [...rule[2].matchAll(/animation:\s*([\w-]+)/g)].map((match) => match[1])).filter((keyframe) => keyframe !== "none"));
  assert.deepEqual([...keyframeSet(name)].sort(), [...used].sort(), `${name} lists exactly the keyframes the ${marker} rules of motion.css play`);
}

// components 下每个模块按它导出的定义归类：进场退场的模块（进场、重排）只有呈现层用，呈现层也只用它们。
const componentsRoot = join(srcRoot, "interactions/components");
const kindOfModule = new Map<string, Set<unknown>>();
for (const name of readdirSync(componentsRoot).filter((file) => file.endsWith(".ts"))) {
  const module: Record<string, unknown> = await import(pathToFileURL(join(componentsRoot, name)).href);
  const values = Object.values(module).flatMap((value) => (typeof value === "object" && value !== null && !("rows" in value) ? Object.values(value) : [value]));
  const kinds = new Set(values.filter((value): value is { rows: unknown; motion: unknown } =>
    typeof value === "object" && value !== null && "rows" in value && "channel" in value).map((definition) => definition.motion));
  if (kinds.size > 0) kindOfModule.set(name.replace(/\.ts$/, ""), kinds);
}
const entranceModules = [...kindOfModule].filter(([, kinds]) => kinds.has("进场退场")).map(([name]) => name).sort();
for (const name of entranceModules) assert.deepEqual([...kindOfModule.get(name)!], ["进场退场"], `${name}.ts mixes entrance and planar definitions`);
assert.deepEqual(entranceModules, ["entrance", "reflow"], "the entrance and exit parts are the entrances and the reflow");
const componentImports = [...owner.matchAll(/from "\.\.\/interactions\/components\/(\w+)\.ts"/g)].map((match) => match[1]);
assert.deepEqual([...new Set(componentImports)].sort(), ["common", ...entranceModules], "the presentation owner uses only the entrance and exit parts");
for (const path of sources) {
  const text = readFileSync(path, "utf8");
  const own = path.replaceAll("\\", "/");
  for (const name of entranceModules) {
    assert.ok(!text.includes(`components/${name}.ts"`) || own.endsWith(`interactions/components/${name}.ts`),
      `${path} uses ${name}.ts; entrance and exit parts belong to presentation/nodeAnimation.ts`);
  }
}
console.log(`PASS node animation contract: ${styleAttributes.size} style data attributes, ${kept.size} kept on ghosts, ${sources.length} sources`);
