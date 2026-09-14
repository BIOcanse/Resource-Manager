export type ApiRequestFailureKind =
  | "timeout"
  | "aborted"
  | "network"
  | "http"
  | "invalid-response";

interface ApiRequestErrorInit {
  kind: ApiRequestFailureKind;
  status?: number;
  payload?: unknown;
  retryable: boolean;
}

export class ApiRequestError extends Error {
  readonly kind: ApiRequestFailureKind;
  readonly status: number | undefined;
  readonly payload: unknown;
  readonly retryable: boolean;
  readonly userMessage: string;

  constructor(userMessage: string, init: ApiRequestErrorInit) {
    super(userMessage);
    this.name = "ApiRequestError";
    this.kind = init.kind;
    this.status = init.status;
    this.payload = init.payload;
    this.retryable = init.retryable;
    this.userMessage = userMessage;
  }
}

export type JsonRequestOptions = Omit<RequestInit, "signal"> & {
  fallbackError: string;
  signal?: AbortSignal;
  timeoutMs?: number;
  allowEmptyResponse?: boolean;
};

export const defaultApiRequestTimeoutMs = 10_000;

export async function requestJson<T>(url: string, options: JsonRequestOptions): Promise<T> {
  const {
    fallbackError,
    signal: callerSignal,
    timeoutMs: requestedTimeoutMs,
    allowEmptyResponse = false,
    ...requestInit
  } = options;
  const timeoutMs = normalizeTimeout(requestedTimeoutMs);
  const controller = new AbortController();
  let cancellationError: ApiRequestError | null = null;
  let rejectCancellation: (error: ApiRequestError) => void = () => undefined;
  const cancellation = new Promise<never>((_resolve, reject) => {
    rejectCancellation = reject;
  });
  const cancel = (error: ApiRequestError) => {
    if (cancellationError) {
      return;
    }

    cancellationError = error;
    controller.abort(error);
    rejectCancellation(error);
  };
  const abortFromCaller = () => cancel(new ApiRequestError("请求已取消", {
    kind: "aborted",
    retryable: false
  }));
  if (callerSignal?.aborted) {
    abortFromCaller();
  } else {
    callerSignal?.addEventListener("abort", abortFromCaller, { once: true });
  }
  const timeoutHandle = globalThis.setTimeout(() => cancel(new ApiRequestError(
    "等待本机服务响应超时，请重试",
    { kind: "timeout", retryable: true })), timeoutMs);

  try {
    let response: Response;
    try {
      response = await Promise.race([
        fetch(url, { ...requestInit, signal: controller.signal }),
        cancellation
      ]);
    } catch (error) {
      if (error instanceof ApiRequestError) {
        throw error;
      }
      throw new ApiRequestError(fallbackMessage(fallbackError), {
        kind: "network",
        retryable: true
      });
    }

    let text: string;
    try {
      text = await Promise.race([response.text(), cancellation]);
    } catch (error) {
      if (error instanceof ApiRequestError) {
        throw error;
      }
      throw new ApiRequestError("本机服务返回的数据无法读取，请重试", {
        kind: "invalid-response",
        status: response.status,
        retryable: true
      });
    }

    let payload: unknown = null;
    let invalidJson = false;
    if (text) {
      try {
        payload = JSON.parse(text) as unknown;
      } catch {
        invalidJson = true;
      }
    }

    if (!response.ok) {
      throw new ApiRequestError(httpFailureMessage(payload, fallbackError), {
        kind: "http",
        status: response.status,
        payload: invalidJson ? null : payload,
        retryable: isRetryableHttpStatus(response.status)
      });
    }

    if ((!text || invalidJson) && !allowEmptyResponse) {
      throw new ApiRequestError("本机服务返回的数据无法读取，请重试", {
        kind: "invalid-response",
        status: response.status,
        retryable: true
      });
    }

    return payload as T;
  } finally {
    globalThis.clearTimeout(timeoutHandle);
    callerSignal?.removeEventListener("abort", abortFromCaller);
  }
}

export async function requestNoContent(
  url: string,
  options: Omit<JsonRequestOptions, "allowEmptyResponse">
) {
  await requestJson<unknown>(url, { ...options, allowEmptyResponse: true });
}

function normalizeTimeout(value: number | undefined) {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? value
    : defaultApiRequestTimeoutMs;
}

function httpFailureMessage(payload: unknown, fallback: string) {
  void payload;
  return fallbackMessage(fallback);
}

function isRetryableHttpStatus(status: number) {
  return status === 408 || status === 425 || status === 429 || status >= 500;
}

function fallbackMessage(value: string) {
  const text = value.trim() || "操作失败，请稍后重试";
  return /[。！？.!?]$/.test(text) ? text : `${text}。`;
}
