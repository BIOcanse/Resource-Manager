import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { formatPollingInterval, formatRate } from "../src/features/deviceTopology/adapters/adapterEvidence.ts";
import { formatFieldValue } from "../src/presentation/fieldValueFormat.ts";
import { applyLanguage, currentLanguage } from "../src/text.ts";

// 数字按界面语言格式化，不按系统或浏览器的区域设置：德语界面开在中文 Windows 上也写 1.234,5。
const count = { name: "f", unit: "count", valueKind: "uint64", numericValue: null, integerValue: 1234567n, booleanValue: null, observedAt: null };
const formatted = new Map<string, string>();
for (const language of ["de-DE", "en-US", "fr-FR"] as const) {
  await applyLanguage(language);
  assert.equal(currentLanguage(), language);
  const rate = formatRate(1234.5, "Hz");
  assert.equal(rate, `${(1234.5).toLocaleString(language, { maximumFractionDigits: 2 })} Hz`, `${language} rate`);
  assert.equal(formatPollingInterval(1500), `${(1.5).toLocaleString(language, { maximumFractionDigits: 3 })} ms`, `${language} polling`);
  assert.equal(formatPollingInterval(125.5), `${(125.5).toLocaleString(language, { maximumFractionDigits: 1 })} us`, `${language} polling us`);
  assert.equal(formatFieldValue(count), (1234567n).toLocaleString(language), `${language} count`);
  formatted.set(language, rate);
}
assert.notEqual(formatted.get("de-DE"), formatted.get("en-US"), "German and English format the same number differently");

// 源码里不允许再出现不带语言的区域格式化：toLocaleString()、toLocaleString(undefined, …)、Intl.*Format() / (undefined …)。
// 交互协议引擎是原样拷贝的外部代码，生成目录是构建产物，都不在扫描范围内。
const sourceRoot = fileURLToPath(new URL("../src/", import.meta.url));
const systemLocaleCall = /\.toLocale(?:Date|Time)?String\(\s*(?:\)|undefined\b)|new Intl\.(?:NumberFormat|DateTimeFormat|PluralRules|RelativeTimeFormat|ListFormat)\(\s*(?:\)|undefined\b)/g;
const pending = [sourceRoot];
const offenders: string[] = [];
let visitedDirectories = 0;
while (pending.length > 0) {
  const directory = pending.pop()!;
  visitedDirectories += 1;
  assert.ok(visitedDirectories < 2000, "source scan exceeded its directory limit");
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== "protocol" && entry.name !== "generated") pending.push(path);
      continue;
    }
    if (!/\.(?:ts|tsx)$/.test(entry.name)) continue;
    const source = readFileSync(path, "utf8");
    for (const match of source.matchAll(systemLocaleCall)) {
      offenders.push(`${relative(sourceRoot, path)}:${source.slice(0, match.index).split("\n").length} ${match[0]}`);
    }
  }
}
assert.deepEqual(offenders, [], "locale formatting must name the interface language");
console.log("interfaceLanguageNumbers: numbers follow the interface language");
