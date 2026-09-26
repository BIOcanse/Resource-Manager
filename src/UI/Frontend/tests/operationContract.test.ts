import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const sources = {
  api: readSource("../src/api.ts"),
  types: readSource("../src/types.ts"),
  management: readSource("../src/stores/managementStore.ts"),
  migration: readSource("../src/stores/migrationStore.ts"),
  softwareActions: readSource("../src/app/useSoftwareActions.ts"),
  page: readSource("../src/features/management/components/ManagementPage.tsx"),
  app: readSource("../src/App.tsx"),
  scheduler: readSource("../src/app/usePageRefreshScheduler.ts"),
  decoder: readSource("../src/data/operations/operationsStateDecoder.ts"),
  operationsApi: readSource("../src/data/operations/operationsApi.ts"),
  operationCommands: readSource("../src/data/operations/operationCommands.ts"),
  operationRegistry: readSource("../src/frontendRuntime/operations/OperationRegistry.ts"),
  frontendSources: readSource("../src/frontendRuntime/source/FrontendSources.ts")
};

for (const state of [
  "queued",
  "startPending",
  "running",
  "cancelPending",
  "retryWait",
  "recoveryPending",
  "succeeded",
  "failed",
  "canceled",
  "stateUncertain"
]) {
  assert.match(sources.types, new RegExp(`"${state}"`));
}

assert.doesNotMatch(
  sources.api,
  /getJson<OperationSnapshot\[\]>/);
assert.doesNotMatch(sources.api, /operationsStateDecoder|operationSnapshotDecoder/);
assert.doesNotMatch(sources.api, /\/api\/operations/);
assert.match(sources.decoder, /host-manager\.operations\.state\.v1/);
assert.match(sources.decoder, /canonical uint64 decimal string/);
assert.match(sources.operationsApi, /RequestClient/);
assert.match(sources.operationsApi, /\/api\/operations\/subscribe/);
assert.match(sources.frontendSources, /key: "host-manager\.operations\.state"/);
assert.match(sources.frontendSources, /BackendCurrentValueSource/);
assert.match(sources.operationRegistry, /source\.subscribe/);
assert.doesNotMatch(sources.operationRegistry, /runtimeCapabilities|capabilit|profile-disabled/i);
assert.match(sources.operationRegistry, /waitForTerminal/);
assert.match(sources.operationRegistry, /operationSnapshotDecoder/);
assert.doesNotMatch(
  sources.operationRegistry,
  /activeRefreshInterval|idleRefreshInterval|sourceLease|backendEpoch|projectedEpoch|sourceAcceptedAttempt/);
assert.match(sources.operationCommands, /componentInstallCommand/);
assert.match(sources.operationCommands, /migrationDiscoveryStartCommand/);
assert.match(sources.management, /options\.operations\.subscribe/);
assert.match(sources.management, /options\.operations\.submit/);
assert.match(sources.management, /options\.operations\.waitForTerminal/);
assert.match(sources.management, /stateUncertain/);
assert.match(sources.migration, /migrationExecuteCommand/);
assert.match(sources.migration, /migrationRestoreCommand/);
assert.match(sources.migration, /migrationDiscoveryStartCommand/);
assert.match(sources.migration, /options\.operations\.cancel/);
assert.match(sources.softwareActions, /migrationRestoreCommand/);
assert.match(sources.softwareActions, /options\.operations\.waitForTerminal/);
assert.match(sources.page, /managementActionKeyFromOperation/);
assert.match(sources.app, /operations: frontendRuntime\.operationRegistry/);
assert.doesNotMatch(sources.scheduler, /refreshOperations|stopOperationPolling/);

for (const source of [
  sources.management,
  sources.migration,
  sources.softwareActions
]) {
  assert.doesNotMatch(source, /getOperations|getOperation|delay\(800\)/);
}

const combined = Object.values(sources).join("\n");
for (const forbidden of [
  "BackgroundTask",
  "ManagedTask",
  "refreshTasks",
  "cancelTaskPolling",
  "managementActionKeyFromTask",
  "/api/tasks",
  "install-task",
  "uninstall-task",
  "taskPolling",
  "activeOperationPolls",
  "pollOperationUntilTerminal",
  "stopOperationPolling"
]) {
  assert.doesNotMatch(combined, new RegExp(escapeRegExp(forbidden), "i"));
}

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
