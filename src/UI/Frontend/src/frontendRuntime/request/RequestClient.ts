import {
  ApiRequestError,
  defaultApiRequestTimeoutMs,
  requestJson,
  type JsonRequestOptions
} from "../../api/httpTransport.ts";
import {
  BackendSessionChangedError,
  BackendSessionOwner,
  BackendSessionUnavailableError
} from "../session/BackendSessionOwner.ts";
import type { BackendSessionCapture } from "../session/backendSessionTypes.ts";
import type { ResponseDecoder } from "./ResponseDecoder.ts";
import {
  RequestProblem,
  type RequestAttemptMetadata,
  type RequestProblemKind
} from "./RequestProblem.ts";
import { uiText } from "../../text.ts";

export interface RequestDescriptor<T> {
  readonly key: string;
  readonly url: string;
  readonly fallbackError: string;
  readonly decoder: ResponseDecoder<T>;
  readonly signal?: AbortSignal;
  readonly timeoutMs?: number;
  readonly request?: Omit<JsonRequestOptions, "fallbackError" | "signal" | "timeoutMs">;
}

export interface RequestOutcome<T> {
  readonly value: T;
  readonly attempt: RequestAttemptMetadata;
}

type RequestTransport = (
  url: string,
  options: JsonRequestOptions
) => Promise<unknown>;

export interface RequestClientOptions {
  readonly transport?: RequestTransport;
  readonly monotonicNow?: () => number;
  readonly attemptIdFactory?: (sequence: number) => string;
  readonly onAttemptSettled?: (observation: RequestAttemptObservation) => void;
}

export type RequestAttemptOutcome = "success" | RequestProblemKind;

export interface RequestAttemptObservation {
  readonly attempt: RequestAttemptMetadata;
  readonly outcome: RequestAttemptOutcome;
}

type CancellationCause = "caller" | "timeout" | "backend-session";

export class RequestClient {
  private readonly backendSession: BackendSessionOwner;
  private readonly transport: RequestTransport;
  private readonly monotonicNow: () => number;
  private readonly attemptIdFactory: (sequence: number) => string;
  private readonly onAttemptSettled: ((observation: RequestAttemptObservation) => void) | undefined;
  private nextSequence = 0;

  constructor(
    backendSession: BackendSessionOwner,
    options: RequestClientOptions = {})
  {
    this.backendSession = backendSession;
    this.transport = options.transport
      ?? ((url, requestOptions) => requestJson<unknown>(url, requestOptions));
    this.monotonicNow = options.monotonicNow ?? defaultMonotonicNow;
    this.attemptIdFactory = options.attemptIdFactory
      ?? ((sequence) => `request-${sequence}`);
    this.onAttemptSettled = options.onAttemptSettled;
  }

  async request<T>(descriptor: RequestDescriptor<T>): Promise<T> {
    return (await this.execute(descriptor)).value;
  }

  async execute<T>(descriptor: RequestDescriptor<T>): Promise<RequestOutcome<T>> {
    const sequence = ++this.nextSequence;
    const attemptId = this.attemptIdFactory(sequence);
    const startedAt = this.monotonicNow();
    const timeoutMs = normalizeTimeout(descriptor.timeoutMs);
    const cancellation = new AbortController();
    let cancellationCause: CancellationCause | null = null;
    let capture: BackendSessionCapture | null = null;
    let removeEpochAbort: () => void = () => undefined;

    const cancel = (cause: CancellationCause) => {
      if (cancellationCause) {
        return;
      }
      cancellationCause = cause;
      cancellation.abort(cause);
    };
    const callerAbort = () => cancel("caller");
    if (descriptor.signal?.aborted) {
      callerAbort();
    } else {
      descriptor.signal?.addEventListener("abort", callerAbort, { once: true });
    }
    const timeoutHandle = globalThis.setTimeout(() => cancel("timeout"), timeoutMs);

    try {
      capture = await this.backendSession.waitForReady(cancellation.signal);
      const epochAbort = () => cancel("backend-session");
      if (capture.signal.aborted) {
        epochAbort();
      } else {
        capture.signal.addEventListener("abort", epochAbort, { once: true });
        removeEpochAbort = () => capture?.signal.removeEventListener("abort", epochAbort);
      }

      const remainingTimeout = Math.max(
        1,
        timeoutMs - Math.max(0, this.monotonicNow() - startedAt));
      const payload = await this.transport(descriptor.url, {
        ...descriptor.request,
        cache: "no-store",
        fallbackError: descriptor.fallbackError,
        signal: cancellation.signal,
        timeoutMs: remainingTimeout
      });

      if (!this.backendSession.isCurrent(capture)) {
        cancel("backend-session");
        throw new BackendSessionChangedError();
      }

      let value: T;
      try {
        value = descriptor.decoder.decode(payload);
      } catch (error) {
        throw new RequestProblem(uiText.request.invalidFormat, {
          kind: "invalid-response",
          retryable: true,
          attempt: this.finishAttempt(
            attemptId,
            sequence,
            descriptor,
            capture.epoch,
            startedAt,
            "invalid-response"),
          cause: error
        });
      }

      return {
        value,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          capture.epoch,
          startedAt,
          "success")
      };
    } catch (error) {
      if (error instanceof RequestProblem) {
        throw error;
      }
      throw this.mapProblem(
        error,
        cancellationCause,
        attemptId,
        sequence,
        descriptor,
        capture?.epoch ?? null,
        startedAt);
    } finally {
      globalThis.clearTimeout(timeoutHandle);
      descriptor.signal?.removeEventListener("abort", callerAbort);
      removeEpochAbort();
    }
  }

  private mapProblem<T>(
    error: unknown,
    cancellationCause: CancellationCause | null,
    attemptId: string,
    sequence: number,
    descriptor: RequestDescriptor<T>,
    backendEpoch: string | null,
    startedAt: number
  ): RequestProblem {
    if (cancellationCause === "timeout") {
      return new RequestProblem(uiText.request.timeout, {
        kind: "timeout",
        retryable: true,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          backendEpoch,
          startedAt,
          "timeout"),
        cause: error
      });
    }
    if (cancellationCause === "caller") {
      return new RequestProblem(uiText.request.canceled, {
        kind: "aborted",
        retryable: false,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          backendEpoch,
          startedAt,
          "aborted"),
        cause: error
      });
    }
    if (cancellationCause === "backend-session"
      || error instanceof BackendSessionChangedError) {
      return new RequestProblem(uiText.request.sessionChanged, {
        kind: "backend-session-changed",
        retryable: true,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          backendEpoch,
          startedAt,
          "backend-session-changed"),
        cause: error
      });
    }
    if (error instanceof BackendSessionUnavailableError) {
      return new RequestProblem(uiText.request.unavailable, {
        kind: "backend-unavailable",
        retryable: true,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          backendEpoch,
          startedAt,
          "backend-unavailable"),
        cause: error
      });
    }
    if (error instanceof ApiRequestError) {
      return new RequestProblem(error.userMessage, {
        kind: error.kind,
        retryable: error.retryable,
        status: error.status,
        payload: error.payload,
        attempt: this.finishAttempt(
          attemptId,
          sequence,
          descriptor,
          backendEpoch,
          startedAt,
          error.kind),
        cause: error
      });
    }

    const kind = inferUnexpectedProblemKind(error);
    return new RequestProblem(normalizeFallback(descriptor.fallbackError), {
      kind,
      retryable: true,
      attempt: this.finishAttempt(
        attemptId,
        sequence,
        descriptor,
        backendEpoch,
        startedAt,
        kind),
      cause: error
    });
  }

  private finishAttempt(
    id: string,
    sequence: number,
    descriptor: Pick<RequestDescriptor<unknown>, "key" | "decoder" | "request">,
    backendEpoch: string | null,
    startedAt: number,
    outcome: RequestAttemptOutcome
  ): RequestAttemptMetadata {
    const finishedAt = this.monotonicNow();
    const attempt = Object.freeze({
      id,
      sequence,
      key: descriptor.key,
      decoderId: descriptor.decoder.id,
      method: String(descriptor.request?.method ?? "GET").toUpperCase(),
      backendEpoch,
      startedAt,
      finishedAt,
      durationMs: Math.max(0, finishedAt - startedAt)
    });
    if (this.onAttemptSettled) {
      try {
        this.onAttemptSettled(Object.freeze({ attempt, outcome }));
      } catch {
        // Diagnostics cannot take ownership of request completion.
      }
    }
    return attempt;
  }
}

function normalizeTimeout(value: number | undefined): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? value
    : defaultApiRequestTimeoutMs;
}

function defaultMonotonicNow(): number {
  return globalThis.performance?.now?.() ?? Date.now();
}

function normalizeFallback(value: string): string {
  const text = value.trim() || uiText.request.actionFailed;
  return /[。！？.!?]$/.test(text) ? text : `${text}。`;
}

function inferUnexpectedProblemKind(error: unknown): RequestProblemKind {
  return error instanceof BackendSessionUnavailableError
    ? "backend-unavailable"
    : "network";
}
