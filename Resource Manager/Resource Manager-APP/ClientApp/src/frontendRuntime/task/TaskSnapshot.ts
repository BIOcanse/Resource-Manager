import type { TaskDescriptor } from "./TaskDescriptor.ts";

export type TaskStatus =
  | "queued"
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled"
  | "superseded";

export interface TaskProgress {
  readonly phase?: string | null;
  readonly message?: string | null;
  readonly completed?: number | null;
  readonly total?: number | null;
}

export interface TaskSnapshot {
  readonly id: string;
  readonly descriptor: TaskDescriptor;
  readonly generation: number;
  readonly scopeGeneration: number;
  readonly status: TaskStatus;
  readonly revision: number;
  readonly progress: TaskProgress | null;
  readonly error: unknown | null;
  readonly reason: unknown | null;
  readonly createdAt: string;
  readonly startedAt: string | null;
  readonly completedAt: string | null;
}

export type TaskRegistryChange =
  | {
      readonly kind: "upsert";
      readonly revision: number;
      readonly snapshot: TaskSnapshot;
    }
  | {
      readonly kind: "remove";
      readonly revision: number;
      readonly taskId: string;
    };

export type TaskOutcome<T> =
  | { readonly status: "succeeded"; readonly value: T }
  | { readonly status: "failed"; readonly error: unknown }
  | { readonly status: "cancelled"; readonly reason: unknown }
  | { readonly status: "superseded"; readonly reason: unknown };

export function isTerminalTaskStatus(status: TaskStatus): boolean {
  return status === "succeeded"
    || status === "failed"
    || status === "cancelled"
    || status === "superseded";
}
