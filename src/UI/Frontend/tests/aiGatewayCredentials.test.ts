import assert from "node:assert/strict";
import { aiGatewayCredentialsDecoder } from "../src/data/aiGatewayCredentials.ts";
import { createFrontendSources } from "../src/frontendRuntime/source/FrontendSources.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { BackendSubscriptionChannel } from "../src/frontendRuntime/push/BackendSubscriptionChannel.ts";

const row = {
  id: "fixture-key", displayName: "Fixture key", compatibilityProfile: "openai",
  createdAt: "2026-10-26T00:00:00Z", baseUrl: "http://127.0.0.1/fixture",
  authenticationHeader: "Authorization"
};
assert.deepEqual(aiGatewayCredentialsDecoder.decode([row]), [row]);
assert.deepEqual(aiGatewayCredentialsDecoder.decode([]), []);
for (const invalid of [null, {}, [{ ...row, id: null }], [{ ...row, createdAt: "invalid" }], [{ ...row, compatibilityProfile: "unknown" }], [{ ...row, authenticationHeader: undefined }]]) {
  assert.throws(() => aiGatewayCredentialsDecoder.decode(invalid));
}

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const channel = new BackendSubscriptionChannel({ backendSession: session });
const registry = new SourceRegistry(session);
let calls = 0;
let signal: AbortSignal | undefined;
let result: unknown = [row];
let fail = false;
const client = new RequestClient(session, { transport: async (url, options) => {
  assert.equal(url, "/api/ai-gateway/credentials");
  assert.equal(options.method, "GET");
  signal = options.signal;
  calls++;
  if (fail) throw new Error("fixture read failure");
  return result;
} });
try {
  const source = createFrontendSources(client, registry, channel).aiGatewayCredentials;
  const first = source.acquire({ active: false });
  const second = source.acquire({ active: false });
  assert.equal(calls, 0, "disabled consumers do not read");
  first.setDemand({ active: true, refreshIntervalMs: null });
  second.setDemand({ active: true, refreshIntervalMs: null });
  const [left, right] = await Promise.all([first.refresh(), second.refresh()]);
  assert.equal(calls, 1, "two consumers and refresh share one request");
  assert.equal(left, right);
  assert.deepEqual(left.data, [row]);
  first.release();
  assert.equal(second.snapshot.data?.length, 1);
  result = [];
  assert.deepEqual((await second.refresh()).data, [], "post-delete refresh accepts a real empty list");
  fail = true;
  const failed = await second.refresh();
  assert.equal(failed.status, "error");
  assert.equal(failed.data, null, "read failure is not hidden by an older key list");
  assert.ok(failed.error);
  fail = false;
  result = [row];
  assert.equal((await second.refresh()).status, "ready", "explicit refresh recovers after failure");
  second.release();
  assert.equal(signal?.aborted, false, "settled reads need no cancellation");
} finally {
  channel.dispose(); registry.dispose(); session.dispose();
}
console.log("AI gateway credentials: strict decoding, shared demand, empty/error and refresh recovery passed.");
