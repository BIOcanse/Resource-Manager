// 中文（简、繁）和日文文案的中西文间距：一条规则，见 src/i18n/cjkSpacing.ts。
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import ts from "typescript";
import { cjkLatinGaps, cjkText } from "../src/i18n/cjkSpacing.ts";

// 标签只在插值紧贴汉字或假名的边界上补空格。
const gpu = "GPU 调度数据";
assert.equal(cjkText`正在加载${gpu}`, "正在加载 GPU 调度数据");
assert.equal(cjkText`正在加载${"内存"}`, "正在加载内存");
assert.equal(cjkText`磁盘${1} 温度`, "磁盘 1 温度");
assert.equal(cjkText`${5}秒后应用：${"省电"}`, "5 秒后应用：省电");
assert.equal(cjkText`${"Chrome"}的后台${"CPU"}占用较高`, "Chrome 的后台 CPU 占用较高");
assert.equal(cjkText`${"NVIDIA"}の主な情報`, "NVIDIA の主な情報");
assert.equal(cjkText`更换${"槽位 1"}：${"CPU"}`, "更换槽位 1：CPU");
assert.equal(cjkText`共 ${3} 项`, "共 3 项", "an interpolation the template already spaces keeps its spacing");
assert.equal(cjkText`正在加载${""}`, "正在加载");
assert.deepEqual(cjkLatinGaps("CPU 风扇 · 1 秒平均"), []);
assert.equal(cjkLatinGaps("CPU风扇").length, 1);
assert.equal(cjkLatinGaps("ノートPCメーカー").length, 2);
assert.deepEqual(cjkLatinGaps("GPU 電力・1 秒平均"), [], "the katakana middle dot is punctuation");

// 中文和日文文案文件：写死的文字遵守规则；插值紧贴汉字或假名的模板必须用 cjkText 标签。
const i18nRoot = new URL("../src/i18n/", import.meta.url);
const catalogFiles = [
  ...readdirSync(new URL("copy/zh/", i18nRoot)).filter((name) => name.endsWith(".ts")).map((name) => `copy/zh/${name}`),
  ...readdirSync(new URL("copy/zh-TW/", i18nRoot)).filter((name) => name.endsWith(".ts")).map((name) => `copy/zh-TW/${name}`),
  ...readdirSync(new URL("copy/locales/", i18nRoot))
    .filter((name) => name.startsWith("ja-JP") || name === "zh-TW.ts").map((name) => `copy/locales/${name}`),
  "settingsLocales/zh-CN.ts",
  "settingsLocales/zh-TW.ts",
  "settingsLocales/ja-JP.ts",
  "settingsLocaleFactory.ts"
];
const cjkCharacter = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー]/u;
const problems: string[] = [];
let templates = 0;
let tagged = 0;
for (const file of catalogFiles) {
  const source = ts.createSourceFile(file, readFileSync(new URL(file, i18nRoot), "utf8"), ts.ScriptTarget.Latest, true);
  const where = (node: ts.Node) => `${file}:${source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1}`;
  const stack: ts.Node[] = [source];
  let visited = 0;
  while (stack.length > 0) {
    if (++visited > 500_000) throw new Error(`${file}: syntax tree exceeds the test traversal bound`);
    const node = stack.pop()!;
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      if (cjkLatinGaps(node.text).length > 0) problems.push(`${where(node)}: ${JSON.stringify(node.text)}`);
    } else if (ts.isTemplateExpression(node)) {
      templates += 1;
      const parts = [node.head.text, ...node.templateSpans.map((span) => span.literal.text)];
      for (const part of parts) {
        if (cjkLatinGaps(part).length > 0) problems.push(`${where(node)}: ${JSON.stringify(part)}`);
      }
      const attached = parts.some((part, index) =>
        (index < parts.length - 1 && cjkCharacter.test(part.slice(-1)))
        || (index > 0 && cjkCharacter.test(Array.from(part)[0] ?? "")));
      const tag = ts.isTaggedTemplateExpression(node.parent) ? node.parent.tag.getText(source) : null;
      if (tag === "cjkText") tagged += 1;
      if (attached && tag !== "cjkText") {
        problems.push(`${where(node)}: an interpolation touches a CJK character without cjkText: ${node.getText(source).slice(0, 80)}`);
      }
    }
    node.forEachChild((child) => { stack.push(child); });
  }
}
assert.deepEqual(problems, [], `CJK/Latin spacing problems:\n${problems.join("\n")}`);
assert.ok(tagged >= 99, `expected the tagged zh, zh-TW and ja templates, found ${tagged}`);
console.log(`cjk spacing ok: ${catalogFiles.length} files, ${templates} templates, ${tagged} tagged`);
