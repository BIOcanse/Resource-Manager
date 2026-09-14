import type { ApiRequestFailureKind } from "../../api/httpTransport.ts";

export type RequestProblemKind =
  | ApiRequestFailureKind
  | "backend-unavailable"
  | "backend-session-changed";

export interface RequestAttemptMetadata {
  readonly id: string;
  readonly sequence: number;
  readonly key: string;
  readonly decoderId: string;
  readonly method: string;
  readonly backendEpoch: string | null;
  readonly startedAt: number;
  readonly finishedAt: number;
  readonly durationMs: number;
}

interface RequestProblemInit {
  readonly kind: RequestProblemKind;
  readonly retryable: boolean;
  readonly attempt: RequestAttemptMetadata;
  readonly status?: number;
  readonly payload?: unknown;
  readonly cause?: unknown;
}

export class RequestProblem extends Error {
  readonly kind: RequestProblemKind;
  readonly retryable: boolean;
  readonly userMessage: string;
  readonly attempt: RequestAttemptMetadata;
  readonly status: number | undefined;
  readonly payload: unknown;
  override readonly cause: unknown;

  constructor(userMessage: string, init: RequestProblemInit) {
    super(userMessage);
    this.name = "RequestProblem";
    this.kind = init.kind;
    this.retryable = init.retryable;
    this.userMessage = userMessage;
    this.attempt = init.attempt;
    this.status = init.status;
    this.payload = init.payload;
    this.cause = init.cause;
  }
}
