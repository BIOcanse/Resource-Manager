import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { accessRejectionCode, accessRejectionCodes, accessRejectionMessage } from "../src/presentation/accessRejection.ts";
import { userFacingErrorMessage } from "../src/presentation/userFacingText.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { uiText } from "../src/text.ts";

// 访问控制拒绝的码与后端 TrustedAccessHttpPolicy 每个 Reject(...) 里写的码逐个相等：后端加一个码而前端没有原因可写时，这里失败。
const policy = readFileSync(new URL("../../../Core/Infrastructure/Security/TrustedAccessHttpPolicy.cs", import.meta.url), "utf8");
const backendCodes = new Set<string>();
for (const call of policy.matchAll(/TrustedAccessAuthorizationDecision\.Reject\(([\s\S]*?)\);/g)) {
  for (const literal of call[1]!.matchAll(/"([a-z]+(?:-[a-z]+)+)"/g)) backendCodes.add(literal[1]!);
}
assert.deepEqual([...accessRejectionCodes].sort(), [...backendCodes].sort(), "access rejection codes match TrustedAccessHttpPolicy");

const rejected = (status: number, error: string) => new RequestProblem("BACKEND_TEXT", {
  kind: "http", status, payload: { error }, retryable: false,
  attempt: {
    id: "request-1", sequence: 1, key: "component.install:hardware-bridge", decoderId: "operation.snapshot", method: "POST",
    backendEpoch: null, startedAt: 0, finishedAt: 1, durationMs: 1
  }
});

// 组件页更新硬件写入辅助进程时后端回 403 access-scope-required：失败提示写出原因，不是调用方的“操作失败”。
const scope = rejected(403, "access-scope-required");
assert.equal(accessRejectionCode(scope), "access-scope-required");
assert.equal(userFacingErrorMessage(scope, uiText.stores.actionFailed), uiText.accessRejection["access-scope-required"]);
for (const code of accessRejectionCodes) {
  const status = code === "access-service-unavailable" ? 503 : code.endsWith("-invalid") || code.startsWith("keyless") || code.startsWith("local-") ? 401 : 403;
  assert.equal(accessRejectionMessage(rejected(status, code)), uiText.accessRejection[code], code);
  assert.ok(uiText.accessRejection[code].trim().length > 0, `${code} has text`);
}

// 其他失败不当成访问控制拒绝：未知码、别的状态码、不是 HTTP 的失败，都照原来的提示。
assert.equal(accessRejectionCode(rejected(403, "ProcessActionRequiresElevation")), null);
assert.equal(accessRejectionCode(rejected(500, "access-scope-required")), null);
assert.equal(accessRejectionCode(new Error("access-scope-required")), null);
assert.equal(accessRejectionCode(null), null);
assert.equal(userFacingErrorMessage(rejected(409, "conflict"), uiText.stores.actionFailed),
  userFacingErrorMessage(new Error("conflict"), uiText.stores.actionFailed), "other HTTP failures keep the caller's fallback");

console.log(`PASS access rejection: ${accessRejectionCodes.length} backend codes`);
