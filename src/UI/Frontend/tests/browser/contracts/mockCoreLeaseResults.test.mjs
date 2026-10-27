import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { decodeRuntimePolicySaveResult } from "../../../src/api/runtimePolicySaveResultDecoder.ts";
import { cpuTopologyDecoder, cpuResidencyDecoder, cpuExclusiveBindingsDecoder } from "../../../src/data/cpu/cpuSourceDecoders.ts";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
const paths = [
  ["/api/cpu/topology", cpuTopologyDecoder, "cpu-topology-not-published"],
  ["/api/cpu/topology/exclusive-bindings", cpuExclusiveBindingsDecoder, "cpu-topology-not-published"],
  ["/api/cpu/residency", cpuResidencyDecoder, "cpu-residency-window-warming"]
];
test("CPU GET responses preserve explicit unavailable reasons and recover to unchanged snapshots", async () => {
  await server.scenario({ cpuTopologyEmpty: true, cpuResidencyUnavailableReason: "cpu-residency-window-warming" });
  for (const [path, decoder, reasonCode] of paths) {
    const response = await server.json(path);
    assert.equal(response.status, 200);
    assert.deepEqual(response.value, { state: "unavailable", reasonCode });
    assert.deepEqual(decoder.decode(response.value), response.value);
  }
  await server.scenario({ cpuTopologyEmpty: false, cpuResidencyUnavailableReason: null });
  for (const [path, decoder] of paths) assert.ok("capturedAt" in decoder.decode((await server.json(path)).value));
});
test("policy and performance writes use publication results; application settings retain their own disposition", async () => {
  const writes = [
    ["/api/gpu-placement/software/frontend-harness.software/policy", "PUT", { softwareId: "frontend-harness.software" }],
    ["/api/gpu-placement/process-policy", "PUT", { processKey: "fixture" }],
    ["/api/cpu/topology/performance-overrides", "PUT", { cpuName: "Fixture CPU", scores: [{ coreIndex: 0, performanceScore: 120 }] }],
    ["/api/cpu/topology/performance-overrides?cpuName=Fixture", "DELETE"],
    ["/api/gpu/performance-overrides", "PUT", { scores: [{ gpuId: "gpu:0", performanceScore: 120 }] }],
    ["/api/gpu/performance-overrides", "DELETE"]
  ];
  for (const [disposition, status] of [["committedAndPublished", 200], ["committedWithDeliveryFailures", 202], ["savedNotApplied", 409]]) {
    await server.scenario({ runtimePolicyDisposition: disposition });
    for (const [path, method, body] of writes) {
      const response = await server.json(path, { method, headers: { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
      assert.equal(response.status, status, path);
      assert.equal(decodeRuntimePolicySaveResult(response.value).runtimeApplicationDisposition, disposition, path);
    }
  }
  assert.equal((await server.json("/api/settings/app")).value.runtimeApplicationDisposition, "committedAndApplied");
  await assert.rejects(() => server.scenario({ runtimePolicyDisposition: "committedAndApplied" }));
});
