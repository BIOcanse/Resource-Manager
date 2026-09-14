import { onCleanup } from "solid-js";
import { useFrontendRuntime } from "../FrontendRuntimeContext.tsx";
import type { TaskScope } from "./TaskScope.ts";

let nextComponentScopeInstance = 0;

export function useTaskScope(logicalKey: string): TaskScope {
  const instance = ++nextComponentScopeInstance;
  const scope = useFrontendRuntime().taskRegistry.openScope(
    `${normalizeLogicalKey(logicalKey)}@${instance}`);
  onCleanup(() => scope.close());
  return scope;
}

function normalizeLogicalKey(value: string): string {
  const key = value.trim();
  if (!key) {
    throw new Error("A component task scope key is required.");
  }
  return key;
}
