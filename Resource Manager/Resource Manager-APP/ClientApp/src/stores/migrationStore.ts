import { createMemo, createSignal } from "solid-js";
import type { Accessor, Setter } from "solid-js";
import {
  getJson,
  postJson
} from "../api";
import {
  migrationDiscoveryStartCommand,
  migrationExecuteCommand,
  migrationRestoreCommand
} from "../data/operations/operationCommands";
import type {
  OperationRegistry,
  OperationRegistrySnapshot
} from "../frontendRuntime/operations/OperationRegistry";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import { selectOperations } from "../frontendRuntime/operations/operationSelectors";
import { defineTaskKey } from "../frontendRuntime/task/TaskDescriptor";
import type { TaskRegistry } from "../frontendRuntime/task/TaskRegistry";
import type { ScopedTaskDescriptor } from "../frontendRuntime/task/TaskDescriptor";
import type { TaskExecutionContext } from "../frontendRuntime/task/TaskScope";
import { isTerminalTaskStatus } from "../frontendRuntime/task/TaskSnapshot";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import {
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { uiText } from "../text";
import { userFacingErrorMessage, userFacingMessage } from "../presentation/userFacingText";
import type {
  DiscoverySession,
  MigrationCandidate,
  MigrationKind,
  MigrationPlan,
  MigrationRoots,
  MigrationTargetCategory,
  OperationSnapshot,
  SoftwareDataMigrationRecord,
} from "../types";

export interface MigrationStoreOptions {
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  refreshSoftware: () => Promise<void>;
  enabled: Accessor<boolean>;
  operations: Pick<
    OperationRegistry,
    "snapshot" | "subscribe" | "submit" | "cancel" | "waitForTerminal"
  >;
  tasks: Pick<TaskRegistry, "openScope" | "listSnapshots" | "subscribe">;
}

export interface MigrationWorkbenchStore {
  roots: Accessor<MigrationRoots | null>;
  rootsObservation: Accessor<ObservationState>;
  records: Accessor<SoftwareDataMigrationRecord[]>;
  recordsObservation: Accessor<ObservationState>;
  sessions: Accessor<DiscoverySession[]>;
  sessionsObservation: Accessor<ObservationState>;
  candidates: Accessor<MigrationCandidate[]>;
  activeSessionId: Accessor<string | null>;
  softwareName: Accessor<string>;
  kind: Accessor<MigrationKind | string>;
  targetCategory: Accessor<MigrationTargetCategory | string>;
  sourcePaths: Accessor<string>;
  discoveryProgramRootPaths: Accessor<string>;
  discoveryProcessNames: Accessor<string>;
  allowMediumRisk: Accessor<boolean>;
  plan: Accessor<MigrationPlan | null>;
  status: Accessor<string>;
  workbenchAvailable: Accessor<boolean>;
  operationActionsAvailable: Accessor<boolean>;
  previewInProgress: Accessor<boolean>;
  candidateLookupInProgress: Accessor<boolean>;
  executeInProgress: Accessor<boolean>;
  discoveryStartInProgress: Accessor<boolean>;
  discoveryStopInProgress: Accessor<boolean>;
  isRestoreInProgress: (recordId: string) => boolean;
  setSoftwareName: Setter<string>;
  setKind: (value: string) => void;
  setTargetCategory: Setter<string>;
  setSourcePaths: Setter<string>;
  setDiscoveryProgramRootPaths: Setter<string>;
  setDiscoveryProcessNames: Setter<string>;
  setAllowMediumRisk: Setter<boolean>;
  setCandidates: Setter<MigrationCandidate[]>;
  resetPlan: () => void;
  refreshRoots: () => Promise<void>;
  refreshRecords: () => Promise<void>;
  refreshSessions: () => Promise<void>;
  preview: () => Promise<void>;
  execute: () => Promise<void>;
  findCandidates: () => Promise<void>;
  startSession: () => Promise<void>;
  stopSession: () => Promise<void>;
  useCandidate: (candidate: MigrationCandidate) => void;
  migrateCandidate: (candidate: MigrationCandidate) => Promise<void>;
  restore: (record: SoftwareDataMigrationRecord) => Promise<void>;
  fillDataMigration: (softwareName: string, sourcePaths?: string[]) => void;
  fillRootMigration: (softwareName: string, sourcePaths: string[]) => void;
  dispose: () => void;
}

export function createMigrationStore(options: MigrationStoreOptions): MigrationWorkbenchStore {
  const taskScope = options.tasks.openScope("migration-workbench");
  const [roots, setRoots] = createSignal<MigrationRoots | null>(null);
  const [rootsObservation, setRootsObservation] = createSignal<ObservationState>(loadingObservation());
  const [records, setRecords] = createSignal<SoftwareDataMigrationRecord[]>([]);
  const [recordsObservation, setRecordsObservation] = createSignal<ObservationState>(loadingObservation());
  const [sessions, setSessions] = createSignal<DiscoverySession[]>([]);
  const [sessionsObservation, setSessionsObservation] = createSignal<ObservationState>(loadingObservation());
  const [candidates, setCandidates] = createSignal<MigrationCandidate[]>([]);
  const [operationProjection, setOperationProjection] =
    createSignal<OperationRegistrySnapshot>(options.operations.snapshot);
  const activeSessionId = createMemo(() => options.enabled()
    ? selectOperations(operationProjection(), {
      kind: "discovery.start",
      active: true
    })[0]?.id ?? null
    : null);
  const [softwareName, setSoftwareName] = createSignal("");
  const [kind, setKindSignal] = createSignal<MigrationKind | string>("Data");
  const [targetCategory, setTargetCategory] = createSignal<MigrationTargetCategory | string>("UserData");
  const [sourcePaths, setSourcePaths] = createSignal("");
  const [discoveryProgramRootPaths, setDiscoveryProgramRootPaths] = createSignal("");
  const [discoveryProcessNames, setDiscoveryProcessNames] = createSignal("");
  const [allowMediumRisk, setAllowMediumRisk] = createSignal(false);
  const [plan, setPlan] = createSignal<MigrationPlan | null>(null);
  const [status, setStatus] = createSignal("尚未预览");
  const [taskRevision, setTaskRevision] = createSignal(0);
  const unsubscribeOperations = options.operations.subscribe(setOperationProjection);
  const unsubscribeTasks = options.tasks.subscribe(() => {
    setTaskRevision((current) => current + 1);
  });
  const previewInProgress = createMemo(() => taskActive(migrationPreviewTaskKey.value));
  const candidateLookupInProgress = createMemo(() => taskActive(migrationCandidateLookupTaskKey.value));
  const workbenchAvailable = createMemo(() => options.enabled());
  const operationActionsAvailable = createMemo(() => options.enabled()
    && operationProjection().actionsEnabled);
  const executeInProgress = createMemo(() => taskActive(migrationExecuteSubmitTaskKey.value)
    || hasActiveOperation("migration.execute"));
  const discoveryStartInProgress = createMemo(() => taskActive(migrationDiscoveryStartSubmitTaskKey.value));
  const discoveryStopInProgress = createMemo(() => {
    const active = activeDiscoveryOperation();
    return active !== null
      && (taskActive(migrationDiscoveryStopTaskKey(active.id).value)
        || active.cancelRequested
        || active.state === "cancelPending");
  });

  function setKind(value: string) {
    setKindSignal(value);
    resetPlan();
  }

  function resetPlan() {
    setPlan(null);
    setStatus("尚未预览");
  }

  async function refreshRoots() {
    if (!options.enabled()) {
      setRootsObservation(profileDisabledObservation("当前启动配置未启用迁移运行时。"));
      return;
    }

    setRootsObservation(refreshingObservation(rootsObservation()));
    try {
      setRoots(await getJson<MigrationRoots>("/api/migrations/roots"));
      setRootsObservation(readyObservation());
    } catch (error) {
      setRootsObservation(failedObservation(
        rootsObservation(),
        userFacingErrorMessage(error, "迁移根目录读取失败")));
    }
  }

  async function refreshRecords() {
    if (!options.enabled()) {
      setRecordsObservation(profileDisabledObservation("当前启动配置未启用迁移运行时。"));
      return;
    }

    setRecordsObservation(refreshingObservation(recordsObservation()));
    try {
      setRecords(await getJson<SoftwareDataMigrationRecord[]>("/api/migrations/records"));
      setRecordsObservation(readyObservation());
    } catch (error) {
      setRecordsObservation(failedObservation(
        recordsObservation(),
        userFacingErrorMessage(error, "迁移记录读取失败")));
    }
  }

  async function refreshSessions() {
    if (!options.enabled()) {
      setSessionsObservation(profileDisabledObservation("当前启动配置未启用迁移运行时。"));
      return;
    }

    setSessionsObservation(refreshingObservation(sessionsObservation()));
    try {
      const loaded = await getJson<DiscoverySession[]>(
        "/api/migrations/discovery/sessions");
      setSessions(loaded);
      setSessionsObservation(readyObservation());
    } catch (error) {
      setSessionsObservation(failedObservation(
        sessionsObservation(),
        userFacingErrorMessage(error, "发现会话读取失败")));
    }
  }

  async function preview() {
    if (!options.enabled()) {
      return;
    }
    const request = buildRequest(false);
    await runFrontendTask({
      key: migrationPreviewTaskKey,
      title: migrationTaskTitle("预览迁移", request.softwareName),
      durability: "transient",
      visibility: "user",
      concurrency: "replace"
    }, async (context) => {
      const nextPlan = await postJson<MigrationPlan>(
        "/api/migrations/preview",
        request,
        "迁移预览失败",
        false,
        { signal: context.signal });
      context.commit(() => {
        setPlan(nextPlan);
        setStatus(nextPlan.canExecute ? "可以执行" : "需要处理");
      });
      return nextPlan;
    }, "迁移预览失败");
  }

  async function execute() {
    if (!options.enabled() || executeInProgress() || !plan()?.canExecute) {
      return;
    }

    const confirmed = await options.confirmDialog({
      title: "执行迁移",
      message: "迁移会复制目录、保留原数据备份，并在原位置创建目录链接。请先关闭相关软件。",
      tone: "warning"
    });
    if (!confirmed || !options.enabled() || executeInProgress() || !plan()?.canExecute) {
      return;
    }

    const request = buildRequest(true);
    const operation = await submitOperationTask({
      key: migrationExecuteSubmitTaskKey,
      title: migrationTaskTitle("提交迁移", request.softwareName),
      durability: "transient",
      visibility: "diagnostics",
      concurrency: "coalesce"
    }, (signal) => ({ ...migrationExecuteCommand(request), signal }), "迁移操作启动失败");
    if (!operation) {
      return;
    }
    await reportOperationFailure(async () => {
      await requireOperationSuccess(operation);
      setStatus("迁移完成");
      options.showToast({ tone: "success", title: uiText.feedback.success, message: "迁移完成" });
      await Promise.all([refreshRecords(), options.refreshSoftware()]);
    }, "迁移操作失败");
  }

  function buildRequest(confirmExecution: boolean) {
    return {
      softwareName: softwareName().trim(),
      targetCategory: targetCategory(),
      migrationKind: kind(),
      sourcePaths: sourcePaths().split(/\r?\n/).map((line) => line.trim()).filter(Boolean),
      allowMediumRisk: allowMediumRisk(),
      confirmExecution
    };
  }

  async function findCandidates() {
    if (!options.enabled()) {
      setCandidates([]);
      return;
    }

    const name = softwareName().trim();
    await runFrontendTask({
      key: migrationCandidateLookupTaskKey,
      title: migrationTaskTitle("查找可迁移内容", name),
      durability: "transient",
      visibility: "user",
      concurrency: "replace"
    }, async (context) => {
      const loaded = await postJson<MigrationCandidate[]>(
        "/api/migrations/discovery/candidates",
        { softwareName: name },
        "查找可迁移内容失败",
        false,
        { signal: context.signal });
      context.commit(() => setCandidates(loaded));
      return loaded;
    }, "查找可迁移内容失败");
  }

  async function startSession() {
    if (!options.enabled() || discoveryStartInProgress() || activeSessionId()) {
      return;
    }

    const request = {
      softwareName: softwareName().trim(),
      programRootPaths: discoveryProgramRootPaths().split(/\r?\n/).map((item) => item.trim()).filter(Boolean),
      processNames: discoveryProcessNames().split(/[,\s]+/).map((item) => item.trim()).filter(Boolean)
    };
    const operation = await submitOperationTask({
      key: migrationDiscoveryStartSubmitTaskKey,
      title: migrationTaskTitle("开始迁移内容监控", request.softwareName),
      durability: "transient",
      visibility: "diagnostics",
      concurrency: "coalesce"
    }, (signal) => ({ ...migrationDiscoveryStartCommand(request), signal }), "发现操作启动失败");
    if (!operation) {
      return;
    }
    if (isTerminalOperation(operation)) {
      await reportOperationFailure(async () => {
        requireSucceededOperation(operation);
      }, "发现操作启动失败");
      return;
    }
    await refreshSessions();
  }

  async function stopSession() {
    if (!options.enabled()) {
      return;
    }

    const id = activeSessionId();
    if (!id) {
      return;
    }

    if (discoveryStopInProgress()) {
      return;
    }
    const operation = await runFrontendTask({
      key: migrationDiscoveryStopTaskKey(id),
      title: "停止迁移内容监控",
      durability: "transient",
      visibility: "diagnostics",
      concurrency: "coalesce"
    }, ({ signal }) => options.operations.cancel(id, signal), "停止监控失败");
    if (!operation) {
      return;
    }
    await reportOperationFailure(async () => {
      await requireOperationTerminal(operation);
      await refreshSessions();
    }, "停止监控失败");
  }

  function useCandidate(candidate: MigrationCandidate) {
    const path = candidate.path ?? candidate.directory ?? "";
    const existing = sourcePaths().split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    if (path && !existing.some((item) => item.toLowerCase() === path.toLowerCase())) {
      existing.push(path);
    }

    setSourcePaths(existing.join("\n"));
    setKindSignal(candidate.recommendedMigrationKind ?? "Data");
    setTargetCategory(candidate.recommendedTargetCategory ?? "UserData");
    resetPlan();
  }

  async function migrateCandidate(candidate: MigrationCandidate) {
    useCandidate(candidate);
    await preview();
  }

  async function restore(record: SoftwareDataMigrationRecord) {
    if (!options.enabled() || isRestoreInProgress(record.id)) {
      return;
    }

    const confirmed = await options.confirmDialog({
      title: "恢复迁移",
      message: "恢复会移除原位置的目录链接，并把当前数据复制回原位置。",
      tone: "warning"
    });
    if (!confirmed || !options.enabled() || isRestoreInProgress(record.id)) {
      return;
    }
    const operation = await submitOperationTask({
      key: migrationRestoreSubmitTaskKey(record.id),
      title: migrationTaskTitle("提交迁移恢复", record.softwareName),
      durability: "transient",
      visibility: "diagnostics",
      concurrency: "coalesce"
    }, (signal) => ({ ...migrationRestoreCommand(record.id, true), signal }), "恢复操作启动失败");
    if (!operation) {
      return;
    }
    await reportOperationFailure(async () => {
      const completed = await requireOperationSuccess(operation);
      options.showToast({
        tone: "success",
        title: uiText.feedback.success,
        message: userFacingMessage(operationResultText(completed), "恢复完成")
      });
      await refreshRecords();
    }, "恢复迁移失败");
  }

  function fillDataMigration(nextSoftwareName: string, nextSourcePaths: string[] = []) {
    setSoftwareName(nextSoftwareName);
    setSourcePaths(nextSourcePaths.join("\n"));
    setKindSignal("Data");
    setTargetCategory("UserData");
    resetPlan();
  }

  function fillRootMigration(nextSoftwareName: string, nextSourcePaths: string[]) {
    setSoftwareName(nextSoftwareName);
    setSourcePaths(nextSourcePaths.join("\n"));
    setKindSignal("Root");
    setTargetCategory("Misc");
    setAllowMediumRisk(true);
    resetPlan();
  }

  return {
    roots,
    rootsObservation,
    records,
    recordsObservation,
    sessions,
    sessionsObservation,
    candidates,
    activeSessionId,
    softwareName,
    kind,
    targetCategory,
    sourcePaths,
    discoveryProgramRootPaths,
    discoveryProcessNames,
    allowMediumRisk,
    plan,
    status,
    workbenchAvailable,
    operationActionsAvailable,
    previewInProgress,
    candidateLookupInProgress,
    executeInProgress,
    discoveryStartInProgress,
    discoveryStopInProgress,
    isRestoreInProgress,
    setSoftwareName,
    setKind,
    setTargetCategory,
    setSourcePaths,
    setDiscoveryProgramRootPaths,
    setDiscoveryProcessNames,
    setAllowMediumRisk,
    setCandidates,
    resetPlan,
    refreshRoots,
    refreshRecords,
    refreshSessions,
    preview,
    execute,
    findCandidates,
    startSession,
    stopSession,
    useCandidate,
    migrateCandidate,
    restore,
    fillDataMigration,
    fillRootMigration,
    dispose: () => {
      taskScope.close(new Error("Migration workbench disposed."));
      unsubscribeTasks();
      unsubscribeOperations();
    }
  };

  function taskActive(taskKey: string) {
    taskRevision();
    return options.tasks.listSnapshots().some((snapshot) =>
      snapshot.descriptor.scopeKey === taskScope.key
      && snapshot.descriptor.key === taskKey
      && !isTerminalTaskStatus(snapshot.status));
  }

  function hasActiveOperation(operationKind: string, domainKey?: string) {
    return selectOperations(operationProjection(), {
      kind: operationKind,
      domainKey,
      active: true
    }).length > 0;
  }

  function activeDiscoveryOperation() {
    return selectOperations(operationProjection(), {
      kind: "discovery.start",
      active: true
    })[0] ?? null;
  }

  function isRestoreInProgress(recordId: string) {
    return taskActive(migrationRestoreSubmitTaskKey(recordId).value)
      || hasActiveOperation("migration.restore", `migration-restore:${recordId}`);
  }

  async function runFrontendTask<T>(
    descriptor: ScopedTaskDescriptor<T>,
    executor: (context: TaskExecutionContext) => Promise<T>,
    fallbackMessage: string
  ): Promise<T | undefined> {
    try {
      const outcome = await taskScope.run(descriptor, executor).completion;
      if (outcome.status === "succeeded") {
        return outcome.value;
      }
      if (outcome.status === "failed") {
        showFailure(outcome.error, fallbackMessage);
      }
    } catch (error) {
      showFailure(error, fallbackMessage);
    }
    return undefined;
  }

  async function submitOperationTask(
    descriptor: ScopedTaskDescriptor<OperationSnapshot>,
    command: (signal: AbortSignal) => ReturnType<typeof migrationExecuteCommand>,
    fallbackMessage: string
  ) {
    return runFrontendTask(
      descriptor,
      ({ signal }) => options.operations.submit(command(signal)),
      fallbackMessage);
  }

  async function reportOperationFailure(action: () => Promise<void>, fallbackMessage: string) {
    try {
      await action();
    } catch (error) {
      showFailure(error, fallbackMessage);
    }
  }

  function showFailure(error: unknown, fallbackMessage: string) {
    options.showToast({
      tone: "error",
      title: uiText.feedback.error,
      message: userFacingErrorMessage(error, fallbackMessage)
    });
  }

  async function requireOperationSuccess(operation: OperationSnapshot) {
    return requireSucceededOperation(await requireOperationTerminal(operation));
  }

  async function requireOperationTerminal(operation: OperationSnapshot) {
    return isTerminalOperation(operation)
      ? operation
      : options.operations.waitForTerminal(operation.id);
  }
}

const migrationPreviewTaskKey = defineTaskKey<MigrationPlan>("migration.preview");
const migrationCandidateLookupTaskKey = defineTaskKey<MigrationCandidate[]>(
  "migration.discovery.candidates");
const migrationExecuteSubmitTaskKey = defineTaskKey<OperationSnapshot>(
  "migration.execute.submit");
const migrationDiscoveryStartSubmitTaskKey = defineTaskKey<OperationSnapshot>(
  "migration.discovery.start.submit");
const migrationDiscoveryStopTaskKeys = new Map<string, ReturnType<typeof defineTaskKey<OperationSnapshot>>>();
const migrationRestoreSubmitTaskKeys = new Map<string, ReturnType<typeof defineTaskKey<OperationSnapshot>>>();

function migrationDiscoveryStopTaskKey(operationId: string) {
  return cachedOperationTaskKey(
    migrationDiscoveryStopTaskKeys,
    operationId,
    "migration.discovery.stop");
}

function migrationRestoreSubmitTaskKey(recordId: string) {
  return cachedOperationTaskKey(
    migrationRestoreSubmitTaskKeys,
    recordId,
    "migration.restore.submit");
}

function cachedOperationTaskKey(
  cache: Map<string, ReturnType<typeof defineTaskKey<OperationSnapshot>>>,
  identity: string,
  prefix: string
) {
  const normalized = identity.trim();
  let key = cache.get(normalized);
  if (!key) {
    key = defineTaskKey<OperationSnapshot>(`${prefix}:${normalized}`);
    cache.set(normalized, key);
  }
  return key;
}

function migrationTaskTitle(action: string, softwareName: string | null | undefined) {
  const name = softwareName?.trim();
  return name ? `${action}：${name}` : action;
}

function requireSucceededOperation(operation: OperationSnapshot) {
  if (operation.state === "succeeded") {
    return operation;
  }

  throw new Error(operation.error
    ?? (operation.state === "stateUncertain"
      ? "操作结果不确定，请先检查实际状态。"
      : "操作未能完成。"));
}

function operationResultText(operation: OperationSnapshot) {
  return typeof operation.result === "string" ? operation.result : null;
}
