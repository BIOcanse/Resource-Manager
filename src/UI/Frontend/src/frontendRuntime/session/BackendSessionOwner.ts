import {
  getHostMessageTransport,
  type HostMessageTransport
} from "../../host/hostMessageTransport.ts";
import type {
  BackendSessionCapture,
  BackendSessionInfo,
  BackendSessionSnapshot,
  HostBackendSessionMessage
} from "./backendSessionTypes.ts";
import { uiText } from "../../text.ts";

export class BackendSessionUnavailableError extends Error {
  constructor(message = uiText.session.notReady) {
    super(message);
    this.name = "BackendSessionUnavailableError";
  }
}

export class BackendSessionChangedError extends Error {
  constructor() {
    super(uiText.session.sessionChanged);
    this.name = "BackendSessionChangedError";
  }
}

export interface BackendSessionOwnerOptions {
  readonly transport?: HostMessageTransport | null;
  readonly allowStandalone?: boolean;
  readonly standaloneEpochFactory?: () => string;
  readonly waitingTimeoutMs?: number;
  readonly schedule?: (callback: () => void, delayMs: number) => unknown;
  readonly cancelSchedule?: (handle: unknown) => void;
}

type BackendSessionListener = (snapshot: BackendSessionSnapshot) => void;

export class BackendSessionOwner {
  private readonly listeners = new Set<BackendSessionListener>();
  private readonly transport: HostMessageTransport | null;
  private readonly waitingTimeoutMs: number;
  private readonly schedule: (callback: () => void, delayMs: number) => unknown;
  private readonly cancelSchedule: (handle: unknown) => void;
  private unsubscribeHostMessages: (() => void) | null = null;
  private epochController: AbortController | null = null;
  private waitingTimer: unknown | null = null;
  private lastHostPublicationRevision: string | null = null;
  private snapshotValue: BackendSessionSnapshot = {
    status: "waiting",
    session: null,
    reason: null,
    publicationRevision: null,
    revision: 0
  };

  constructor(options: BackendSessionOwnerOptions = {}) {
    this.waitingTimeoutMs = normalizeWaitingTimeout(options.waitingTimeoutMs);
    this.schedule = options.schedule
      ?? ((callback, delayMs) => globalThis.setTimeout(callback, delayMs));
    this.cancelSchedule = options.cancelSchedule
      ?? ((handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>));
    this.transport = options.transport === undefined
      ? getHostMessageTransport()
      : options.transport;
    if (!this.transport) {
      if (options.allowStandalone !== true) {
        this.setUnavailable(uiText.session.noTrustedHost);
        return;
      }
      const epoch = normalizeStandaloneEpoch(
        options.standaloneEpochFactory?.() ?? createStandaloneEpoch());
      this.acceptReadySession({
        epoch,
        authority: "standalone-page"
      }, null);
      return;
    }

    if (!this.transport.canSubscribeMessages) {
      this.setUnavailable(uiText.session.hostCannotPublish);
      return;
    }

    this.unsubscribeHostMessages = this.transport.subscribeMessages(
      (message) => this.handleHostMessage(message));
    if (!this.unsubscribeHostMessages) {
      this.setUnavailable(uiText.session.hostCannotSubscribe);
      return;
    }

    this.requestSession();
  }

  get snapshot(): BackendSessionSnapshot {
    return this.snapshotValue;
  }

  subscribe(listener: BackendSessionListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  capture(): BackendSessionCapture {
    const snapshot = this.snapshotValue;
    const controller = this.epochController;
    if (snapshot.status !== "ready" || !snapshot.session || !controller) {
      throw new BackendSessionUnavailableError(snapshot.reason ?? undefined);
    }

    return {
      epoch: snapshot.session.epoch,
      authority: snapshot.session.authority,
      signal: controller.signal
    };
  }

  async waitForReady(signal?: AbortSignal): Promise<BackendSessionCapture> {
    if (signal?.aborted) {
      throw abortReason(signal);
    }

    if (this.snapshotValue.status === "ready") {
      return this.capture();
    }
    if (this.snapshotValue.status === "unavailable"
      || this.snapshotValue.status === "disposed") {
      throw new BackendSessionUnavailableError(this.snapshotValue.reason ?? undefined);
    }

    return new Promise<BackendSessionCapture>((resolve, reject) => {
      let settled = false;
      const finish = (action: () => void) => {
        if (settled) {
          return;
        }
        settled = true;
        unsubscribe();
        signal?.removeEventListener("abort", handleAbort);
        action();
      };
      const handleAbort = () => finish(() => reject(abortReason(signal!)));
      const unsubscribe = this.subscribe((snapshot) => {
        if (snapshot.status === "ready") {
          finish(() => resolve(this.capture()));
        } else if (snapshot.status === "unavailable"
          || snapshot.status === "disposed") {
          finish(() => reject(new BackendSessionUnavailableError(snapshot.reason ?? undefined)));
        }
      });
      signal?.addEventListener("abort", handleAbort, { once: true });
      if (signal?.aborted) {
        handleAbort();
      }
    });
  }

  retry(signal?: AbortSignal): Promise<BackendSessionCapture> {
    if (this.snapshotValue.status === "disposed") {
      return Promise.reject(
        new BackendSessionUnavailableError(this.snapshotValue.reason ?? undefined));
    }
    if (this.snapshotValue.status === "ready") {
      return Promise.resolve(this.capture());
    }
    if (this.snapshotValue.status !== "waiting") {
      this.requestSession();
    }
    return this.waitForReady(signal);
  }

  isCurrent(capture: BackendSessionCapture): boolean {
    return this.snapshotValue.status === "ready"
      && this.snapshotValue.session?.epoch === capture.epoch
      && this.epochController?.signal === capture.signal
      && !capture.signal.aborted;
  }

  dispose(): void {
    if (this.snapshotValue.status === "disposed") {
      return;
    }

    this.unsubscribeHostMessages?.();
    this.unsubscribeHostMessages = null;
    this.clearWaitingTimer();
    this.abortCurrentEpoch();
    this.publish({
      status: "disposed",
      session: null,
      reason: uiText.session.frontendClosed,
      publicationRevision: this.snapshotValue.publicationRevision,
      revision: this.snapshotValue.revision + 1
    });
    this.listeners.clear();
  }

  private handleHostMessage(value: unknown): void {
    if (this.snapshotValue.status === "disposed") {
      return;
    }

    const message = decodeHostBackendSessionMessage(value);
    if (!message) {
      return;
    }

    if (!isNewerDecimal(
      message.publicationRevision,
      this.lastHostPublicationRevision)) {
      return;
    }
    this.lastHostPublicationRevision = message.publicationRevision;

    if (message.state === "unavailable") {
      this.setUnavailable(
        uiText.session.sessionUnavailable(message.reasonCode),
        message.publicationRevision);
      return;
    }

    this.acceptReadySession({
      epoch: message.epoch,
      authority: "native-ui"
    }, message.publicationRevision);
  }

  private acceptReadySession(
    session: BackendSessionInfo,
    publicationRevision: string | null
  ): void {
    if (this.snapshotValue.status === "disposed") {
      return;
    }

    this.clearWaitingTimer();
    const current = this.snapshotValue.session;
    if (this.snapshotValue.status === "ready"
      && current?.epoch === session.epoch
      && current.authority === session.authority) {
      if (this.snapshotValue.publicationRevision !== publicationRevision) {
        this.publish({
          ...this.snapshotValue,
          publicationRevision,
          revision: this.snapshotValue.revision + 1
        });
      }
      return;
    }

    this.abortCurrentEpoch();
    this.epochController = new AbortController();
    this.publish({
      status: "ready",
      session,
      reason: null,
      publicationRevision,
      revision: this.snapshotValue.revision + 1
    });
  }

  private setUnavailable(
    reason: string,
    publicationRevision: string | null = this.snapshotValue.publicationRevision
  ): void {
    if (this.snapshotValue.status === "disposed") {
      return;
    }
    this.clearWaitingTimer();
    if (this.snapshotValue.status === "unavailable"
      && this.snapshotValue.reason === reason) {
      if (this.snapshotValue.publicationRevision !== publicationRevision) {
        this.publish({
          ...this.snapshotValue,
          publicationRevision,
          revision: this.snapshotValue.revision + 1
        });
      }
      return;
    }

    this.abortCurrentEpoch();
    this.publish({
      status: "unavailable",
      session: null,
      reason,
      publicationRevision,
      revision: this.snapshotValue.revision + 1
    });
  }

  private requestSession(): void {
    if (this.snapshotValue.status === "disposed") {
      return;
    }
    if (!this.transport || !this.unsubscribeHostMessages) {
      this.setUnavailable(uiText.session.hostCannotRerequest);
      return;
    }

    this.clearWaitingTimer();
    if (this.snapshotValue.status !== "waiting"
      || this.snapshotValue.reason !== null
      || this.snapshotValue.session !== null) {
      this.abortCurrentEpoch();
      this.publish({
        status: "waiting",
        session: null,
        reason: null,
        publicationRevision: this.snapshotValue.publicationRevision,
        revision: this.snapshotValue.revision + 1
      });
    }

    this.waitingTimer = this.schedule(() => {
      this.waitingTimer = null;
      if (this.snapshotValue.status === "waiting") {
        this.setUnavailable(uiText.session.waitTimeout);
      }
    }, this.waitingTimeoutMs);

    try {
      this.transport.postMessage({ type: "host.backend-session.request" });
    } catch (error: unknown) {
      this.setUnavailable(
        uiText.session.requestFailed(errorMessage(error)));
    }
  }

  private clearWaitingTimer(): void {
    if (this.waitingTimer === null) {
      return;
    }
    this.cancelSchedule(this.waitingTimer);
    this.waitingTimer = null;
  }

  private abortCurrentEpoch(): void {
    if (this.epochController && !this.epochController.signal.aborted) {
      this.epochController.abort(new BackendSessionChangedError());
    }
    this.epochController = null;
  }

  private publish(snapshot: BackendSessionSnapshot): void {
    this.snapshotValue = snapshot;
    for (const listener of this.listeners) {
      listener(snapshot);
    }
  }
}

export function decodeHostBackendSessionMessage(
  value: unknown): HostBackendSessionMessage | null
{
  if (!isRecord(value)
    || value.type !== "host.backend-session"
    || value.schemaVersion !== 1
    || !isCanonicalDecimal(value.publicationRevision, 20)
    || (value.state !== "ready" && value.state !== "unavailable")) {
    return null;
  }
  if (value.state === "unavailable") {
    if (!isBoundedText(value.reasonCode, 1, 128)) {
      return null;
    }
    return {
      type: "host.backend-session",
      schemaVersion: 1,
      publicationRevision: value.publicationRevision,
      state: "unavailable",
      reasonCode: value.reasonCode
    };
  }

  if (!isBoundedText(value.epoch, 1, 256)) {
    return null;
  }

  return {
    type: "host.backend-session",
    schemaVersion: 1,
    publicationRevision: value.publicationRevision,
    state: "ready",
    epoch: value.epoch
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isBoundedText(value: unknown, minimum: number, maximum: number): value is string {
  return typeof value === "string"
    && value.length >= minimum
    && value.length <= maximum;
}

function isCanonicalDecimal(value: unknown, maximumDigits: number): value is string {
  return typeof value === "string"
    && value.length <= maximumDigits
    && /^(0|[1-9][0-9]*)$/.test(value);
}

function isNewerDecimal(candidate: string, previous: string | null): boolean {
  if (previous === null) {
    return true;
  }
  if (candidate.length !== previous.length) {
    return candidate.length > previous.length;
  }
  return candidate > previous;
}

function normalizeStandaloneEpoch(value: string): string {
  const epoch = value.trim();
  if (!epoch || epoch.length > 256) {
    throw new Error("The standalone backend epoch is invalid.");
  }
  return epoch;
}

function createStandaloneEpoch(): string {
  const randomId = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return `standalone-page:${randomId}`;
}

function normalizeWaitingTimeout(value: number | undefined): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? Math.max(1, Math.floor(value))
    : 10_000;
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message.trim()
    ? error.message
    : uiText.session.unknownError;
}

function abortReason(signal: AbortSignal): Error {
  return signal.reason instanceof Error
    ? signal.reason
    : new Error(uiText.session.operationCanceled);
}
