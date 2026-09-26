export type TaskDurability =
  | "transient"
  | "frontend-session"
  | "external-durable";

export type TaskVisibility = "hidden" | "diagnostics" | "user";

export type TaskConcurrency = "allow" | "replace" | "coalesce" | "serial";

declare const taskResultType: unique symbol;

export interface TaskKey<T> {
  readonly value: string;
  readonly [taskResultType]: (value: T) => T;
}

export function defineTaskKey<T>(value: string): TaskKey<T> {
  const normalized = normalizeTaskKey(value);
  return Object.freeze({ value: normalized }) as TaskKey<T>;
}

export interface TaskDescriptor {
  readonly key: string;
  readonly title?: string;
  readonly scopeKey: string;
  readonly groupKey?: string;
  readonly durability: TaskDurability;
  readonly visibility: TaskVisibility;
  readonly concurrency: TaskConcurrency;
}

export interface ScopedTaskDescriptor<T> {
  readonly key: TaskKey<T>;
  readonly title?: string;
  readonly groupKey?: string;
  readonly durability: TaskDurability;
  readonly visibility: TaskVisibility;
  readonly concurrency: TaskConcurrency;
}

function normalizeTaskKey(value: string): string {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized || normalized.includes("\0")) {
    throw new Error("A valid task key is required.");
  }
  return normalized;
}
