import assert from "node:assert/strict";
import { flattenCopy, inspectCopy, inspectParameters, inspectText, inspectTraditional } from "./localizationCoverage.ts";

const english = {
  title: "Hardware settings",
  help: "The driver does not expose a writable range.",
  protocol: "NVAPI",
  productName: "Resource Manager",
  count: (n: number) => `There are ${n} files in the folder.`
};
const japanese = { ...english, title: "ハードウェア設定", help: "ドライバーは書き込み可能な範囲を提供していません。" };
assert.deepEqual(inspectCopy("ja-JP", "app", japanese, english), []);
assert.deepEqual(inspectCopy("ja-JP", "app", english, english).map(issue => issue.kind), ["untranslated-english"]);
assert.deepEqual(inspectCopy("en-US", "app", english, english), []);
assert.equal(inspectCopy("fr-FR", "app", { ...english, title: undefined }, english).some(issue => issue.kind === "type"), true);
assert.equal(inspectCopy("fr-FR", "app", { ...english, title: " " }, english).some(issue => issue.kind === "empty"), true);
const { title: _title, ...missingTitle } = english;
assert.equal(inspectCopy("fr-FR", "app", missingTitle, english).some(issue => issue.kind === "missing"), true);
assert.equal(inspectText("ko-KR", "consumer.label", "风扇", "Fan")[0]?.kind, "unexpected-han");
assert.deepEqual(inspectText("es-ES", "app.page.control", "Control", "Control"), []);
assert.deepEqual(inspectText("ja-JP", "settings.credits.name", "Intel Performance Counter Monitor for Windows", "Intel Performance Counter Monitor for Windows"), []);
assert.equal(inspectText("ja-JP", "app.count(3)", english.count(3), english.count(3))[0]?.kind, "untranslated-english");
assert.equal(inspectText("ja-JP", "app.needsComponent", "Needs NVAPI", "Needs NVAPI", true)[0]?.kind, "untranslated-english");
assert.deepEqual(inspectParameters("ja-JP", "app.count(3)", "フォルダーには 3 個のファイルがあります。", ["3"]), []);
assert.equal(inspectParameters("ja-JP", "app.count(3)", "ファイルがあります。", ["3"])[0]?.kind, "parameter");
assert.equal(inspectTraditional("app.page.settings", "设置")[0]?.kind, "simplified-chinese");
assert.deepEqual(inspectTraditional("app.page.settings", "設定"), []);

const optionalBase = { performance: { adaptiveBooleanModeOptions: [{ id: "enabled", label: "Enabled", description: "Always enabled" }] } };
const optionalCopy = { performance: { adaptiveBooleanModeOptions: [{ id: "enabled", label: "开启" }] } };
assert.deepEqual(inspectCopy("zh-CN", "settings", optionalCopy, optionalBase, true), []);
assert.equal(inspectCopy("zh-CN", "settings", optionalCopy, optionalBase, false)[0]?.kind, "missing");
const cycle: Record<string, unknown> = {};
cycle.self = cycle;
assert.throws(() => flattenCopy(cycle), /contains a cycle/);
const shared = { title: "NVAPI" };
assert.deepEqual([...flattenCopy({ left: shared, right: shared })].sort(), [["left.title", "NVAPI"], ["right.title", "NVAPI"]]);
console.log("Localization validator detects missing/type/empty/prose/function/parameter/script defects and preserves neutral terms.");
