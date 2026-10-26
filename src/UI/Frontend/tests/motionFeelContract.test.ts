// 手感目录和 motion.css 一致：标准就是 :root 的令牌，其余手感各有一块 :root[data-motion-feel] 覆盖，
// 每块把目录里的令牌写全、值相同，--ease-bar 不随手感变。规则见 docs/motion_feel_2026-10-25.md。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { motionFeels, motionFeelIds, motionFeel } from "../src/presentation/motionFeel.ts";
import { globalLayer, PARAM_SPECS, partLayer, resolveParams } from "../src/interactions/protocol/params.ts";
import { columnResizeHandleDefinition } from "../src/interactions/components/columnResizeHandle.ts";
import { switchToggleDefinition } from "../src/interactions/components/switchToggle.ts";
import { contentTransitionDefinition, CONTENT_TRANSITION_CURVE, CONTENT_TRANSITION_EASE_MS } from "../src/interactions/components/contentTransition.ts";
import { declaredSpecs } from "../src/interactions/protocol/definition.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";

const css = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../src/styles/motion.css"), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");

function block(selector: string): Map<string, string> {
  const start = css.indexOf(`${selector} {`);
  assert.ok(start >= 0, `motion.css has no block ${selector}`);
  assert.equal(css.indexOf(`${selector} {`, start + 1), -1, `motion.css repeats ${selector}`);
  const body = css.slice(css.indexOf("{", start) + 1, css.indexOf("}", start));
  const declarations = new Map<string, string>();
  for (const line of body.split(";")) {
    const colon = line.indexOf(":");
    if (colon >= 0) declarations.set(line.slice(0, colon).trim(), line.slice(colon + 1).trim());
  }
  return declarations;
}

const curve = (points: readonly number[]) => `cubic-bezier(${points.join(", ")})`;

assert.equal(motionFeels[0].id, "standard", "The first feel is the :root default");
assert.equal(new Set(motionFeels.map((feel) => feel.id)).size, motionFeels.length, "Feel IDs are unique");

for (const [index, feel] of motionFeels.entries()) {
  const declarations = block(index === 0 ? ":root" : `:root[data-motion-feel="${feel.id}"]`);
  for (const [token, ms] of Object.entries(feel.durationsMs)) {
    assert.equal(declarations.get(token), `${ms}ms`, `${feel.id} ${token}`);
  }
  for (const [token, points] of Object.entries(feel.easings)) {
    assert.equal(declarations.get(token), curve(points), `${feel.id} ${token}`);
  }
  if (index > 0) {
    assert.equal(declarations.size, Object.keys(feel.durationsMs).length + Object.keys(feel.easings).length,
      `${feel.id} overrides only the catalog tokens: ${[...declarations.keys()].join(", ")}`);
    assert.ok(!declarations.has("--ease-bar"), `${feel.id} must not change --ease-bar`);
  }
}

// 没有目录之外的手感块。
const feelSelectors = [...css.matchAll(/data-motion-feel="([^"]+)"/g)].map((match) => match[1]);
assert.deepEqual([...new Set(feelSelectors)].sort(), motionFeels.slice(1).map((feel) => feel.id).sort());

// 磁石全局层：到位时间和回点时间跟这种手感的 --motion-medium 一致。标准不覆盖，引擎的来源值本来就是 180。
const engineSource = (id: string) => {
  const spec = PARAM_SPECS.find((entry) => entry.id === id);
  assert.ok(spec && (spec.source.kind === "token" || spec.source.kind === "protocol"), `engine param ${id} has a fixed source value`);
  return spec.source.value;
};
for (const [index, feel] of motionFeels.entries()) {
  const medium = feel.durationsMs["--motion-medium"];
  if (index === 0) {
    assert.deepEqual(feel.lodestone, {}, "standard leaves the Lodestone global layer empty");
    assert.equal(engineSource("settleTime"), medium, "engine settleTime source equals the standard --motion-medium");
    assert.equal(engineSource("springTime"), medium, "engine springTime source equals the standard --motion-medium");
  } else {
    assert.deepEqual(Object.keys(feel.lodestone).sort(),
      ["pageEaseMs", "pageEaseX1", "pageEaseX2", "pageEaseY1", "pageEaseY2", "settleTime", "springTime"],
      `${feel.id} overrides only the settle and spring times and the content transition ease`);
    assert.equal(feel.lodestone.settleTime, medium, `${feel.id} Lodestone settleTime equals --motion-medium`);
    assert.equal(feel.lodestone.springTime, medium, `${feel.id} Lodestone springTime equals --motion-medium`);
  }
}

// 内容过渡（换页、二级分页、页签面板、设置分区）的换页缓动：组件给的默认就是标准的 --motion-page 和 --ease-decel，
// 其余手感在全局层换成自己的 --motion-page 和 --ease-decel。
assert.equal(CONTENT_TRANSITION_EASE_MS, motionFeels[0].durationsMs["--motion-page"], "content transition default = standard --motion-page");
assert.deepEqual([...CONTENT_TRANSITION_CURVE], [...motionFeels[0].easings["--ease-decel"]], "content transition default curve = standard --ease-decel");
for (const feel of motionFeels) {
  const table = resolveParams(null, [globalLayer({ ...feel.lodestone }), partLayer("内容过渡", contentTransitionDefinition.params ?? {})],
    declaredSpecs(contentTransitionDefinition));
  assert.equal(table.pageEaseMs.value, feel.durationsMs["--motion-page"], `${feel.id}: content transition time follows --motion-page`);
  assert.deepEqual([table.pageEaseX1.value, table.pageEaseY1.value, table.pageEaseX2.value, table.pageEaseY2.value],
    [...feel.easings["--ease-decel"]], `${feel.id}: content transition curve follows --ease-decel`);
}

// 范围小的覆盖大的：手感只在全局层，组件定义写的值不受它影响（列宽把手的到位时间 0、开关的松手吸能 0），
// 定义没写的项（开关的回点时间）跟手感走。
for (const feel of motionFeels) {
  const column = resolveParams(null, [globalLayer({ ...feel.lodestone }), partLayer("列宽把手", columnResizeHandleDefinition.params ?? {})]);
  assert.equal(column.settleTime.value, 0, `${feel.id}: the column handle keeps its own settleTime 0`);
  const toggle = resolveParams(null, [globalLayer({ ...feel.lodestone }), partLayer("设置开关", switchToggleDefinition.params ?? {})]);
  assert.equal(toggle.releaseAbsorb.value, 0, `${feel.id}: the switch keeps its own releaseAbsorb 0`);
  assert.equal(toggle.springTime.value, feel.durationsMs["--motion-medium"], `${feel.id}: the switch spring follows the feel`);
}

// 前端自由点 appearance-motion_feel：选项就是目录，默认是标准；不认识的值取点的默认值。
const points = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../preferences.points.json"), "utf8")).points as Array<{ name: string; options?: string[]; value: unknown }>;
const point = points.find((entry) => entry.name === "appearance-motion_feel");
assert.ok(point, "preferences.points.json declares appearance-motion_feel");
assert.deepEqual(point.options, [...motionFeelIds], "point options are the catalog in order");
assert.equal(point.value, "standard", "the point default is standard");
assert.equal(motionFeel(undefined).id, "standard");
assert.equal(motionFeel("bouncy").id, "standard");
assert.equal(motionFeel("crisp").id, "crisp");

// 九语：行标题、说明和三个选项名都有；不是基础语言原文（繁中对简中，其余对英文），非中日文不含汉字。
const languages: ConcreteAppLanguageMode[] = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"];
const texts = new Map(await Promise.all(languages.map(async (language) => [language, await loadSettingsText(language)] as const)));
for (const language of languages) {
  const text = texts.get(language)!;
  assert.equal(text.language, language, `${language}: settings text fell back to another language`);
  const strings = [text.appearance.motionFeelTitle, text.appearance.motionFeelDescription, ...text.motionFeelOptions.map((option) => option.label)];
  assert.deepEqual(text.motionFeelOptions.map((option) => option.id), [...motionFeelIds], `${language}: options follow the catalog`);
  for (const value of strings) {
    assert.ok(typeof value === "string" && value.trim().length > 0, `${language}: empty motion feel text`);
    if (!/^(?:zh|ja)-/.test(language)) assert.doesNotMatch(value, /\p{Script=Han}/u, `${language}: motion feel text contains Han: ${value}`);
  }
  const base = language === "zh-CN" || language === "en-US" ? null : texts.get(language === "zh-TW" ? "zh-CN" : "en-US")!;
  if (base) {
    assert.notEqual(text.appearance.motionFeelDescription, base.appearance.motionFeelDescription, `${language}: description left in the base language`);
    if (language !== "zh-TW") assert.notEqual(text.appearance.motionFeelTitle, base.appearance.motionFeelTitle, `${language}: title left in the base language`);
    assert.notDeepEqual(text.motionFeelOptions, base.motionFeelOptions, `${language}: option names left in the base language`);
  }
}

console.log(`motionFeelContract: ${motionFeels.length} feels match motion.css, the Lodestone layer and nine languages`);
