import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.ts";
import { decodeBaseScoreResolution, decodeProcessBaseScores } from "../../../src/data/softwarePolicy/baseScoreResolutionDecoder.ts";
import type { GpuPlacementSoftwarePolicy } from "../../../src/types.ts";
type SoftwareSettingsWire = {
  baseScore: ReturnType<typeof decodeBaseScoreResolution>;
  processBaseScores: ReturnType<typeof decodeProcessBaseScores>;
  softwarePolicy: GpuPlacementSoftwarePolicy;
};

// 模拟后端的软件详情读取带后端同形的 baseScore 和 processBaseScores（CompiledBaseScorePlan.Resolve），过前端的严格解码。
let server: Awaited<ReturnType<typeof startMockServer>>;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
const id = "frontend-harness.software";
const path = `/api/gpu-placement/software/${id}`;

test("software settings read carries the five-layer base score chain and follows the saved software override", async () => {
  const read = async () => (await server.json<SoftwareSettingsWire>(`${path}?softwareName=Example&softwareKind=game`)).value;
  const initial = await read();
  const base = decodeBaseScoreResolution(initial.baseScore, "$.baseScore");
  assert.deepEqual([base.value, base.source, base.layers[0].key], [95, "type_default", "game"]);
  assert.deepEqual(decodeProcessBaseScores(initial.processBaseScores, "$.processBaseScores"), {});

  const saved = await server.json(`${path}/policy`, { method: "PUT", headers: { "content-type": "application/json" },
    body: JSON.stringify({ ...initial.softwarePolicy, baseScoreOverride: 70 }) });
  assert.equal(saved.status, 200);
  const overridden = decodeBaseScoreResolution((await read()).baseScore, "$.baseScore");
  assert.deepEqual([overridden.value, overridden.source, overridden.layers[3]], [70, "user_software",
    { source: "user_software", value: 70, key: id }]);
  assert.equal(overridden.layers[0].value, 95, "the earlier layers stay visible");

  await server.json(`${path}/policy`, { method: "PUT", headers: { "content-type": "application/json" },
    body: JSON.stringify({ ...initial.softwarePolicy, baseScoreOverride: null }) });
  assert.equal(decodeBaseScoreResolution((await read()).baseScore, "$.baseScore").source, "type_default");
});

test("without a software kind the type layer has a value and no key, as the backend sends it, and the decoder accepts it", async () => {
  const value = (await server.json<SoftwareSettingsWire>(`${path}?softwareName=Example`)).value;
  assert.deepEqual(value.baseScore.layers[0], { source: "type_default", value: 35, key: null });
  const decoded = decodeBaseScoreResolution(value.baseScore, "$.baseScore");
  assert.equal(decoded.source, "type_default");
  assert.equal(decoded.layers[0].key, null);
});
