import type { OperationSnapshot } from "../../types.ts";
import type { OperationRegistrySnapshot } from "./OperationRegistry.ts";
import { isTerminalOperation } from "./operationMerge.ts";

export interface OperationSelector {
  readonly kind?: string;
  readonly domainKey?: string | null;
  readonly active?: boolean;
}

export function selectOperations(
  snapshot: OperationRegistrySnapshot,
  selector: OperationSelector = {}
): readonly OperationSnapshot[] {
  return snapshot.operations.filter((operation) => {
    if (selector.kind !== undefined && operation.kind !== selector.kind) {
      return false;
    }
    if (selector.domainKey !== undefined
      && operation.domainKey !== selector.domainKey) {
      return false;
    }
    if (selector.active !== undefined
      && selector.active === isTerminalOperation(operation)) {
      return false;
    }
    return true;
  });
}
