import assert from "node:assert/strict";
import decisions from "./fixtures/trustedAccessDecisions.json" with { type: "json" };
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import {
  accessKeyCreatedDecoder,
  accessKeySnapshotDecoder,
  accessRequestDecisionDecoder,
  accessRequestPendingSnapshotDecoder,
  defaultAccessKeyStateDecoder,
  decodeAccessRequestDecision
} from "../src/features/trustedAccess/trustedAccessDecoder.ts";
import {
  approveAccessRequest,
  buildAccessRequestPendingSubscriptionUrl,
  createAccessKey,
  denyAccessRequest,
  getAccessKeySnapshot,
  revokeAccessKey,
  rotateAccessKey,
  setDefaultAccessKey
} from "../src/features/trustedAccess/trustedAccessApi.ts";
import type { AccessKeySummary } from "../src/features/trustedAccess/trustedAccessTypes.ts";

const defaultScopes = [
  "read", "metrics.read", "processes.read", "session", "adapters.register.self",
  "desktop.publish", "gpu.resolve", "gpu.report", "keys.manage"
];
const independentKey: AccessKeySummary = {
  id: "script-1",
  name: "Local report script",
  purpose: "control",
  scopes: ["read", "metrics.read"],
  createdAt: "2026-10-24T12:00:00Z",
  isDefault: false
};
const defaultKey: AccessKeySummary = {
  id: "default-1",
  name: "Native default",
  purpose: "control",
  scopes: defaultScopes,
  createdAt: "2026-10-24T11:00:00Z",
  isDefault: true
};

assert.equal(buildAccessRequestPendingSubscriptionUrl(), "/api/access-requests/subscribe");
assert.deepEqual(accessKeySnapshotDecoder.decode({
  keys: [defaultKey, independentKey],
  defaultEnabled: true,
  defaultKeyId: defaultKey.id,
  availableScopes: [...defaultScopes, "machine.write", "injection.configure"]
}).keys.map((key) => key.name), ["Native default", "Local report script"]);
assert.throws(() => accessKeySnapshotDecoder.decode({
  keys: [{ ...defaultKey, apiKey: "default-secret" }],
  defaultEnabled: true,
  defaultKeyId: defaultKey.id,
  availableScopes: defaultScopes
}), /absent from metadata responses/);
assert.throws(() => accessKeySnapshotDecoder.decode({
  keys: [{ ...defaultKey, purpose: "openai" }],
  defaultEnabled: true,
  defaultKeyId: defaultKey.id,
  availableScopes: defaultScopes
}), /control-purpose keys only/);
assert.throws(() => accessKeySnapshotDecoder.decode({
  keys: [], defaultEnabled: true, defaultKeyId: "missing", availableScopes: defaultScopes
}), /consistent enabled default key metadata/);
assert.throws(() => defaultAccessKeyStateDecoder.decode({
  defaultEnabled: true, defaultKeyId: defaultKey.id, apiKey: "default-secret"
}), /absent from metadata responses/);
assert.throws(() => accessKeyCreatedDecoder.decode({
  key: defaultKey,
  apiKey: "unexpected-default-secret"
}), /an independent control key/);

const pending = accessRequestPendingSnapshotDecoder.decode([{
  requestId: "request-1",
  name: "Telemetry helper",
  requestedScopes: ["read", "metrics.read"],
  observedExecutablePath: "C:\\Tools\\telemetry.exe",
  observedProcessId: 4321,
  submittedAt: "2026-10-24T12:01:00Z",
  expiresAt: "2026-10-24T12:06:00Z"
}]);
assert.equal(pending.requests[0].observedExecutablePath, "C:\\Tools\\telemetry.exe");
assert.equal(pending.requests[0].observedProcessId, 4321);
assert.throws(() => accessRequestPendingSnapshotDecoder.decode([{
  requestId: "request-1",
  name: "Telemetry helper",
  requestedScopes: ["read"],
  observedExecutablePath: "C:\\Tools\\telemetry.exe",
  observedProcessId: 4321,
  submittedAt: "2026-10-24T12:01:00Z",
  expiresAt: "2026-10-24T12:06:00Z",
  deliverySecret: "must-not-enter-the-pending-stream"
}]), /absent from metadata responses/);
assert.deepEqual(decodeAccessRequestDecision({ status: "expired" }, "request-1"), {
  requestId: "request-1", status: "expired"
});
for (const [status, decision] of Object.entries(decisions)) {
  const result = decodeAccessRequestDecision(decision, "request-1");
  assert.deepEqual(result, {
    requestId: "request-1",
    status,
    ...(decision.key ? { key: decision.key } : {}),
    ...(decision.denialReason ? { denialReason: decision.denialReason } : {})
  }, `production-serialized ${status} decision is normalized at the boundary`);
}
assert.throws(() => decodeAccessRequestDecision({
  ...decisions.approved, denialReason: "unsupported"
}, "request-1"), (error) => error instanceof ResponseDecodeError && error.path === "$.denialReason");
assert.throws(() => decodeAccessRequestDecision({
  ...decisions.expired, key: independentKey
}, "request-1"), (error) => error instanceof ResponseDecodeError && error.path === "$.key");
assert.throws(() => decodeAccessRequestDecision({
  ...decisions.denied, denialReason: "unknown"
}, "request-1"), (error) => error instanceof ResponseDecodeError && error.path === "$.denialReason");
assert.throws(() => decodeAccessRequestDecision({
  status: "approved", key: independentKey, apiKey: "must-only-go-to-requestor"
}, "request-1"), /absent from metadata responses/);
assert.throws(() => decodeAccessRequestDecision({ requestId: "request-1", status: "approved" }, "other-request"), /requested access-request ID/);
assert.throws(() => accessRequestDecisionDecoder.decode({
  requestId: "request-1", status: "denied", key: independentKey
}), (error) => error instanceof ResponseDecodeError && error.path === "$.key");

const originalFetch = globalThis.fetch;
const calls: Array<{ path: string; method: string; body: unknown }> = [];
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), "http://frontend.test");
  const method = init?.method ?? "GET";
  const body = typeof init?.body === "string" ? JSON.parse(init.body) : null;
  calls.push({ path: url.pathname, method, body });

  if (url.pathname === "/api/access-keys" && method === "GET") {
    return json({
      keys: [defaultKey, independentKey],
      defaultEnabled: true,
      defaultKeyId: defaultKey.id,
      availableScopes: defaultScopes
    });
  }
  if (url.pathname === "/api/access-keys" && method === "POST") {
    return json({ key: { ...independentKey, id: "created-1", name: body.name, scopes: body.scopes }, apiKey: "independent-one-time-secret" });
  }
  if (url.pathname === "/api/access-keys/script-1/rotate" && method === "POST") {
    return json({ key: { ...independentKey, id: "script-2" }, apiKey: "rotated-one-time-secret" });
  }
  if (url.pathname === "/api/access-keys/default" && method === "POST") {
    return json({ defaultEnabled: body.enabled, defaultKeyId: body.enabled ? "default-2" : null });
  }
  if (url.pathname === "/api/access-requests/expired-1/approve" && method === "POST") {
    return json(decisions.expired, 409);
  }
  if (url.pathname === "/api/access-requests/approved-1/approve" && method === "POST") {
    return json(decisions.approved);
  }
  if (url.pathname === "/api/access-requests/denied-1/deny" && method === "POST") {
    return json(decisions.denied);
  }
  if (url.pathname === "/api/access-keys/created-1" && method === "DELETE") {
    return new Response(null, { status: 204 });
  }
  throw new Error(`Unexpected trusted-access request: ${method} ${url.pathname}`);
}) as typeof fetch;

try {
  const loaded = await getAccessKeySnapshot("key list failed");
  assert.equal(loaded.defaultEnabled, true);

  const created = await createAccessKey("Narrow reader", ["read"], "create failed");
  assert.equal(created.apiKey, "independent-one-time-secret");
  assert.deepEqual(calls.find((call) => call.method === "POST" && call.path === "/api/access-keys")?.body, {
    name: "Narrow reader", purpose: "control", scopes: ["read"]
  });

  const rotated = await rotateAccessKey("script-1", "rotate failed");
  assert.equal(rotated.apiKey, "rotated-one-time-secret");

  const defaultState = await setDefaultAccessKey(true, true, "default key update failed");
  assert.deepEqual(defaultState, { defaultEnabled: true, defaultKeyId: "default-2" });
  assert.deepEqual(calls.find((call) => call.path === "/api/access-keys/default")?.body,
    { enabled: true, rotate: true });

  const approved = await approveAccessRequest("approved-1", ["read"], "approval failed");
  assert.deepEqual(approved, { requestId: "approved-1", status: "approved", key: decisions.approved.key });
  const expired = await approveAccessRequest("expired-1", ["read"], "approval failed");
  assert.deepEqual(expired, { requestId: "expired-1", status: "expired" });
  const denied = await denyAccessRequest("denied-1", "unsupported", "denial failed");
  assert.deepEqual(denied, {
    requestId: "denied-1", status: "denied", denialReason: "unsupported"
  });
  await revokeAccessKey("created-1", "revoke failed");
  assert.deepEqual(calls.map((call) => call.path), [
    "/api/access-keys",
    "/api/access-keys",
    "/api/access-keys/script-1/rotate",
    "/api/access-keys/default",
    "/api/access-requests/approved-1/approve",
    "/api/access-requests/expired-1/approve",
    "/api/access-requests/denied-1/deny",
    "/api/access-keys/created-1"
  ]);
  assert.equal(calls.some((call) => call.path.includes("acquire-default")), false,
    "the frontend never requests the native-only default secret");
} finally {
  globalThis.fetch = originalFetch;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

console.log("Trusted-access decoders, endpoint payloads, and typed expiry/denial results passed.");
