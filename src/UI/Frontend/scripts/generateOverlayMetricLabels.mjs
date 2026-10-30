import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { zhAppCopy } from "../src/i18n/copy/zh/index.ts";
import { enAppCopy } from "../src/i18n/copy/en/index.ts";
import { zhAppCopy as zhTwAppCopy } from "../src/i18n/copy/zh-TW/index.ts";
import ja from "../src/i18n/copy/locales/ja-JP.ts";
import ko from "../src/i18n/copy/locales/ko-KR.ts";
import fr from "../src/i18n/copy/locales/fr-FR.ts";
import de from "../src/i18n/copy/locales/de-DE.ts";
import es from "../src/i18n/copy/locales/es-ES.ts";
import ru from "../src/i18n/copy/locales/ru-RU.ts";

const copies = {
  "zh-CN": zhAppCopy,
  "zh-TW": zhTwAppCopy,
  "en-US": enAppCopy,
  "ja-JP": ja,
  "ko-KR": ko,
  "fr-FR": fr,
  "de-DE": de,
  "es-ES": es,
  "ru-RU": ru
};
const indexSentinel = "7319";
const ids = Object.keys(zhAppCopy.metricLabel).sort();
const labels = {};
for (const id of ids) {
  labels[id] = {};
  for (const [language, copy] of Object.entries(copies)) {
    const entry = copy.metricLabel[id];
    if (entry === undefined) throw new Error(`${language}: missing metric ${id}`);
    // The overlay fills {index} with a number, so render with a number and swap it back: the copy's own spacing rule
    // (i18n/cjkSpacing.ts puts a space between a CJK character and a digit) then applies exactly as at run time.
    if (typeof entry === "function") {
      const rendered = entry(indexSentinel);
      if (rendered.split(indexSentinel).length !== 2) throw new Error(`${language}: ${id} must contain the index exactly once`);
      labels[id][language] = rendered.replace(indexSentinel, "{index}");
    } else {
      labels[id][language] = entry;
    }
  }
}
// 浮层项的其余文字（OverlayItemLabels.cs 按 overlayItemLabels.ts 的顺序查）：目标软件项的模板、进程字段名、取不到值的原因。
const nameSentinel = "NAME7319";
const softwareTemplate = {};
for (const [language, copy] of Object.entries(copies)) {
  const rendered = copy.performanceOverlay.itemSoftware(nameSentinel);
  if (rendered.split(nameSentinel).length !== 2) throw new Error(`${language}: itemSoftware must contain the name exactly once`);
  softwareTemplate[language] = rendered.replace(nameSentinel, "{name}");
}
const keyedTable = (pick, what) => {
  const table = {};
  for (const key of Object.keys(pick(zhAppCopy)).sort()) {
    table[key] = {};
    for (const [language, copy] of Object.entries(copies)) {
      const text = pick(copy)[key];
      if (typeof text !== "string" || !text.trim()) throw new Error(`${language}: missing ${what} ${key}`);
      table[key][language] = text;
    }
  }
  return table;
};
const itemLabels = {
  softwareTemplate,
  processFields: keyedTable((copy) => copy.resourceTable.processFieldNames, "process field"),
  reasons: keyedTable((copy) => copy.performanceOverlay.itemReasons, "overlay item reason")
};

const outputs = [
  ["../../Core/Overlay/metric-labels.json", JSON.stringify(labels, null, 2) + "\n", "Overlay metric labels"],
  ["../../Core/Overlay/overlay-item-labels.json", JSON.stringify(itemLabels, null, 2) + "\n", "Overlay item labels"]
];
for (const [path, output, name] of outputs) {
  const destination = fileURLToPath(new URL(path, import.meta.url));
  if (process.argv.includes("--check")) {
    const existing = await readFile(destination, "utf8").catch(() => null);
    if (existing !== output)
      throw new Error(`${name} differ from the frontend copy. Regenerate the manifest.`);
  } else {
    await writeFile(destination, output, "utf8");
  }
}
