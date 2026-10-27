import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { cardExcludedFields, canonicalFieldNames } from "../src/features/monitor/canonicalFields.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import type { AppCopy } from "../src/i18n/copy/index.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";

/*
 * 规范字段的前端名单与九语文案必须和后端注册表一致。
 *
 * 名单按后端 MetricFieldRegistry.Definitions 的规则重建：先是 new(Code: MetricFieldCode.…) 表里每一行，
 * 再按 Definitions 列出的来源顺序（nvidia.nvml、amd.adlx、amd.smu）逐行套用 FindByTemplate 的改名，
 * 同名只留第一次。改名行的厂商覆盖原行厂商，用于"同资源同厂商内显示名不重复"的检查。
 */

interface RegistryField { name: string; resource: string; vendor: string; valueKind: string }

const registrySource = readFileSync(new URL("../../../ReadWriteCore/Fields/MetricFieldRegistry.cs", import.meta.url), "utf8");
// 参数之间允许换行缩进（长行在 MetricVendor 之后折行）。
// 每行末尾可带读取变化性（Volatility: ReadVolatility.Sampled，或带事件名的 ReadVolatility.EventDriven("…")）。
const rowPattern = /new\((?:Code:\s*)?MetricFieldCode\.(\w+),\s+(?:Name:\s*)?"([^"]+)",\s+(?:Resource:\s*)?MetricResource\.(\w+),\s+(?:Vendor:\s*)?MetricVendor\.(\w+),\s+(?:Unit:\s*)?"[^"]*",\s+(?:RequestTemplate:\s*)?"[^"]*"(?:,\s+Volatility:\s*ReadVolatility\.\w+(?:\("[^"]*"\))?)?\)(?:\s+\{([^}]*)\})?/g;
const baseRows = [...registrySource.matchAll(rowPattern)].map((match) => ({
  code: match[1],
  field: { name: match[2], resource: match[3], vendor: match[4], valueKind: /ValueKind = "([^"]+)"/.exec(match[5] ?? "")?.[1] ?? "float64" }
}));
assert.equal(baseRows.length, registrySource.match(/new\((?:Code:\s*)?MetricFieldCode\./g)?.length, "Every registry row must be parsed.");
assert.ok(baseRows.length >= 300, "Registry parse found too few rows.");

const sourceOrder = /new\[\] \{ ((?:"[^"]+"(?:, )?)+) \}\.SelectMany/.exec(registrySource)?.[1].match(/"([^"]+)"/g)?.map((value) => value.slice(1, -1));
assert.ok(sourceOrder && sourceOrder.length > 0, "Definitions source order not found.");
const renames = new Map<string, { name: string; vendor: string }>();
for (const match of registrySource.matchAll(/\(MetricFieldCode\.(\w+), "([^"]+)"\) => field with \{ Name = "([^"]+)", Vendor = MetricVendor\.(\w+)/g)) {
  renames.set(`${match[1]}|${match[2]}`, { name: match[3], vendor: match[4] });
}
assert.ok(renames.size > 0, "Source-specific field names not found.");

const registry: RegistryField[] = [];
const registryByName = new Map<string, RegistryField>();
for (const candidate of [
  ...baseRows.map((row) => row.field),
  ...sourceOrder.flatMap((sourceId) => baseRows.map((row) => {
    const rename = renames.get(`${row.code}|${sourceId}`);
    return rename ? { ...row.field, name: rename.name, vendor: rename.vendor } : row.field;
  }))
]) {
  if (registryByName.has(candidate.name)) continue;
  registryByName.set(candidate.name, candidate);
  registry.push(candidate);
}
for (const renamed of renames.values()) assert.ok(registryByName.has(renamed.name), `${renamed.name}: rename was not applied.`);

// 名单与后端注册表逐项、按顺序相等。
assert.deepEqual([...canonicalFieldNames], registry.map((field) => field.name), "canonicalFieldNames must equal the registry Definitions order.");
assert.ok(Object.isFrozen(canonicalFieldNames));

// 卡片排除表只含注册表字段，原始字节页全部排除，原因不为空。
assert.ok(Object.isFrozen(cardExcludedFields));
for (const [name, reason] of Object.entries(cardExcludedFields)) {
  assert.ok(registryByName.has(name), `${name}: excluded field is not in the registry.`);
  assert.ok(reason.trim().length > 0, `${name}: exclusion reason is empty.`);
}
for (const field of registry.filter((entry) => entry.valueKind === "bytes")) {
  assert.ok(field.name in cardExcludedFields, `${field.name}: raw byte pages must not be offered on cards.`);
}
assert.ok(Object.keys(cardExcludedFields).length < registry.length / 2, "Most fields must stay selectable.");

// 九种可选语言都为每个字段提供显示名和说明。
const languages: ConcreteAppLanguageMode[] = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"];
const copies = new Map<ConcreteAppLanguageMode, AppCopy>();
for (const language of languages) copies.set(language, await loadAppCopy(language));
const english = copies.get("en-US")!;
for (const language of languages) {
  const copy = copies.get(language)!;
  // loadAppCopy 载入失败会静默退回英文；这里要求真正载入了该语言的文案包。
  if (language !== "en-US") assert.notEqual(copy.fieldLabel, english.fieldLabel, `${language}: fell back to English field labels.`);
  assert.deepEqual(Object.keys(copy.fieldLabel).sort(), [...canonicalFieldNames].sort(), `${language}: field label keys differ from the registry.`);
  const labelsByScope = new Map<string, string>();
  for (const field of registry) {
    const entry = copy.fieldLabel[field.name];
    assert.ok(entry, `${language}: ${field.name} has no field label.`);
    for (const part of ["label", "hint"] as const) {
      assert.equal(typeof entry[part], "string", `${language}: ${field.name}.${part} is not text.`);
      assert.ok(entry[part].trim().length > 0, `${language}: ${field.name}.${part} is empty.`);
      if (!/^(?:zh|ja)-/.test(language)) {
        assert.doesNotMatch(entry[part], /\p{Script=Han}/u, `${language}: ${field.name}.${part} contains Han characters.`);
      }
    }
    const scope = `${field.resource}|${field.vendor}|${entry.label}`;
    assert.ok(!labelsByScope.has(scope), `${language}: "${entry.label}" is used by both ${labelsByScope.get(scope)} and ${field.name} (${field.resource}/${field.vendor}).`);
    labelsByScope.set(scope, field.name);
  }
}

// 繁体中文不是简体文案的复制：含简繁不同字形的条目必须改写。
const simplified = copies.get("zh-CN")!.fieldLabel;
const traditional = copies.get("zh-TW")!.fieldLabel;
assert.notEqual(traditional, simplified, "zh-TW must not reuse the Simplified field label table.");
const simplifiedOnlyGlyphs = /[设监软调风转线读载应显频压计内据错误驱动网络发记录态状温储换为备结过时间实际]/u;
let convertible = 0;
for (const name of canonicalFieldNames) {
  for (const part of ["label", "hint"] as const) {
    if (!simplifiedOnlyGlyphs.test(simplified[name][part])) continue;
    convertible++;
    assert.notEqual(traditional[name][part], simplified[name][part], `zh-TW: ${name}.${part} is still Simplified Chinese.`);
    assert.doesNotMatch(traditional[name][part], simplifiedOnlyGlyphs, `zh-TW: ${name}.${part} contains Simplified-only glyphs.`);
  }
}
assert.ok(convertible > 100, "The zh-TW comparison covered too few entries.");
for (const name of ["memory_all_physical_used_bytes", "gpu_all_memory_used_reported_bytes", "cpu_all_frequency_reported_mhz", "fan_all_speed_rpm"]) {
  assert.notEqual(traditional[name].label, simplified[name].label, `zh-TW: ${name} label must use Taiwan wording.`);
}

console.log(`field labels: ${registry.length} fields, ${Object.keys(cardExcludedFields).length} excluded from cards, ${languages.length} languages`);
