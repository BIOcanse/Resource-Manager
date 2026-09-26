import assert from "node:assert/strict";
import {
  AppSettingsRevisionConflictError,
  getAppSettings,
  reapplyAppSettings,
  saveAppSettingsPatch
} from "../src/data/appSettings/appSettingsApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:app-settings-test"
});
const client = new RequestClient(owner);

try {
  const requests: Array<{ url: string; init: RequestInit | undefined }> = [];
  globalThis.fetch = (async (input, init) => {
    requests.push({ url: String(input), init });
    return jsonResponse(settingsResult("revision-a"));
  }) as typeof fetch;

  assert.equal((await getAppSettings(client)).revision, "revision-a");
  await saveAppSettingsPatch(client, "revision-a", { "appearance.theme": "dark" });
  await reapplyAppSettings(client);
  assert.deepEqual(requests.map((request) => request.init?.method), ["GET", "PATCH", "POST"]);
  assert.equal(requests[1].url, "/api/settings/app");
  assert.deepEqual(JSON.parse(String(requests[1].init?.body)), {
    expectedRevision: "revision-a",
    changes: { "appearance.theme": "dark" }
  });

  globalThis.fetch = (async () => jsonResponse({
    ...settingsResult("revision-b"),
    runtimeApplicationDisposition: "revisionConflict"
  }, 409)) as typeof fetch;
  await assert.rejects(
    saveAppSettingsPatch(client, "revision-a", { "appearance.theme": "light" }),
    (error: unknown) => error instanceof AppSettingsRevisionConflictError
      && error.result.revision === "revision-b");

  globalThis.fetch = (async () => jsonResponse({
    settings: { version: "" },
    revision: "revision-c"
  })) as typeof fetch;
  await assert.rejects(
    getAppSettings(client),
    (error: unknown) => error instanceof RequestProblem
      && error.kind === "invalid-response"
      && error.attempt.decoderId === "app.settings.result.v1");
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}

function settingsResult(revision: string) {
  return {
    settings: {
      version: "1.0.23",
      appearance: { theme: "system" }
    },
    revision,
    runtimeApplicationDisposition: "committedAndApplied"
  };
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}
