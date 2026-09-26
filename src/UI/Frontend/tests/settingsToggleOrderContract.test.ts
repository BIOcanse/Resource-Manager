import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// 设置页的两态/三态开关，一律「开启在前、关闭在后」。
// 这个顺序由各设置项自己的选项数组决定，框架只按给定顺序渲染，
// 所以写错一处就会出现两行开关左右相反 —— 这里把约定钉住。
const section = read("../src/features/settings/components/PerformanceSettingsSection.tsx");
const locales = read("../src/i18n/settingsLocaleFactory.ts");

assert.ok(
  section.indexOf('{ id: "precise"') < section.indexOf('{ id: "basic"'),
  "精确 GPU 选择：开启（precise）要排在关闭（basic）前面");

for (const [enabled, disabled] of [
  ['{ id: "on"', '{ id: "off"'],
  ['{ id: "enabled"', '{ id: "disabled"']
] as const) {
  let cursor = 0;
  for (;;) {
    const start = locales.indexOf(enabled, cursor);
    if (start < 0) {
      break;
    }
    const stop = locales.indexOf(disabled, start);
    assert.ok(stop > start, `${enabled} 之后要紧跟 ${disabled}`);
    cursor = stop + disabled.length;
  }
}

// 三态的自动项排在最前，其后才是开启与关闭。
for (const block of ["adaptiveBooleanModeOptions", "resourceBarHardwareAccelerationModeOptions"]) {
  const start = locales.indexOf(block);
  assert.ok(start > 0, `找不到 ${block}`);
  const slice = locales.slice(start, start + 600);
  assert.ok(
    slice.indexOf('"auto"') < slice.indexOf('"enabled"')
      && slice.indexOf('"enabled"') < slice.indexOf('"disabled"'),
    `${block} 的顺序要是 自动 / 开启 / 关闭`);
}

function read(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}

console.log("settingsToggleOrderContract: ok");
