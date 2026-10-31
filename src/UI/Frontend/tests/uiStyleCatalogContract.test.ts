// 前端的风格目录副本必须和唯一来源 src/Shared/Appearance/ui-styles.json 一致，预启动底色表必须是按它生成的。
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { normalizeUiAppearance, uiStyles } from "../src/presentation/uiStyles.ts";

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const catalog = JSON.parse(readFileSync(resolve(frontendRoot, "../../Shared/Appearance/ui-styles.json"), "utf8")) as {
  styles: Array<{ id: string; palettes: Array<{ id: string; startupBackground?: string; follows?: { light: string; dark: string } }> }>;
};

assert.deepEqual(
  uiStyles.map((style) => ({ id: style.id, palettes: [...style.palettes] })),
  catalog.styles.map((style) => ({ id: style.id, palettes: style.palettes.map((palette) => palette.id) })),
  "presentation/uiStyles.ts differs from ui-styles.json");

for (const style of catalog.styles) {
  const fixed = new Set(style.palettes.filter((palette) => !palette.follows).map((palette) => palette.id));
  for (const palette of style.palettes) {
    if (palette.follows) {
      assert.ok(fixed.has(palette.follows.light) && fixed.has(palette.follows.dark),
        `${style.id}/${palette.id} must follow two fixed palettes of the same style`);
      assert.equal(palette.startupBackground, undefined, `${style.id}/${palette.id} takes its background from the palettes it follows`);
    } else {
      assert.match(palette.startupBackground ?? "", /^#[0-9A-F]{6}$/, `${style.id}/${palette.id} needs an uppercase #RRGGBB startup background`);
    }
  }
}

// 归一化规则：风格不认识用默认风格；配色不在该风格里用该风格的默认配色；在就保留。
assert.deepEqual(normalizeUiAppearance(undefined, undefined), { uiStyle: "standard", theme: "system" });
assert.deepEqual(normalizeUiAppearance("unknown", "dark"), { uiStyle: "standard", theme: "dark" });
assert.deepEqual(normalizeUiAppearance("terminal", "system"), { uiStyle: "terminal", theme: "phosphor" });
assert.deepEqual(normalizeUiAppearance("soft", "dark"), { uiStyle: "soft", theme: "dark" });
assert.deepEqual(normalizeUiAppearance("soft", "lowContrast"), { uiStyle: "soft", theme: "system" });
assert.deepEqual(normalizeUiAppearance("classic", "lowContrast"), { uiStyle: "classic", theme: "lowContrast" });
assert.deepEqual(normalizeUiAppearance("classic", "paper"), { uiStyle: "classic", theme: "system" });

execFileSync(process.execPath, [resolve(frontendRoot, "scripts/generatePrebootAppearance.ts"), "--check"], { stdio: "inherit" });

console.log("ui style catalog contract ok");
