import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import { ApiRequestError } from "../api/httpTransport.ts";
import { uiText } from "../text.ts";
import type { SystemProcessIdentity, SystemProcessOperationResult } from "../types";

export async function withProcessActionElevation(
  action: "terminate" | "dump", targets: readonly SystemProcessIdentity[],
  request: () => Promise<SystemProcessOperationResult>, fallbackError: string
): Promise<SystemProcessOperationResult> {
  try { return await request(); }
  catch (error) {
    if (!(error instanceof ApiRequestError) || error.status !== 403
        || !error.payload || typeof error.payload !== "object"
        || (error.payload as { code?: unknown }).code !== "ProcessActionRequiresElevation") throw error;
    const transport = getHostMessageTransport();
    if (!transport?.canSubscribeMessages) throw error;
    const requestId = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      let unsubscribe: (() => void) | null = null;
      const timer = globalThis.setTimeout(() => {
        unsubscribe?.();
        reject(new ApiRequestError(uiText.misc.transportTimeout, { kind: "timeout", retryable: false }));
      }, 120_000);
      unsubscribe = transport.subscribeMessages((message: unknown) => {
        if (!message || typeof message !== "object") return;
        const response = message as { type?: unknown; requestId?: unknown; canceled?: unknown; timedOut?: unknown; result?: unknown };
        if (response.type !== "shell.processAction.result" || response.requestId !== requestId) return;
        globalThis.clearTimeout(timer);
        unsubscribe?.();
        if (response.canceled === true) {
          reject(new ApiRequestError(uiText.misc.transportCanceled, { kind: "aborted", retryable: false }));
          return;
        }
        if (response.timedOut === true) {
          reject(new ApiRequestError(uiText.misc.transportTimeout, { kind: "timeout", retryable: false }));
          return;
        }
        if (!isExactResult(response.result, targets)) {
          reject(new ApiRequestError(fallbackError, { kind: "invalid-response", retryable: false }));
          return;
        }
        resolve(response.result);
      });
      if (!unsubscribe) {
        globalThis.clearTimeout(timer);
        reject(error);
        return;
      }
      try { transport.postMessage({ type: "shell.processAction", requestId, batch: { action, targets } }); }
      catch (cause) { globalThis.clearTimeout(timer); unsubscribe(); reject(cause); }
    });
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
