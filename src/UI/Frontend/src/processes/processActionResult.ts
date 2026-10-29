import { ApiRequestError } from "../api/httpTransport.ts";
import type { SystemProcessIdentity, SystemProcessOperationResult } from "../types";
import { systemProcessOperationDecoder } from "../data/systemProcessOperationDecoder.ts";

/**
 * 结束或转储进程的结果：动作与请求一致，逐项对应请求的目标（同一进程号、同一启动键），
 * 否则当作无效响应，不把别的进程的结果显示成这次操作的结果。
 */
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
