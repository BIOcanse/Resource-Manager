// 动画风格目录（presentation/animationStyle.ts）是唯一来源（docs/animation_style_2026-10-26.md「归属」）：
// motion.css 的令牌块和它一致，磁石全局层每种风格同一组参数、值按目录，前端自由点和九语都跟着目录。
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { animationStyle, animationStyleIds, animationStyles, cssEase, movingAnimationStyleIds, TERMINAL_STEPS } from "../src/presentation/animationStyle.ts";
import type { AnimationStyleDefinition } from "../src/presentation/animationStyle.ts";
import { CARRY_PARTS, springParam, SWITCH_PART } from "../src/interactions/components/springOrEase.ts";
import { globalLayer, partLayer, PARAM_SPECS, resolveParams } from "../src/interactions/protocol/params.ts";
import { declaredSpecs } from "../src/interactions/protocol/definition.ts";
import type { Definition } from "../src/interactions/protocol/definition.ts";
import { cardSlotLayoutDefinition } from "../src/interactions/components/cardSlotLayout.ts";
import { columnResizeHandleDefinition } from "../src/interactions/components/columnResizeHandle.ts";
import { entranceDefinitions } from "../src/interactions/components/entrance.ts";
import { controlSliderDefinition } from "../src/interactions/components/controlSlider.ts";
import { curvePointDefinition } from "../src/interactions/components/curvePoint.ts";
import { selectionIndicatorDefinition } from "../src/interactions/components/selectionIndicator.ts";
import { switchToggleDefinition } from "../src/interactions/components/switchToggle.ts";
import { EDGE_BAND_RUBBER, edgeRubberBandDefinition, IOS_RUBBER } from "../src/interactions/components/edgeRubberBand.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";

const here = dirname(fileURLToPath(import.meta.url));
const stylesRoot = resolve(here, "../src/styles");
const strip = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, "");
const css = strip(readFileSync(join(stylesRoot, "motion.css"), "utf8"));
type Rule = { selectors: string[]; declarations: Map<string, string>; at: number };
const rules: Rule[] = [...css.replace(/@keyframes[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, (match) => " ".repeat(match.length))
  .matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((match) => {
  const declarations = new Map<string, string>();
  for (const part of match[2].split(";")) {
    const colon = part.indexOf(":");
    if (colon >= 0) declarations.set(part.slice(0, colon).trim(), part.slice(colon + 1).replace(/\s+/g, " ").trim());
  }
  return { selectors: match[1].split(/,(?![^(]*\))/).map((selector) => selector.trim()), declarations, at: match.index! };
});
const isToken = (name: string) => name.startsWith("--motion-") || name.startsWith("--ease-");
const tokenRules = (selector: string) => rules.filter((rule) => rule.selectors.includes(selector) && [...rule.declarations.keys()].some(isToken));
function tokensOf(selector: string): Map<string, string> {
  const found = tokenRules(selector);
  assert.equal(found.length, 1, `motion.css has exactly one token block for ${selector}`);
  const tokens = new Map([...found[0].declarations].filter(([name]) => isToken(name)));
  assert.equal(tokens.size, found[0].declarations.size, `${selector} token block declares only motion tokens`);
  return tokens;
}
function expectedTokens(style: AnimationStyleDefinition): Map<string, string> {
  return new Map<string, string>([
    ...Object.entries(style.durationsMs).map(([token, ms]) => [token, `${ms}ms`] as [string, string]),
    ...Object.entries(style.easings).map(([token, ease]) => [token, cssEase(ease)] as [string, string])
  ]);
}

// 1. 目录：五种风格按设置里的次序，标识不重复；会动的是前三种。
assert.deepEqual([...animationStyleIds], ["smooth", "desktop", "terminal", "none", "ultra"]);
assert.deepEqual([...movingAnimationStyleIds], ["smooth", "desktop", "terminal"]);
const byId = (id: string) => animationStyles.find((style) => style.id === id)!;
const desktop = byId("desktop");

// 2. motion.css = 目录：:root 是桌面常规，每种风格一块 body[data-animations] 令牌，值和目录一样；窗口缩放清零的规则在它们后面。
assert.deepEqual(tokensOf(":root"), expectedTokens(desktop), ":root is the desktop tokens");
for (const style of animationStyles) {
  assert.deepEqual(tokensOf(`body[data-animations="${style.id}"]`), expectedTokens(style), `${style.id} tokens follow the catalog`);
}
const resizing = tokenRules('body[data-window-resizing="yes"]');
assert.equal(resizing.length, 1);
for (const style of animationStyles) {
  assert.ok(tokenRules(`body[data-animations="${style.id}"]`)[0].at < resizing[0].at, `window resizing overrides ${style.id}: it must come after`);
}
assert.ok([...resizing[0].declarations].every(([name, value]) => name.startsWith("--motion-") && value === "0ms"));
// 终端顿挫：CSS 的 steps(n) 和磁石的输出步数是同一个数。
for (const ease of Object.values(byId("terminal").easings)) assert.deepEqual(ease, { kind: "steps", count: TERMINAL_STEPS });
// 所有样式表里出现的风格标识都在目录里；“会动”的门控写的就是会动的那几种。
const styleSheets = readdirSync(stylesRoot, { recursive: true }).map(String).filter((name) => name.endsWith(".css"));
const moving = `body:is(${movingAnimationStyleIds.map((id) => `[data-animations="${id}"]`).join(", ")})`;
for (const name of styleSheets) {
  const text = strip(readFileSync(join(stylesRoot, name), "utf8"));
  for (const match of text.matchAll(/data-animations="([^"]+)"/g)) {
    assert.ok((animationStyleIds as readonly string[]).includes(match[1]), `${name}: data-animations="${match[1]}" is not a catalog style`);
  }
  for (const match of text.matchAll(/body:is\(([^)]*data-animations[^)]*)\)/g)) {
    const ids = [...match[1].matchAll(/data-animations="([^"]+)"/g)].map((entry) => entry[1]);
    const reduced = ids.every((id) => !movingAnimationStyleIds.includes(id as never));
    assert.ok(reduced || `body:is(${match[1]})` === moving, `${name}: a moving gate must list exactly ${moving}, got body:is(${match[1]})`);
  }
}
assert.ok(rules.some((rule) => rule.selectors.some((selector) => selector.startsWith(moving))), "motion.css gates movement on the moving styles");

// 3. 磁石全局层：每种风格同一组参数名；无动画、超高性能用桌面常规那一层（落点照样能算）；参数都是引擎或组件声明过的。
const keys = (style: AnimationStyleDefinition) => Object.keys(style.lodestone).sort();
for (const style of animationStyles) assert.deepEqual(keys(style), keys(desktop), `${style.id}: same Lodestone parameter set as desktop`);
for (const id of ["none", "ultra"]) assert.deepEqual(byId(id).lodestone, desktop.lodestone, `${id} uses the desktop Lodestone layer`);
assert.deepEqual(animationStyles.map((style) => [style.id, style.engineMode]),
  [["smooth", "normal"], ["desktop", "normal"], ["terminal", "normal"], ["none", "none"], ["ultra", "ultra"]]);
const definitions: Array<[string, Definition]> = [
  ["内容进场", entranceDefinitions.page], ["选中指示条", selectionIndicatorDefinition], ["卡片槽位", cardSlotLayoutDefinition],
  ["曲线点", curvePointDefinition], ["数值滑条", controlSliderDefinition], ["设置开关", switchToggleDefinition], ["列宽把手", columnResizeHandleDefinition],
  ["弹窗进场", entranceDefinitions.dialog], ["滚动区边缘回弹", edgeRubberBandDefinition]
];
const known = new Set([...PARAM_SPECS.map((spec) => spec.id as string), ...definitions.flatMap(([, def]) => declaredSpecs(def).map((spec) => spec.id as string))]);
for (const key of keys(desktop)) assert.ok(known.has(key), `Lodestone layer key ${key} is declared by the engine or a component`);
// 桌面常规那一层就是组件给的默认：缓动、不用弹簧，时长和曲线同 CARRY_PARTS（和 :root 的令牌）。
for (const [prefix, part] of [...Object.entries(CARRY_PARTS), [SWITCH_PART.prefix, SWITCH_PART] as const]) {
  assert.equal(desktop.lodestone[springParam(prefix)], 0, `desktop ${prefix}: eased`);
  assert.equal(desktop.lodestone[`${prefix}EaseMs`], part.ease.ms, `desktop ${prefix}: component default duration`);
  assert.equal(desktop.lodestone[`${prefix}EaseMs`], desktop.durationsMs[part.durationToken], `desktop ${prefix}: duration is ${part.durationToken}`);
  assert.deepEqual([1, 2].flatMap((n) => [desktop.lodestone[`${prefix}EaseX${n}`], desktop.lodestone[`${prefix}EaseY${n}`]]), [...part.ease.curve]);
}
assert.equal(desktop.lodestone.outputSteps, 0);
assert.equal(byId("terminal").lodestone.outputSteps, TERMINAL_STEPS);
assert.equal(byId("smooth").lodestone.outputSteps, 0);
for (const [prefix, part] of Object.entries(CARRY_PARTS)) {
  assert.equal(byId("smooth").lodestone[springParam(prefix)], 1, `smooth ${prefix}: spring`);
  assert.equal(byId("terminal").lodestone[springParam(prefix)], 0, `terminal ${prefix}: eased`);
  for (const style of animationStyles) {
    assert.equal(style.lodestone[`${prefix}SpringResponse`], part.spring.responseMs, `${style.id} ${prefix}: replica response`);
    assert.equal(style.lodestone[`${prefix}SpringDamping`], part.spring.damping, `${style.id} ${prefix}: replica damping`);
  }
}
assert.equal(byId("smooth").lodestone[springParam(SWITCH_PART.prefix)], 1);
// 滚动区边缘回弹只在流畅平滑：橡皮筋系数取 iOS 列表复刻的值，其余风格 0（不回弹，组件给的默认）。
for (const style of animationStyles) assert.equal(style.lodestone[EDGE_BAND_RUBBER], style.id === "smooth" ? IOS_RUBBER : 0, `${style.id}: edge rubber band`);
assert.equal(resolveParams(null, [], declaredSpecs(edgeRubberBandDefinition))[EDGE_BAND_RUBBER].value, 0, "the component default does not bounce");
// 弹窗不再有 CSS 进场动画：呈现层用磁石带（presentation/nodeAnimation.ts）；退场在幽灵上（[data-exit-ghost]）。
assert.ok(!rules.some((rule) => rule.selectors.some((selector) => selector === ".modal" || selector === ".modal-backdrop") && rule.declarations.has("animation")),
  "the dialog entrance is drawn by Lodestone, not a CSS animation");
// 退出动画槽位只在会动的风格里有：每条 [data-exit-ghost] 的动画规则都挂在会动的风格下，无动画、超高性能的槽位是空的。
const exitRules = rules.filter((rule) => rule.declarations.has("animation") && rule.selectors.some((selector) => selector.includes("[data-exit-ghost]")));
assert.ok(exitRules.length >= 8, "exit slots exist for popovers, dialogs, toasts, content, rows, disclosures and the rest");
for (const rule of exitRules) {
  for (const selector of rule.selectors) {
    assert.ok(selector.startsWith('body:is([data-animations="smooth"], [data-animations="desktop"], [data-animations="terminal"])'),
      `${selector}: an exit slot exists only in the moving styles`);
  }
}
// 范围小的覆盖大的：组件定义里写死的值不受风格影响；没写的项跟风格走。
for (const style of animationStyles) {
  const layer = globalLayer({ ...style.lodestone });
  const table = (by: string, def: Definition) => resolveParams(null, [layer, partLayer(by, def.params ?? {})], declaredSpecs(def));
  assert.equal(table("列宽把手", columnResizeHandleDefinition).settleTime.value, 0, `${style.id}: the column handle keeps settleTime 0`);
  const toggle = table("设置开关", switchToggleDefinition);
  assert.equal(toggle.releaseAbsorb.value, 0, `${style.id}: the switch keeps releaseAbsorb 0`);
  assert.equal(toggle.springTime.value, style.lodestone.springTime, `${style.id}: the switch spring time follows the style`);
  const page = table("内容进场", entranceDefinitions.page);
  assert.equal(page.pageSpring.value, style.lodestone.pageSpring, `${style.id}: page transition spring choice follows the style`);
  assert.equal(page.pageEaseMs.value, style.lodestone.pageEaseMs, `${style.id}: page transition duration follows the style`);
  assert.equal(page.outputSteps.value, style.lodestone.outputSteps, `${style.id}: output steps follow the style`);
}

// 4. 前端自由点：风格 choice 的选项就是目录、默认流畅平滑；自动关闭是 boolean、默认开；不认识的值取点的默认。
const points = JSON.parse(readFileSync(resolve(here, "../preferences.points.json"), "utf8")).points as Array<{ name: string; type: string; options?: string[]; value: unknown }>;
const stylePoint = points.find((entry) => entry.name === "appearance-animation_style");
assert.ok(stylePoint && stylePoint.type === "choice");
assert.deepEqual(stylePoint.options, [...animationStyleIds]);
assert.equal(stylePoint.value, "smooth");
const autoPoint = points.find((entry) => entry.name === "appearance-auto_disable_animations");
assert.ok(autoPoint && autoPoint.type === "boolean" && autoPoint.value === true);
assert.ok(!points.some((entry) => entry.name === "appearance-motion_feel" || entry.name === "appearance-animation_mode"), "old points are deleted");
assert.equal(animationStyle(undefined).id, "smooth");
assert.equal(animationStyle("normal").id, "smooth");
assert.equal(animationStyle("terminal").id, "terminal");

// 5. 九语：行名、开关名和说明、五个风格名和说明都有；不是基础语言原文（繁中对简中，其余对英文），非中日文不含汉字。
const languages: ConcreteAppLanguageMode[] = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"];
const texts = new Map(await Promise.all(languages.map(async (language) => [language, await loadSettingsText(language)] as const)));
for (const language of languages) {
  const text = texts.get(language)!;
  assert.equal(text.language, language, `${language}: settings text fell back to another language`);
  assert.deepEqual(text.animationStyleOptions.map((option) => option.id), [...animationStyleIds], `${language}: options follow the catalog`);
  const own = [text.appearance.animationStyleTitle, text.appearance.animationStyleDescription,
    text.appearance.autoDisableAnimationsTitle, text.appearance.autoDisableAnimationsDescription,
    ...text.animationStyleOptions.flatMap((option) => [option.label, option.description])];
  for (const value of own) {
    assert.ok(typeof value === "string" && value.trim().length > 0, `${language}: empty animation style text`);
    if (!/^(?:zh|ja)-/.test(language)) assert.doesNotMatch(value, /\p{Script=Han}/u, `${language}: animation style text contains Han: ${value}`);
  }
  assert.equal(new Set(text.animationStyleOptions.map((option) => option.label)).size, animationStyleIds.length, `${language}: style names are distinct`);
  const base = language === "zh-CN" || language === "en-US" ? null : texts.get(language === "zh-TW" ? "zh-CN" : "en-US")!;
  if (!base) continue;
  const baseOwn = [base.appearance.animationStyleTitle, base.appearance.animationStyleDescription,
    base.appearance.autoDisableAnimationsTitle, base.appearance.autoDisableAnimationsDescription,
    ...base.animationStyleOptions.flatMap((option) => [option.label, option.description])];
  own.forEach((value, index) => assert.notEqual(value, baseOwn[index], `${language}: text ${index} left in the base language: ${value}`));
}

console.log(`animationStyleContract: ${animationStyles.length} styles match motion.css, the Lodestone layer, the points and nine languages`);
