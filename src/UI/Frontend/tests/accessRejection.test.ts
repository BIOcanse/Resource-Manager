import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { accessRejection, accessRejectionCode, accessRejectionCodes, accessRejectionMessage } from "../src/presentation/accessRejection.ts";
import { userFacingErrorMessage } from "../src/presentation/userFacingText.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { currentSettingsText, uiText } from "../src/text.ts";

// 访问控制拒绝的码与后端 TrustedAccessHttpPolicy 每个 Reject(...) 里写的码逐个相等：后端加一个码而前端没有原因可写时，这里失败。
const policy = readFileSync(new URL("../../../Core/Infrastructure/Security/TrustedAccessHttpPolicy.cs", import.meta.url), "utf8");
const backendCodes = new Set<string>();
// 只取 Reject( 的参数（到它自己的右括号，允许一层嵌套括号），不取后面 with { ... } 里的内容。
for (const call of policy.matchAll(/TrustedAccessAuthorizationDecision\.Reject\(([^()]*(?:\([^()]*\)[^()]*)*)\)/g)) {
  for (const literal of call[1]!.matchAll(/"([a-z]+(?:-[a-z]+)+)"/g)) backendCodes.add(literal[1]!);
}
assert.deepEqual([...accessRejectionCodes].sort(), [...backendCodes].sort(), "access rejection codes match TrustedAccessHttpPolicy");

const rejected = (status: number, error: string, extra: Record<string, unknown> = {}) => new RequestProblem("BACKEND_TEXT", {
  kind: "http", status, payload: { error, ...extra }, retryable: false,
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

// 后端 e271bfb5：access-scope-required 带 missingScopes（范围 ID，按路由要求的顺序）。本版本都认识时写出缺少的范围名，
// 有不认识的就只写缺少范围（不显示原始 ID，也不只列一部分）；别的码即使带了这个字段也不读。
const scopeLabels = currentSettingsText().systemIntegration.trustedAccess.scopeLabels;
assert.equal(userFacingErrorMessage(rejected(403, "access-scope-required", { missingScopes: ["machine.write"] }), uiText.stores.actionFailed),
  uiText.accessRejection.missingScopes([scopeLabels["machine.write"]]));
assert.ok(uiText.accessRejection.missingScopes([scopeLabels["machine.write"]]).includes(scopeLabels["machine.write"]));
assert.equal(accessRejectionMessage(rejected(403, "access-scope-required", { missingScopes: ["machine.write", "injection.configure"] })),
  uiText.accessRejection.missingScopes([scopeLabels["machine.write"], scopeLabels["injection.configure"]]));
assert.equal(accessRejectionMessage(rejected(403, "access-scope-required", { missingScopes: ["machine.write", "future.scope"] })),
  uiText.accessRejection["access-scope-required"]);
assert.equal(accessRejectionMessage(rejected(403, "access-scope-required", { missingScopes: [] })), uiText.accessRejection["access-scope-required"]);
assert.deepEqual(accessRejection(rejected(403, "access-key-required", { missingScopes: ["machine.write"] }))?.missingScopes, []);

// 其他失败不当成访问控制拒绝：未知码、别的状态码、不是 HTTP 的失败，都照原来的提示。
assert.equal(accessRejectionCode(rejected(403, "ProcessActionRequiresElevation")), null);
assert.equal(accessRejectionCode(rejected(500, "access-scope-required")), null);
assert.equal(accessRejectionCode(new Error("access-scope-required")), null);
assert.equal(accessRejectionCode(null), null);
assert.equal(userFacingErrorMessage(rejected(409, "conflict"), uiText.stores.actionFailed),
  userFacingErrorMessage(new Error("conflict"), uiText.stores.actionFailed), "other HTTP failures keep the caller's fallback");

console.log(`PASS access rejection: ${accessRejectionCodes.length} backend codes`);
