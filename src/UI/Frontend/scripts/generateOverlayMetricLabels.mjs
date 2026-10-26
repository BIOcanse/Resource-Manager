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
const output = JSON.stringify(labels, null, 2) + "\n";
const destination = fileURLToPath(new URL("../../Core/Overlay/metric-labels.json", import.meta.url));
if (process.argv.includes("--check")) {
  if (await readFile(destination, "utf8") !== output)
    throw new Error("Overlay metric labels differ from the frontend copy. Regenerate the manifest.");
} else {
  await writeFile(destination, output, "utf8");
}
