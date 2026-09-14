import { defineTaskKey } from "../src/frontendRuntime/task/TaskDescriptor.ts";
import type { TaskScope } from "../src/frontendRuntime/task/TaskScope.ts";

const scope = null as unknown as TaskScope;
const textKey = defineTaskKey<string>("typed.coalesced-result");
const descriptor = {
  key: textKey,
  durability: "transient",
  visibility: "hidden",
  concurrency: "coalesce"
} as const;

scope.run(descriptor, async () => "text");

// @ts-expect-error A stable task key cannot be reused with an incompatible result type.
scope.run(descriptor, async () => 42);

const numericKey = defineTaskKey<number>("typed.numeric-result");

// @ts-expect-error Distinct result types cannot be substituted even when runtime names differ.
const incompatibleKey: typeof textKey = numericKey;
