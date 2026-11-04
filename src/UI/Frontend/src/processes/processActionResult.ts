import { ApiRequestError } from "../api/httpTransport.ts";
import type { SystemProcessIdentity, SystemProcessOperationResult } from "../types";
import { systemProcessOperationDecoder } from "../data/systemProcessOperationDecoder.ts";

/** Require the requested action and exact PID/start-key target set; reject mismatches so another process result cannot appear as this operation. */
export function exactProcessActionResult(
  action: "terminate" | "dump", targets: readonly SystemProcessIdentity[], value: unknown, fallbackError: string
): SystemProcessOperationResult {
  try {
    const result = systemProcessOperationDecoder.decode(value);
    if (result.action !== (action === "terminate" ? "Terminate" : "Dump") || !isExactResult(result, targets)) throw new Error();
    return result;
  } catch {
    throw new ApiRequestError(fallbackError, { kind: "invalid-response", retryable: false });
  }
}

function isExactResult(value: unknown, targets: readonly SystemProcessIdentity[]): value is SystemProcessOperationResult {
  if (!value || typeof value !== "object") return false;
  const result = value as Partial<SystemProcessOperationResult>;
  if (!Array.isArray(result.items) || result.items.length !== targets.length) return false;
  const expected = new Map(targets.map(target => [target.processId, target.processStartKey]));
  return result.items.every(item => {
    if (!item || expected.get(item.processId) !== item.processStartKey
        || !["Succeeded", "Skipped", "Failed"].includes(item.state)) return false;
    return expected.delete(item.processId);
  }) && expected.size === 0;
}
