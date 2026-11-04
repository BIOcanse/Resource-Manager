export type BackendSessionStatus =
  | "waiting"
  | "ready"
  | "unavailable"
  | "disposed";

export type BackendSessionAuthority = "native-ui" | "standalone-page";

export interface BackendSessionInfo {
  readonly epoch: string;
  readonly authority: BackendSessionAuthority;
}

export interface BackendSessionSnapshot {
  readonly status: BackendSessionStatus;
  readonly session: BackendSessionInfo | null;
  readonly reason: string | null;
  readonly publicationRevision: string | null;
  readonly revision: number;
}

export interface BackendSessionCapture {
  readonly epoch: string;
  readonly authority: BackendSessionAuthority;
  readonly signal: AbortSignal;
}

export interface HostBackendSessionReadyMessage {
  readonly type: "host.backend-session";
  readonly schemaVersion: 1;
  readonly publicationRevision: string;
  readonly state: "ready";
  readonly epoch: string;
}

export interface HostBackendSessionUnavailableMessage {
  readonly type: "host.backend-session";
  readonly schemaVersion: 1;
  readonly publicationRevision: string;
  readonly state: "unavailable";
  readonly reasonCode: string;
}

export type HostBackendSessionMessage =
  | HostBackendSessionReadyMessage
  | HostBackendSessionUnavailableMessage;
