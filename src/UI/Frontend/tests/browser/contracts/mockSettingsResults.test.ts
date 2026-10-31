import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.ts";
import type { MockAppSettingsResult } from "./mockServerFixture.ts";
let server: Awaited<ReturnType<typeof startMockServer>>;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });
const request = (method: string, body: unknown): RequestInit => ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

for (const [disposition, expectedStatus] of [["savedNotApplied", 202], ["committedWithDeliveryFailures", 202], ["committedWithCapabilityConstraints", 200], ["committedAndApplied", 200]]) {
  test(`M05 ${disposition} preserves saved values, HTTP status and runtime result`, async () => {
    await server.scenario({ settingsRuntimeDisposition: disposition });
    const initial = (await server.json<MockAppSettingsResult>("/api/settings/app")).value;
    const changed = !initial.settings.debug.debugModeEnabled;
    const { status, value } = await server.json<MockAppSettingsResult>("/api/settings/app", request("PATCH", { expectedRevision: initial.revision, changes: { debug: { debugModeEnabled: changed } } }));
    assert.equal(status, expectedStatus);
    assert.equal(value.runtimeApplicationDisposition, disposition);
    assert.notEqual(value.revision, initial.revision);
    assert.equal(value.settings.debug.debugModeEnabled, changed);
    assert.equal(value.runtimeDeliveryFailureCount, disposition === "committedWithDeliveryFailures" ? 1 : 0);
    assert.equal(value.runtimeFailureCode ?? null, disposition === "savedNotApplied" ? "runtime-plan-publication-failed" : null);
    assert.equal(value.runtimeCapabilityConstrainedPaths.length > 0, disposition === "committedWithCapabilityConstraints");
    const readback = (await server.json<MockAppSettingsResult>("/api/settings/app")).value;
    assert.equal(readback.revision, value.revision);
    assert.equal(readback.runtimeApplicationDisposition, disposition);
    assert.equal(readback.settings.debug.debugModeEnabled, changed);
  });
}

test("M05 PUT returns accepted delivery failures and reapply preserves the revision", async () => {
  await server.scenario({ settingsRuntimeDisposition: "committedWithDeliveryFailures" });
  const initial = (await server.json<MockAppSettingsResult>("/api/settings/app")).value;
  const settings = structuredClone(initial.settings);
  settings.debug.debugModeEnabled = !settings.debug.debugModeEnabled;
  const saved = await server.json<MockAppSettingsResult>("/api/settings/app", request("PUT", settings));
  assert.equal(saved.status, 202);
  assert.equal(saved.value.runtimeApplicationDisposition, "committedWithDeliveryFailures");
  await server.scenario({ settingsRuntimeDisposition: "committedAndApplied" });
  const applied = await server.json<MockAppSettingsResult>("/api/settings/app/reapply", { method: "POST" });
  assert.equal(applied.status, 200);
  assert.equal(applied.value.runtimeApplicationDisposition, "committedAndApplied");
  assert.equal(applied.value.revision, saved.value.revision);
  assert.equal(applied.value.runtimeDeliveryFailureCount, 0);
  assert.deepEqual(applied.value.settings, settings);
});

test("M05 revision conflicts never commit the settings draft", async () => {
  const initial = (await server.json<MockAppSettingsResult>("/api/settings/app")).value;
  const { status, value } = await server.json<MockAppSettingsResult>("/api/settings/app", request("PATCH", { expectedRevision: "stale", changes: { debug: { debugModeEnabled: !initial.settings.debug.debugModeEnabled } } }));
  assert.equal(status, 409);
  assert.equal(value.runtimeApplicationDisposition, "revisionConflict");
  assert.equal(value.revision, initial.revision);
  assert.deepEqual((await server.json<MockAppSettingsResult>("/api/settings/app")).value.settings, initial.settings);
});
