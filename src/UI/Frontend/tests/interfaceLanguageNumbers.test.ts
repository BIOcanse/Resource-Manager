import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { formatPollingInterval, formatRate } from "../src/features/deviceTopology/adapters/adapterEvidence.ts";
import { formatFieldValue } from "../src/presentation/fieldValueFormat.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { metricCardReading, metricDisplayValue, resourceTableCellText, resourceTableCellPresentation } from "../src/presentation/metricLabels.ts";
import { formatBytePair, formatBitsPerSecond, formatBytes } from "../src/presentation/byteUnits.ts";
import { formatLegacyMetricNumber } from "../src/presentation/legacyMetricNumber.ts";
import { formatFixedDigits, formatInterfaceNumber, formatSignificantDigits, formatTrimmedDigits, interfaceDecimalSeparator } from "../src/presentation/interfaceNumbers.ts";
import { userFacingDateTime } from "../src/presentation/userFacingText.ts";

// 数字按界面语言格式化，不按系统或浏览器的区域设置：德语界面开在中文 Windows 上也写 1.234,5。
const count = { name: "f", unit: "count", valueKind: "uint64", numericValue: null, integerValue: 1234567n, booleanValue: null, observedAt: null };
const formatted = new Map<string, string>();
const languages = languageOptions.filter(option => option.id !== "system");
assert.equal(languages.length, 9);
for (const { id: language } of languages) {
  await applyLanguage(language);
  assert.equal(currentLanguage(), language);
  const rate = formatRate(1234.5, "Hz");
  assert.equal(rate, `${(1234.5).toLocaleString(language, { maximumFractionDigits: 2 })} Hz`, `${language} rate`);
  assert.equal(formatPollingInterval(1500), `${(1.5).toLocaleString(language, { maximumFractionDigits: 3 })} ms`, `${language} polling`);
  assert.equal(formatPollingInterval(125.5), `${(125.5).toLocaleString(language, { maximumFractionDigits: 1 })} us`, `${language} polling us`);
  assert.equal(formatFieldValue(count), (1234567n).toLocaleString(language), `${language} count`);
  formatted.set(language, rate);
  const decimal = (88.3).toLocaleString(language, { useGrouping: false, minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const metric = { id: "cpu.usage", unit: "%", numericValue: 88.3, status: 1 };
  assert.equal(formatLegacyMetricNumber(88.3, 1), decimal, `${language} alias number owner`);
  assert.equal(metricDisplayValue(metric), `${decimal}%`, `${language} metric alias`);
  const reading = metricCardReading(null, metric);
  assert.equal(reading.value + reading.gap + reading.unit, `${decimal}%`, `${language} card reading`);
  assert.equal(formatFieldValue({ ...count, integerValue: null, numericValue: 88.3, unit: "%" }), `${decimal}%`, `${language} canonical field`);
  assert.equal(formatFieldValue({ ...count, integerValue: null, numericValue: 1.15, unit: "%" }), `${(1.1).toLocaleString(language, { useGrouping: false })}%`, `${language} keeps existing field rounding`);
  assert.equal(resourceTableCellText({ value: 88.3, unit: "%" }), `${decimal}%`, `${language} resource table`);
  // The column maximum is twice this reading, so the cell's heat is exactly half.
  const presentation = resourceTableCellPresentation({ value: 88.3, unit: "%",
    basis: "frame basis" }, "cpu", { label: "CPU", basis: "column basis" }, 88.3 * 2);
  assert.deepEqual(presentation, { display: `${decimal}%`, title: `CPU: ${decimal}%\ncolumn basis\nframe basis`,
    heat: 50, privateHeat: 50, unavailable: false }, `${language} shared cell text, tooltip and heat`);
  const failure = resourceTableCellPresentation({ value: null, unit: "%", availability: "Unavailable",
    unavailableReason: "fixture-unavailable" }, "cpu", { label: "CPU", basis: undefined }, 100);
  // 未知原因码不进格子：格子写“无读数”，原始码只在悬停提示里（资源表和资源条共用的原因族表，见 resourceBarUnavailableReason.ts）。
  assert.equal(failure.display, uiText.dashboard.noReading, `${language} failure replaces the previous reading`);
  assert.ok(failure.title.includes(uiText.backendMessage.reasonCode("fixture-unavailable")), `${language} raw reason code on hover`);
  assert.equal(failure.unavailable, true);
  const fixed = (1.5).toLocaleString(language, { useGrouping: false, minimumFractionDigits: 2, maximumFractionDigits: 2 });
  assert.equal(formatBytes(1.5 * 1024 ** 3, "memory", "native"), `${fixed} GiB`, `${language} bytes`);
  assert.equal(formatBytePair(1.5 * 1024 ** 3, 2.5 * 1024 ** 3, "memory", "native"), `${fixed} / ${(2.5).toLocaleString(language, { useGrouping: false, minimumFractionDigits: 2, maximumFractionDigits: 2 })} GiB`, `${language} byte pair`);
  assert.equal(formatBitsPerSecond(1500), `${fixed} kbit/s`, `${language} bit rate`);
  assert.notEqual(resourceTableCellText({ value: 1.25e-25, unit: "" }), "0", `${language} small unitless values stay nonzero`);
  for (const value of [1.15, 1.005, -1.15, -0.01, -0, 1234567.89]) {
    for (const digits of [0, 1, 2, 3, 4]) {
      assert.equal(formatFixedDigits(value, digits), Number(value.toFixed(digits)).toLocaleString(language, { useGrouping: false, minimumFractionDigits: digits, maximumFractionDigits: digits }), `${language} fixed ${value}/${digits}`);
      assert.equal(formatTrimmedDigits(value, digits), Number(value.toFixed(digits)).toLocaleString(language, { useGrouping: false, maximumFractionDigits: digits }), `${language} trimmed ${value}/${digits}`);
    }
  }
  const timestamp = "2026-10-26T12:34:56Z";
  assert.equal(userFacingDateTime(timestamp), new Date(timestamp).toLocaleString(language), `${language} existing date formatting`);
}
assert.notEqual(formatted.get("de-DE"), formatted.get("en-US"), "German and English format the same number differently");

// Count real constructor calls: values remain fresh, equivalent options share
// a formatter, and switching back reuses that language's formatter.
await applyLanguage("fr-FR");
const OriginalNumberFormat = Intl.NumberFormat;
const originalFormatToParts = Intl.NumberFormat.prototype.formatToParts;
let constructions = 0;
let separatorReads = 0;
Intl.NumberFormat.prototype.formatToParts = function (this: Intl.NumberFormat, ...args: Parameters<typeof originalFormatToParts>) {
  separatorReads++;
  return originalFormatToParts.apply(this, args);
};
Intl.NumberFormat = new Proxy(OriginalNumberFormat, {
  construct(target, args) { constructions++; return Reflect.construct(target, args); }
});
try {
  for (let read = 0; read < 20; read++) {
    assert.equal(interfaceDecimalSeparator(), ",");
  }
  assert.equal(separatorReads, 0, "a language's known separator is not rebuilt for each reading");
  const first = formatFixedDigits(1.25, 12);
  assert.notEqual(formatFixedDigits(2.25, 12), first, "readings are never cached");
  formatInterfaceNumber(3.25, { maximumFractionDigits: 12, minimumFractionDigits: 12, useGrouping: false });
  assert.equal(constructions, 1, "equivalent options reuse one formatter");
  formatTrimmedDigits(1.25, 12);
  formatSignificantDigits(1.25, 12);
  assert.equal(constructions, 3, "distinct precision options have distinct formatters");
  await applyLanguage("en-US");
  assert.match(formatFixedDigits(1.25, 12), /\./);
  assert.equal(interfaceDecimalSeparator(), ".");
  assert.equal(constructions, 4, "the next format uses the new language");
  await applyLanguage("fr-FR");
  assert.equal(formatFixedDigits(1.25, 12), first);
  assert.equal(interfaceDecimalSeparator(), ",");
  assert.equal(constructions, 4, "switching back reuses its formatter");
} finally {
  Intl.NumberFormat = OriginalNumberFormat;
  Intl.NumberFormat.prototype.formatToParts = originalFormatToParts;
}

// 源码里不允许再出现不带语言的区域格式化：toLocaleString()、toLocaleString(undefined, …)、Intl.*Format() / (undefined …)。
// 交互协议引擎是原样拷贝的外部代码，生成目录是构建产物，都不在扫描范围内。
const sourceRoot = fileURLToPath(new URL("../src/", import.meta.url));
const systemLocaleCall = /\.toLocale(?:Date|Time)?String\(\s*(?:\)|undefined\b)|new Intl\.(?:NumberFormat|DateTimeFormat|PluralRules|RelativeTimeFormat|ListFormat)\(\s*(?:\)|undefined\b)/g;
const pending = [sourceRoot];
const offenders: string[] = [];
const ownerOffenders: string[] = [];
const numberOwnerCall = /\.toLocaleString\(\s*currentLanguage\s*\(|new\s+Intl\.NumberFormat\s*\(/g;
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
    if (relative(sourceRoot, path).replaceAll("\\", "/") !== "presentation/interfaceNumbers.ts") {
      for (const match of source.matchAll(numberOwnerCall)) {
        ownerOffenders.push(`${relative(sourceRoot, path)}:${source.slice(0, match.index).split("\n").length} ${match[0]}`);
      }
    }
    for (const match of source.matchAll(systemLocaleCall)) {
      offenders.push(`${relative(sourceRoot, path)}:${source.slice(0, match.index).split("\n").length} ${match[0]}`);
    }
  }
}
assert.deepEqual(offenders, [], "locale formatting must name the interface language");
assert.deepEqual(ownerOffenders, [], "interface-language number formatting must use the cached presentation owner");
console.log("interfaceLanguageNumbers: numbers follow the interface language");
