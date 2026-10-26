// 数量文案的单复数：一条机制（src/i18n/plural.ts），九种语言都按 0、1、2、5、21 渲染核对。
import assert from "node:assert/strict";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { plural, pluralCategories, type PluralLocale } from "../src/i18n/plural.ts";

const locales = Object.keys(pluralCategories) as PluralLocale[];

// 1. 类别表就是 Intl 给出的类别（顺序无关）。
for (const locale of locales) {
  assert.deepEqual(
    [...pluralCategories[locale]].sort(),
    [...new Intl.PluralRules(locale).resolvedOptions().pluralCategories].sort(),
    `${locale} plural categories`);
}

// 2. 形式按类别选出，{count} 换成数字本身；缺一种形式时明确报错，不拿别的形式顶替。
assert.equal(plural("de-DE", 1, { one: "{count} Eintrag", other: "{count} Einträge" }), "1 Eintrag");
assert.equal(plural("de-DE", 21, { one: "{count} Eintrag", other: "{count} Einträge" }), "21 Einträge");
const ruCores = { one: "{count} ядро", few: "{count} ядра", many: "{count} ядер", other: "{count} ядра" };
for (const [n, text] of [[0, "0 ядер"], [1, "1 ядро"], [2, "2 ядра"], [5, "5 ядер"], [11, "11 ядер"], [21, "21 ядро"],
  [22, "22 ядра"], [25, "25 ядер"], [111, "111 ядер"], [1.5, "1.5 ядра"]] as const) {
  assert.equal(plural("ru-RU", n, ruCores), text);
}
assert.equal(plural("fr-FR", 0, { one: "{count} fichier", many: "{count} de fichiers", other: "{count} fichiers" }), "0 fichier");
assert.equal(plural("fr-FR", 1000000, { one: "{count} fichier", many: "{count} de fichiers", other: "{count} fichiers" }),
  "1000000 de fichiers");
assert.equal(plural("zh-CN", 1, { other: "{count} 个文件" }), "1 个文件");
assert.throws(() => plural("ru-RU", 2, { one: "{count} ядро", many: "{count} ядер", other: "{count} ядра" } as never),
  /No few plural form for ru-RU/);

// 3. 目录里所有带数量参数的文案函数。参数名就是数量的名字；下面这些函数里同名参数不是需要变化的数量。
const countParameters = new Set(["count", "total", "connected", "limit", "host", "downstream", "interfaces", "controllers",
  "devices", "softwareCount", "processCount", "files", "folders", "succeeded", "done", "gpuCount"]);
const notCountPhrases: Record<string, string> = {
  freeOfTotal: "byte sizes formatted by the caller",
  usedOfTotal: "byte sizes formatted by the caller",
  filterAll: "a tally after a fixed filter name",
  filterTrusted: "a tally after a fixed filter name",
  filterUntrusted: "a tally after a fixed filter name",
  rulesUnavailable: "a tally in parentheses",
  activeCount: "the number is followed by an invariant phrase (in progress, laufend, en cours, en curso) or a label",
  taskCenterActive: "the number is followed by an invariant phrase or a label",
  externalSummary: "the numbers are followed by place phrases (on device, expansion) that never agree"
};

type CountFunction = { path: string; fn: (...args: unknown[]) => unknown; parameters: string[] };

function parameterNames(fn: Function) {
  const match = /^\s*(?:async\s*)?(?:function\s*\w*\s*)?\(([^)]*)\)|^\s*(\w+)\s*=>/.exec(fn.toString());
  return (match?.[1] ?? match?.[2] ?? "").split(",").map((part) => part.trim().split(/[\s:=?]/)[0]).filter(Boolean);
}

function countFunctions(roots: Array<[string, unknown]>) {
  const found: CountFunction[] = [];
  const stack = [...roots];
  const seen = new Set<object>();
  let visited = 0;
  while (stack.length > 0) {
    assert.ok(++visited < 200000, "copy walk exceeded its node limit");
    const [path, value] = stack.pop()!;
    if (typeof value === "function") {
      const parameters = parameterNames(value);
      const key = path.split(".").at(-1)!;
      if (parameters.some((name) => countParameters.has(name)) && !(key in notCountPhrases)) {
        found.push({ path, fn: value as CountFunction["fn"], parameters });
      }
    } else if (value && typeof value === "object" && !seen.has(value)) {
      seen.add(value);
      for (const [key, child] of Object.entries(value)) stack.push([`${path}.${key}`, child]);
    }
  }
  return found.sort((a, b) => a.path.localeCompare(b.path));
}

// 数字前面是标签（"Dateien: 5"、"Выбрано: 5"）、或单独一个数字、或是分数的分子（"3 / 5"），都没有跟它一致变化的词。
function rawUseIsLabelled(source: string, parameter: string) {
  const pattern = new RegExp(`\\$\\{${parameter}\\}`, "g");
  for (const match of source.matchAll(pattern)) {
    const before = source.slice(0, match.index);
    const after = source.slice(match.index + match[0].length);
    if (/^\s*\/\s*(?:\$\{\w+\}|\{count\})/.test(after)) continue;
    // 标签后面可以是单个数字，也可以是"分子 / 分母"（"Procesos finalizados: 3 / 5."）。
    const labelled = /[:：]\s*(?:\$\{\w+\}\s*\/\s*)?$/.test(before) && /^(?:\s*[·.,;)/]|\s*`|\s*$)/.test(after);
    const alone = /`$/.test(before) && /^`/.test(after);
    if (!labelled && !alone) return false;
  }
  return true;
}

const samples = [0, 1, 2, 5, 21];
const extraSamples: Partial<Record<PluralLocale, number[]>> = { "ru-RU": [11, 22, 25, 111], "fr-FR": [1000000], "es-ES": [1000000] };
const rendered = new Map<string, string>();
let checkedFunctions = 0;
let referencePaths: string | undefined;
for (const locale of locales) {
  const functions = countFunctions([["app", await loadAppCopy(locale)], ["settings", await loadSettingsText(locale)]]);
  assert.ok(functions.length >= 40, `${locale}: only ${functions.length} count functions found`);
  const paths = functions.map((entry) => entry.path).join("\n");
  referencePaths ??= paths;
  assert.equal(paths, referencePaths, `${locale} has a different set of count functions than ${locales[0]}`);
  const inflected = pluralCategories[locale].length > 1;
  for (const { path, fn, parameters } of functions) {
    const source = fn.toString();
    for (const name of parameters.filter((parameter) => countParameters.has(parameter))) {
      const throughPlural = new RegExp(`plural\\(\\s*"${locale}",\\s*${name}\\s*,`).test(source);
      if (inflected) {
        assert.ok(throughPlural || rawUseIsLabelled(source, name),
          `${locale} ${path}: ${name} sits next to a word that agrees with it; choose the form with plural()`);
      } else {
        assert.ok(!throughPlural, `${locale} ${path}: ${locale} has a single plural category and needs no forms`);
      }
    }
    for (const n of [...samples, ...(extraSamples[locale] ?? [])]) {
      const args = parameters.map((parameter) => countParameters.has(parameter) ? n : `«${parameter}»`);
      const text = fn(...args);
      assert.equal(typeof text, "string", `${locale} ${path}(${n})`);
      assert.ok(!String(text).includes("{count}"), `${locale} ${path}(${n}) left a placeholder: ${text}`);
      assert.ok(new RegExp(`(^|\\D)${n}(\\D|$)`).test(String(text)), `${locale} ${path}(${n}) lost the number: ${text}`);
      rendered.set(`${locale} ${path} ${n}`, String(text));
    }
    checkedFunctions += 1;
  }
}

// 4. 具体写法：德语不再出现 "1 Einträge"，俄语三种形式，法语 0 用单数，中日韩照常。
const expect = (locale: string, path: string, n: number, text: string) =>
  assert.equal(rendered.get(`${locale} ${path} ${n}`), text, `${locale} ${path}(${n})`);
for (const path of ["app.managementPage.itemCount", "app.deviceTopology.itemCount", "app.smartReport.itemCount",
  "app.resourceBreakdownView.itemCount"]) {
  expect("de-DE", path, 1, "1 Eintrag");
  expect("de-DE", path, 2, "2 Einträge");
  expect("de-DE", path, 21, "21 Einträge");
  expect("en-US", path, 1, "1 item");
  expect("en-US", path, 0, "0 items");
  expect("fr-FR", path, 0, "0 élément");
  expect("fr-FR", path, 2, "2 éléments");
  expect("es-ES", path, 1, "1 elemento");
  expect("es-ES", path, 0, "0 elementos");
  expect("ru-RU", path, 1, "1 элемент");
  expect("ru-RU", path, 2, "2 элемента");
  expect("ru-RU", path, 5, "5 элементов");
  expect("ru-RU", path, 11, "11 элементов");
  expect("ru-RU", path, 21, "21 элемент");
}
for (const [n, text] of [[1, "1 ядро"], [2, "2 ядра"], [5, "5 ядер"], [21, "21 ядро"], [22, "22 ядра"], [111, "111 ядер"]] as const) {
  expect("ru-RU", "app.cpuTopology.coreCount", n, text);
}
expect("de-DE", "app.diskUsage.scanTotals", 1, "«size» · ausgewählte Wurzel · gesamter Scan: 1 Datei · 1 Ordner");
expect("ru-RU", "app.diskUsage.scanTotals", 2, "«size» · выбранный корень · весь скан: 2 файла · 2 папки");
expect("en-US", "app.diskUsage.fileCount", 1, "1 file");
expect("de-DE", "app.diskUsage.fileCount", 1, "Dateien: 1");
expect("fr-FR", "app.softwareActions.endTaskMessage", 1, "Cela mettra fin à 1 processus correspondant.");
expect("es-ES", "app.softwareActions.restoredRecords", 1, "Se restauró 1 registro de migración.");
expect("en-US", "app.managementFeedback.processTerminateSummary", 1, "Terminated 1 / 1 process.");
expect("de-DE", "app.gpuScheduling.summary", 1, "1 GPU · GPU «gpuPercent» % / VRAM «vramPercent» % · «telemetry»");
// GPU 数量读不到时传 null，照旧显示 "--"，不经过单复数。
assert.equal((await loadAppCopy("en-US")).gpuScheduling.summary(null, 80, 70, "ok"), "-- GPUs · GPU 80% / VRAM 70% · ok");
assert.equal((await loadAppCopy("zh-CN")).gpuScheduling.summary(null, 80, 70, "ok"), "-- 张 GPU · GPU 80% / 显存 70% · ok");
expect("zh-CN", "app.diskUsage.fileCount", 1, "1 个文件");
expect("ja-JP", "app.diskUsage.fileCount", 1, "ファイル 1 件");
expect("ko-KR", "app.diskUsage.fileCount", 1, "파일 1개");

console.log(`plural count contract: ${checkedFunctions} count functions across ${locales.length} languages`);
