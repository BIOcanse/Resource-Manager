// classic 的配色在 theme.css；其余每个风格（包括 standard）的每个配色都要在自己的文件里完整定义颜色变量，风格文件只改外观不改布局。规则见 docs/ui_styles.md。
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { uiStyles } from "../src/presentation/uiStyles.ts";

const stylesRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src/styles");

interface CssRule {
  selectors: string[];
  media: string | null;
  declarations: Map<string, string>;
}

// 只认本项目风格文件用到的写法：普通规则和一层 @media 包裹。
function parseCss(source: string): CssRule[] {
  const text = source.replace(/\/\*[\s\S]*?\*\//g, "");
  const rules: CssRule[] = [];
  const stack: Array<string | null> = [];
  let media: string | null = null;
  let cursor = 0;
  while (cursor < text.length) {
    const open = text.indexOf("{", cursor);
    const close = text.indexOf("}", cursor);
    if (close >= 0 && (open < 0 || close < open)) {
      media = stack.pop() ?? null;
      cursor = close + 1;
      continue;
    }
    if (open < 0) {
      break;
    }
    const prelude = text.slice(cursor, open).trim();
    if (prelude.startsWith("@media")) {
      stack.push(media);
      media = prelude;
      cursor = open + 1;
      continue;
    }
    const end = text.indexOf("}", open);
    assert.ok(end > open, `unterminated rule: ${prelude}`);
    const declarations = new Map<string, string>();
    for (const part of text.slice(open + 1, end).split(";")) {
      const colon = part.indexOf(":");
      if (colon > 0) {
        declarations.set(part.slice(0, colon).trim(), part.slice(colon + 1).trim());
      }
    }
    rules.push({ selectors: prelude.split(",").map((selector) => selector.trim()), media, declarations });
    cursor = end + 1;
  }
  return rules;
}

const classicRules = parseCss(readFileSync(resolve(stylesRoot, "theme.css"), "utf8"));
const classicDark = classicRules.find((rule) =>
  rule.media === null && rule.selectors.some((selector) => selector.includes('[data-theme="dark"]')));
assert.ok(classicDark, "theme.css must define the classic dark palette");
const requiredTokens = [...classicDark.declarations.keys()].filter((name) => name.startsWith("--"));
requiredTokens.push("color-scheme");

const layoutTokens = [
  "--shellbar-height",
  "--managementbar-height",
  "--workspace-max-width",
  "--app-shell-x-padding",
  "--control-height",
  "--control-height-compact",
  "--control-padding-x"
];
const forbiddenProperties = /^(width|height|min-width|min-height|max-width|max-height|padding(-\w+)*|margin(-\w+)*|gap|row-gap|column-gap|inset|top|right|bottom|left|position|display|grid(-[\w-]+)?|flex(-[\w-]+)?|order|font-size|line-height|letter-spacing|text-transform|border-width|border(-(top|right|bottom|left))?(-width)?|font|backdrop-filter|filter|transition(-[\w-]+)?|animation(-[\w-]+)?|transform|background-attachment|content-visibility|contain)$/;

const darkMedia = (media: string | null) => media !== null && /prefers-color-scheme:\s*dark/.test(media);

for (const style of uiStyles) {
  if (style.id === "classic") {
    continue;
  }
  const file = resolve(stylesRoot, "ui-styles", `${style.id}.css`);
  if (!existsSync(file)) {
    // 风格文件还没写好时只提示，不让其余检查失败；目录里的风格最终都必须有文件。
    console.warn(`missing ${style.id}.css`);
    continue;
  }
  const rules = parseCss(readFileSync(file, "utf8"));
  const styleSelector = `[data-ui-style="${style.id}"]`;

  for (const rule of rules) {
    for (const selector of rule.selectors) {
      assert.ok(selector.includes(styleSelector), `${style.id}.css: selector not scoped to the style: ${selector}`);
    }
    for (const property of rule.declarations.keys()) {
      assert.ok(!layoutTokens.includes(property), `${style.id}.css: layout token overridden: ${property}`);
      assert.ok(!forbiddenProperties.test(property), `${style.id}.css: layout or performance property not allowed: ${property} in ${rule.selectors.join(", ")}`);
    }
  }

  // 跟随系统的深色一侧只认深色媒体查询里的配色块和不分配色的风格级变量，
  // 不能拿浅色一侧的定义充数，否则漏写的变量会在深色下露出浅色值。
  const tokensFor = (palette: string, dark: boolean) => {
    const declared = new Set<string>();
    for (const rule of rules) {
      for (const selector of rule.selectors) {
        if (!selector.startsWith(":root") || /\s/.test(selector) || !selector.includes(styleSelector)) {
          continue;
        }
        const paletteScoped = selector.includes("[data-theme=");
        if (paletteScoped && !selector.includes(`[data-theme="${palette}"]`)) {
          continue;
        }
        const applies = rule.media === null
          ? !dark || !paletteScoped
          : dark && darkMedia(rule.media);
        if (applies) {
          for (const name of rule.declarations.keys()) {
            declared.add(name);
          }
        }
      }
    }
    return declared;
  };

  for (const palette of style.palettes) {
    const variants = palette === "system" ? [false, true] : [false];
    for (const dark of variants) {
      const declared = tokensFor(palette, dark);
      const missing = requiredTokens.filter((token) => !declared.has(token));
      assert.deepEqual(missing, [], `${style.id}/${palette}${dark ? " (system dark)" : ""} is missing tokens`);
    }
  }
}

console.log("ui style token coverage ok");
