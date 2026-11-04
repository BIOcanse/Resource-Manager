import type { SystemProcessOperationResult, SystemProcessOutcome } from "../types.ts";
import { defineResponseDecoder, requireArray, requireNonEmptyString, requireNonNegativeSafeInteger,
  requireNullable, requireOneOf, requireRecord, requireString, ResponseDecodeError } from "../frontendRuntime/request/ResponseDecoder.ts";
import { isCanonicalUInt64Decimal } from "./operations/uint64Decimal.ts";

const outcomes = ["Terminated", "DumpCreated", "BackendProtected", "CriticalProcessProtected", "IdentityChanged",
  "ProcessExited", "AccessDenied", "NativeFailure", "FileFailure", "RequestRejected"] as const satisfies readonly SystemProcessOutcome[];

export const systemProcessOperationDecoder = defineResponseDecoder<SystemProcessOperationResult>(
  "system-process-operation.v2", value => {
    const record = requireRecord(value);
    const action = requireOneOf(record.action, "$.action", ["Terminate", "Dump"] as const);
    const ids = new Set<number>();
    const items = requireArray(record.items, "$.items").map((value, index) => {
      const path = `$.items[${index}]`;
      const item = requireRecord(value, path);
      const processId = requireNonNegativeSafeInteger(item.processId, `${path}.processId`);
      if (processId === 0 || ids.has(processId)) throw new ResponseDecodeError(`${path}.processId`, "unique positive process id");
      ids.add(processId);
      const processStartKey = requireNonEmptyString(item.processStartKey, `${path}.processStartKey`);
      if (!isCanonicalUInt64Decimal(processStartKey) || processStartKey === "0")
        throw new ResponseDecodeError(`${path}.processStartKey`, "positive uint64 decimal string");
      const state = requireOneOf(item.state, `${path}.state`, ["Succeeded", "Skipped", "Failed"] as const);
      const outcome = requireOneOf(item.outcome, `${path}.outcome`, outcomes);
      const expectedState = outcome === "Terminated" || outcome === "DumpCreated" ? "Succeeded"
        : ["BackendProtected", "CriticalProcessProtected", "IdentityChanged", "ProcessExited"].includes(outcome) ? "Skipped" : "Failed";
      if (state !== expectedState || (outcome === "Terminated" && action !== "Terminate")
        || (outcome === "DumpCreated" && action !== "Dump"))
        throw new ResponseDecodeError(`${path}.outcome`, "outcome matching state and action");
      return {
        processId, processStartKey, state, outcome,
        processName: requireNullable(item.processName, `${path}.processName`, requireString),
        path: requireNullable(item.path, `${path}.path`, requireString),
        nativeErrorCode: requireNullable(item.nativeErrorCode, `${path}.nativeErrorCode`, requireNonNegativeSafeInteger)
      };
    });
    if (items.length === 0 || items.length > 64) throw new ResponseDecodeError("$.items", "1 through 64 process outcomes");
    return { action, items, directoryPath: requireNullable(record.directoryPath, "$.directoryPath", requireString) };
  });
