import assert from "node:assert/strict";
import {
  baseScoreWithoutOwnLayer, decodeBaseScoreResolution, decodeProcessBaseScores
} from "../src/data/softwarePolicy/baseScoreResolutionDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

// 软件详情读取的基础分（后端 CompiledBaseScorePlan.Resolve）：五层固定顺序、已知来源、key 为 null 当且仅当 value 为 null，
// source 和 value 是最后一个有值的层。

type Layer = { source: string; value: number | null; key: string | null };
const empty = (source: string): Layer => ({ source, value: null, key: null });
const catalogGroup = () => ({
  value: 90, source: "catalog_group",
  layers: [
    { source: "type_default", value: 35, key: "other" },
    { source: "catalog_group", value: 90, key: "adapted.resource-manager" },
    empty("catalog_process"), empty("user_software"), empty("user_process")
  ] as Layer[]
});

const valid = decodeBaseScoreResolution(catalogGroup(), "$.baseScore");
assert.deepEqual(valid, catalogGroup(), "a valid chain decodes unchanged");

const userOverride = catalogGroup();
userOverride.layers[3] = { source: "user_software", value: 70, key: "example.software" };
userOverride.value = 70; userOverride.source = "user_software";
assert.equal(decodeBaseScoreResolution(userOverride, "$").source, "user_software", "a later layer overrides an earlier one, lower or not");
assert.deepEqual(baseScoreWithoutOwnLayer(decodeBaseScoreResolution(userOverride, "$"), "user_software"),
  { source: "catalog_group", value: 90, key: "adapted.resource-manager" }, "the placeholder ignores the user's own layer");

const processChain = catalogGroup();
processChain.layers[2] = { source: "catalog_process", value: 30, key: "system.background:searchhost.exe" };
processChain.layers[3] = { source: "user_software", value: 50, key: "example.software" };
processChain.layers[4] = { source: "user_process", value: 85.5, key: "example.exe" };
processChain.value = 85.5; processChain.source = "user_process";
assert.equal(baseScoreWithoutOwnLayer(decodeBaseScoreResolution(processChain, "$"), "user_process")?.value, 50,
  "a process placeholder inherits the user's software setting");
assert.deepEqual(Object.keys(decodeProcessBaseScores({ "example.exe": processChain }, "$.processBaseScores")), ["example.exe"]);
assert.deepEqual(decodeProcessBaseScores({}, "$.processBaseScores"), {});

function rejects(value: unknown, path: string, message: string) {
  assert.throws(() => decodeBaseScoreResolution(value, "$.baseScore"),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === path, message);
}

// 顺序不对：目录组排在类型默认前面。
const wrongOrder = catalogGroup();
[wrongOrder.layers[0], wrongOrder.layers[1]] = [wrongOrder.layers[1], wrongOrder.layers[0]];
rejects(wrongOrder, "$.baseScore.layers[0].source", "wrong order");
// 少一层、多一层。
rejects({ ...catalogGroup(), layers: catalogGroup().layers.slice(0, 4) }, "$.baseScore.layers", "four layers");
rejects({ ...catalogGroup(), layers: [...catalogGroup().layers, empty("user_process")] }, "$.baseScore.layers", "six layers");
// 不认识的来源。
const unknownSource = catalogGroup();
unknownSource.layers[2] = { source: "catalog_service", value: null, key: null };
rejects(unknownSource, "$.baseScore.layers[2].source", "unknown layer source");
rejects({ ...catalogGroup(), source: "catalog_service" }, "$.baseScore.source", "unknown effective source");
// 有键没值、有值没键。
const keyWithoutValue = catalogGroup();
keyWithoutValue.layers[3] = { source: "user_software", value: null, key: "example.software" };
rejects(keyWithoutValue, "$.baseScore.layers[3].key", "key without value");
const valueWithoutKey = catalogGroup();
valueWithoutKey.layers[1] = { source: "catalog_group", value: 90, key: null };
rejects(valueWithoutKey, "$.baseScore.layers[1].key", "value without key");
// 读取没带软件类型时，类型默认层有值但没有类型键：照常解码。
const typeWithoutKind = catalogGroup();
typeWithoutKind.layers[0] = { source: "type_default", value: 35, key: null };
assert.equal(decodeBaseScoreResolution(typeWithoutKind, "$").layers[0].key, null);
// source 不是最后一个有值的层。
rejects({ ...catalogGroup(), source: "type_default", value: 35 }, "$.baseScore.source", "source is not the last layer with a value");
// value 和那一层的值不一样。
rejects({ ...catalogGroup(), value: 35 }, "$.baseScore.value", "value mismatch");
// 缺字段、类型不对、非有限数。
const { source: _source, ...withoutSource } = catalogGroup();
rejects(withoutSource, "$.baseScore.source", "missing source");
const { value: _value, ...withoutValue } = catalogGroup();
rejects(withoutValue, "$.baseScore.value", "missing value");
const { layers: _layers, ...withoutLayers } = catalogGroup();
rejects(withoutLayers, "$.baseScore.layers", "missing layers");
const missingKey = catalogGroup();
missingKey.layers[1] = { source: "catalog_group", value: 90 } as Layer;
rejects(missingKey, "$.baseScore.layers[1].key", "missing layer key");
const missingLayerValue = catalogGroup();
missingLayerValue.layers[4] = { source: "user_process", key: null } as Layer;
rejects(missingLayerValue, "$.baseScore.layers[4].value", "missing layer value");
const infinite = catalogGroup();
infinite.layers[1] = { source: "catalog_group", value: Number.POSITIVE_INFINITY, key: "group" };
rejects(infinite, "$.baseScore.layers[1].value", "non-finite layer value");
rejects({ value: 0, source: "type_default", layers: ["type_default", "catalog_group", "catalog_process", "user_software", "user_process"].map(empty) },
  "$.baseScore.layers", "no layer with a value");
rejects(undefined, "$.baseScore", "missing baseScore");
rejects(null, "$.baseScore", "null baseScore");
assert.throws(() => decodeProcessBaseScores(undefined, "$.processBaseScores"),
  (error: unknown) => error instanceof ResponseDecodeError && error.path === "$.processBaseScores", "missing processBaseScores");
assert.throws(() => decodeProcessBaseScores({ "a.exe": { ...catalogGroup(), value: 1 } }, "$.processBaseScores"),
  (error: unknown) => error instanceof ResponseDecodeError && error.path === "$.processBaseScores[\"a.exe\"].value", "a bad process entry");

console.log("base score resolution decoder: ok");
