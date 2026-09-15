import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import {
  appSettingsResultDecoder,
  readSettingsFailureDisposition,
  type CommittedAppSettingsResult
} from "./appSettingsResultDecoder.ts";
import { uiText } from "../../text.ts";

type AppSettingsRequestClient = Pick<RequestClient, "request">;

export class AppSettingsRevisionConflictError extends Error {
  readonly result: CommittedAppSettingsResult;

  constructor(result: CommittedAppSettingsResult) {
    super(uiText.misc.settingsConflict);
    this.name = "AppSettingsRevisionConflictError";
    this.result = result;
  }
}

export function getAppSettings(
  requestClient: AppSettingsRequestClient,
  signal?: AbortSignal
): Promise<CommittedAppSettingsResult> {
  return requestClient.request({
    key: "app.settings.get",
    url: "/api/settings/app",
    fallbackError: uiText.misc.readSettingsFailed,
    decoder: appSettingsResultDecoder,
    signal,
    request: { method: "GET" }
  });
}

export async function saveAppSettingsPatch(
  requestClient: AppSettingsRequestClient,
  expectedRevision: string,
  changes: Record<string, unknown>,
  signal?: AbortSignal
): Promise<CommittedAppSettingsResult> {
  try {
    return await requestClient.request({
      key: "app.settings.patch",
      url: "/api/settings/app",
      fallbackError: uiText.misc.saveSettingsFailed,
      decoder: appSettingsResultDecoder,
      signal,
      request: {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedRevision, changes })
      }
    });
  } catch (error) {
    throwSettingsFailure(error);
  }
}

export function reapplyAppSettings(
  requestClient: AppSettingsRequestClient,
  signal?: AbortSignal
): Promise<CommittedAppSettingsResult> {
  return requestClient.request({
    key: "app.settings.reapply",
    url: "/api/settings/app/reapply",
    fallbackError: uiText.misc.reapplySettingsFailed,
    decoder: appSettingsResultDecoder,
    signal,
    request: { method: "POST" }
  });
}

function throwSettingsFailure(error: unknown): never {
  if (error instanceof RequestProblem) {
    const disposition = readSettingsFailureDisposition(error.payload);
    if (error.status === 409 && disposition === "revisionConflict") {
      try {
        throw new AppSettingsRevisionConflictError(
          appSettingsResultDecoder.decode(error.payload));
      } catch (decodeError) {
        if (decodeError instanceof AppSettingsRevisionConflictError) {
          throw decodeError;
        }
      }
    }
    if (disposition === "savedNotApplied") {
      throw new Error(uiText.misc.settingsSavedNotApplied, { cause: error });
    }
  }
  throw error;
}
