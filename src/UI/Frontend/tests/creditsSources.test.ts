// 致谢的说明和链接（docs/credits_sources.md）：
// - 每个子项目要么有官方原文（非空，带 https 来源 URL），要么没有说明；不同子项目之间没有同一段说明；
// - 原文不翻译：九种语言下每个子项目的说明一字不差；
// - 按钮标签只在固定词表里，同一子项目里按钮种类和地址都不重复，按固定顺序排；
// - 界面固定文字（分组、按钮词、“来源”、依赖表表头）九种语言都有。
import assert from "node:assert/strict";
import { creditSources, createCreditGroups } from "../src/i18n/settingsCredits.ts";
import { creditLinkKinds } from "../src/i18n/settingsTypes.ts";
import type { SettingsCopy } from "../src/i18n/settingsTypes.ts";

const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const;
const copies = new Map<string, SettingsCopy>();
for (const language of languages) {
  copies.set(language, (await import(`../src/i18n/settingsLocales/${language}.ts`)).default as SettingsCopy);
}

// 1. 原文和来源。
const seen = new Map<string, string>();
let described = 0;
let withoutDescription = 0;
for (const item of creditSources) {
  assert.ok(item.subjects.length > 0, `${item.name}: no subject`);
  for (const subject of item.subjects) {
    const where = `${item.name} → ${subject.name}`;
    if (subject.description === undefined) {
      withoutDescription++;
      continue;
    }
    described++;
    const { text, source } = subject.description;
    assert.ok(text.length > 0 && text === text.trim(), `${where}: empty or untrimmed description`);
    assert.doesNotMatch(text, /&(amp|lt|gt|quot|#\d+);/, `${where}: HTML entities must be decoded`);
    assert.equal(new URL(source).protocol, "https:", `${where}: description source must be an https URL`);
    const previous = seen.get(text);
    assert.equal(previous, undefined, `${where}: repeats the description of ${previous}`);
    seen.set(text, where);
  }
}
assert.ok(described > 0 && withoutDescription > 0, "Both described subjects and subjects without an official summary are expected");

// 2. 原文不翻译；3. 按钮词表、顺序、去重；4. 固定文字九种语言都有。
const english = copies.get("en-US")!;
for (const [language, copy] of copies) {
  const labels = copy.credits.linkLabels;
  assert.deepEqual(Object.keys(labels).sort(), [...creditLinkKinds].sort(), `${language}: link words`);
  assert.equal(labels.github, "GitHub");
  assert.equal(labels.nuget, "NuGet");
  assert.equal(labels.npm, "npm");
  for (const kind of ["official", "docs", "license"] as const) assert.ok(labels[kind].trim(), `${language}: ${kind} label`);
  const vocabulary = new Set(Object.values(labels));
  assert.equal(vocabulary.size, creditLinkKinds.length, `${language}: link words must be distinct`);
  assert.ok(copy.credits.sourceLabel.trim(), `${language}: source label`);
  for (const column of ["name", "version", "license", "source"] as const) {
    assert.ok(copy.credits.dependencyColumns[column].trim(), `${language}: dependency column ${column}`);
  }
  for (const group of Object.values(copy.credits.groups)) assert.ok(group.trim(), `${language}: group title`);
  for (const role of Object.values(copy.credits.roles)) assert.ok(role.trim(), `${language}: role`);
  if (language !== "en-US" && language !== "de-DE") {
    // 德语的 Website、Name、Version 和英语同形；其余语言的固定文字是真翻译。
    assert.notEqual(labels.official, english.credits.linkLabels.official, `${language}: website label is untranslated`);
    assert.notEqual(copy.credits.dependencyColumns.name, english.credits.dependencyColumns.name, `${language}: name column is untranslated`);
  }

  const groups = createCreditGroups(copy);
  const rendered = groups.flatMap((group) => group.items);
  assert.equal(rendered.length, creditSources.length);
  for (const item of rendered) {
    const source = creditSources.find((candidate) => candidate.name === item.name)!;
    assert.ok(source, `${language}: unknown item ${item.name}`);
    assert.equal(item.subjects.length, source.subjects.length);
    for (const [s, subject] of item.subjects.entries()) {
      const where = `${language}: ${item.name} → ${source.subjects[s].name}`;
      assert.deepEqual(subject.description, source.subjects[s].description ?? null, `${where}: description must be the untranslated original`);
      assert.ok(subject.links.length > 0, `${where}: no link`);
      assert.ok(subject.links.every((link) => vocabulary.has(link.label)), `${where}: label outside the fixed words`);
      assert.equal(new Set(subject.links.map((link) => link.kind)).size, subject.links.length, `${where}: repeated link kind`);
      assert.equal(new Set(subject.links.map((link) => link.href)).size, subject.links.length, `${where}: two buttons open the same page`);
      const order = subject.links.map((link) => creditLinkKinds.indexOf(link.kind));
      assert.deepEqual(order, [...order].sort((a, b) => a - b), `${where}: buttons out of the fixed order`);
      for (const link of subject.links) {
        assert.equal(new URL(link.href).protocol, "https:");
        assert.ok(link.accessibleName.startsWith(link.label) && link.accessibleName.includes(source.subjects[s].name),
          `${where}: accessible name must start with the visible word and name the subject`);
      }
      // 只有一个子项目且和行名相同时不重复写名字；其余都写出子项目名，按钮和子项目一一对应。
      const single = item.subjects.length === 1 && source.subjects[0].name === item.name;
      assert.equal(subject.name, single ? null : source.subjects[s].name, `${where}: subject name`);
    }
  }
}
process.stdout.write(`PASS credits sources: ${described} official descriptions with sources, ${withoutDescription} without, nine languages, fixed link words\n`);
