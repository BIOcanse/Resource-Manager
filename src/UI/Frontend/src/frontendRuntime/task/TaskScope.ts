import type {
  ScopedTaskDescriptor,
  TaskDescriptor
} from "./TaskDescriptor.ts";
import type {
  TaskOutcome,
  TaskProgress,
  TaskSnapshot
} from "./TaskSnapshot.ts";

export interface TaskExecutionContext {
  readonly signal: AbortSignal;
  isCurrent(): boolean;
  commit(effect: () => void): boolean;
  reportProgress(progress: TaskProgress): boolean;
}

export type TaskExecutor<T> = (
  context: TaskExecutionContext
) => T | Promise<T>;

export interface TaskRun<T> {
  readonly id: string;
  readonly descriptor: TaskDescriptor;
  readonly snapshot: TaskSnapshot;
  readonly completion: Promise<TaskOutcome<T>>;
  subscribe(listener: (snapshot: TaskSnapshot) => void): () => void;
  cancel(reason?: unknown): void;
}

export interface TaskScope {
  readonly key: string;
  readonly generation: number;
  readonly closed: boolean;
  run<T>(
    descriptor: ScopedTaskDescriptor<T>,
    executor: TaskExecutor<T>
  ): TaskRun<T>;
  close(reason?: unknown): void;
}
