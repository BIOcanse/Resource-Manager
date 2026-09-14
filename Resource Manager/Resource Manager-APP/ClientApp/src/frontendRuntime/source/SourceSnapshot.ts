export type SourceStatus =
  | "idle"
  | "loading"
  | "ready"
  | "refreshing"
  | "stale"
  | "unavailable"
  | "error"
  | "disposed";

export type SourceDomainRevision = number | string;

export interface SourceSnapshot<T> {
  readonly key: string;
  readonly status: SourceStatus;
  readonly data: T | null;
  readonly error: unknown | null;
  readonly backendEpoch: string | null;
  readonly revision: number;
  readonly acceptedAttempt: number | null;
  readonly domainRevision: SourceDomainRevision | null;
  readonly capturedAt: string | null;
}

export function sourceCanRender<T>(snapshot: SourceSnapshot<T>): boolean {
  return snapshot.data !== null
    && (snapshot.status === "ready"
      || snapshot.status === "refreshing"
      || snapshot.status === "stale");
}
