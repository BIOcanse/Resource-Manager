import { Accessor, createMemo, createSignal, Setter } from "solid-js";
import {
  addManualSoftware,
  getComponents,
  getSoftware,
} from "../api";
import { getBrowserRuntimeSnapshot } from "../browserRuntimes/browserRuntimeApi";
import {
  sharedBrowserRuntimeComponentId,
  type BrowserRuntimeSnapshot
} from "../browserRuntimes/browserRuntimeTypes";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import { managementActionKey, managementActionKeyFromOperation } from "../components/ManagementPage";
import type { ManagementSubpageId } from "../management/managementNavigation";
import { readManagementSnapshotCache, writeManagementSnapshotCache } from "../management/managementSnapshotCache";
import { uiText } from "../text";
import {
  cachedObservation,
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { userFacingErrorMessage, userFacingMessage } from "../presentation/userFacingText";
import {
  compareUInt64Decimal,
  formatUInt64Bytes
} from "../data/operations/uint64Decimal";
import {
  componentInstallCommand,
  softwareUninstallCommand
} from "../data/operations/operationCommands";
import type {
  OperationRegistry,
  OperationRegistrySnapshot
} from "../frontendRuntime/operations/OperationRegistry";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import type {
  ManagedComponent,
  ManagementKind,
  ManualSoftwareKind,
  ManualSoftwareRequest,
  OperationSnapshot,
  SoftwareRecord
} from "../types";
import { textOrEmpty } from "../utils";

export interface ManagementStoreOptions {
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  mutablePersistenceEnabled: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  operations: Pick<
    OperationRegistry,
    "snapshot" | "subscribe" | "submit" | "waitForTerminal"
  >;
}

export interface ManagementStore {
  activeSubpage: Accessor<ManagementSubpageId>;
  setActiveSubpage: Setter<ManagementSubpageId>;
  components: Accessor<ManagedComponent[]>;
  componentsObservation: Accessor<ObservationState>;
  software: Accessor<SoftwareRecord[]>;
  softwareObservation: Accessor<ObservationState>;
  browserRuntimes: Accessor<BrowserRuntimeSnapshot | null>;
  browserRuntimesObservation: Accessor<ObservationState>;
  operationsObservation: Accessor<ObservationState>;
  browserRuntimeRefreshInProgress: Accessor<boolean>;
  refreshInProgress: Accessor<boolean>;
  actionLabels: Accessor<Record<string, string>>;
  manualSoftwareKind: Accessor<ManualSoftwareKind | null>;
  manualSoftwareActionInProgress: Accessor<boolean>;
  openManualSoftwareModal: (kind: ManagementKind) => void;
  closeManualSoftwareModal: () => void;
  submitManualSoftware: (request: ManualSoftwareRequest) => Promise<void>;
  refreshComponents: () => Promise<void>;
  refreshSoftware: (refreshPortableRegistrations?: boolean) => Promise<void>;
  refreshBrowserRuntimes: (forceRefresh?: boolean) => Promise<void>;
  refreshState: (userInitiated: boolean) => Promise<void>;
  installComponent: (component: ManagedComponent) => Promise<void>;
  uninstallSoftware: (software: SoftwareRecord, actionKey?: string) => Promise<void>;
  dispose: () => void;
}

export function createManagementStore(options: ManagementStoreOptions): ManagementStore {
  const cachedSnapshot = readManagementSnapshotCache();
  const [activeSubpage, setActiveSubpage] = createSignal<ManagementSubpageId>("Dependency");
  const [components, setComponents] = createSignal<ManagedComponent[]>(cachedSnapshot?.components ?? []);
  const [software, setSoftware] = createSignal<SoftwareRecord[]>(cachedSnapshot?.software ?? []);
  const cachedState = cachedSnapshot
    ? cachedObservation(cachedSnapshot.capturedAt)
    : loadingObservation();
  const [componentsObservation, setComponentsObservation] =
    createSignal<ObservationState>(cachedState);
  const [softwareObservation, setSoftwareObservation] =
    createSignal<ObservationState>(options.mutablePersistenceEnabled()
      ? cachedState
      : profileDisabledObservation(
        "当前启动配置不加载可写软件登记。"));
  const [browserRuntimes, setBrowserRuntimes] = createSignal<BrowserRuntimeSnapshot | null>(null);
  const [browserRuntimesObservation, setBrowserRuntimesObservation] =
    createSignal<ObservationState>(loadingObservation());
  const [browserRuntimeRefreshInProgress, setBrowserRuntimeRefreshInProgress] = createSignal(false);
  const [refreshInProgress, setRefreshInProgress] = createSignal(false);
  const [temporaryActionLabels, setTemporaryActionLabels] =
    createSignal<Record<string, string>>({});
  const [operationProjection, setOperationProjection] =
    createSignal<OperationRegistrySnapshot>(options.operations.snapshot);
  const [manualSoftwareKind, setManualSoftwareKind] = createSignal<ManualSoftwareKind | null>(null);
  const [manualSoftwareActionInProgress, setManualSoftwareActionInProgress] = createSignal(false);
  const operationsObservation = createMemo<ObservationState>(() =>
    operationObservation(
      operationProjection(),
      options.runtimeEffectsEnabled()));
  const actionLabels = createMemo<Record<string, string>>(() => {
    if (!options.runtimeEffectsEnabled()) {
      return {};
    }
    const labels = { ...temporaryActionLabels() };
    for (const operation of operationProjection().operations) {
      if (isTerminalOperation(operation)) {
        continue;
      }
      const key = managementActionKeyFromOperation(operation);
      if (key) {
        labels[key] = labelForOperation(operation);
      }
    }
    return labels;
  });
  const unsubscribeOperations = options.operations.subscribe(setOperationProjection);

  function openManualSoftwareModal(kind: ManagementKind) {
    if (!options.mutablePersistenceEnabled() || !isManualSoftwareKind(kind)) {
      return;
    }

    setManualSoftwareKind(kind);
    if (software().length === 0) {
      void refreshSoftware();
    }
  }

  function closeManualSoftwareModal() {
    setManualSoftwareKind(null);
  }

  async function submitManualSoftware(request: ManualSoftwareRequest) {
    if (!options.mutablePersistenceEnabled() || manualSoftwareActionInProgress()) {
      return;
    }

    setManualSoftwareActionInProgress(true);
    try {
      await addManualSoftware(request);
      setManualSoftwareKind(null);
      await refreshSoftware();
    } catch (error) {
      showErrorToast(error, "添加软件失败");
    } finally {
      setManualSoftwareActionInProgress(false);
    }
  }

  async function refreshComponents() {
    setComponentsObservation(refreshingObservation);
    try {
      const next = await getComponents();
      setComponents(next);
      writeManagementSnapshotCache(next, software());
      setComponentsObservation(readyObservation());
    } catch (error) {
      setComponentsObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, "组件目录刷新失败")));
    }
  }

  async function refreshSoftware(refreshPortableRegistrations = false) {
    if (!options.mutablePersistenceEnabled()) {
      setSoftwareObservation(profileDisabledObservation(
        "当前启动配置不加载可写软件登记。"));
      return;
    }

    setSoftwareObservation(refreshingObservation);
    try {
      const next = await getSoftware(refreshPortableRegistrations);
      setSoftware(next);
      writeManagementSnapshotCache(components(), next);
      setSoftwareObservation(readyObservation());
    } catch (error) {
      setSoftwareObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, "软件登记刷新失败")));
      if (refreshPortableRegistrations) {
        throw error;
      }
    }
  }

  async function refreshBrowserRuntimes(forceRefresh = false) {
    if (browserRuntimeRefreshInProgress()) {
      return;
    }

    setBrowserRuntimeRefreshInProgress(true);
    setBrowserRuntimesObservation(refreshingObservation);
    try {
      const next = await getBrowserRuntimeSnapshot(forceRefresh);
      setBrowserRuntimes(next);
      setBrowserRuntimesObservation(readyObservation(next.capturedAt));
    } catch (error) {
      setBrowserRuntimesObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, "浏览器运行时状态刷新失败")));
    } finally {
      setBrowserRuntimeRefreshInProgress(false);
    }
  }

  async function refreshState(userInitiated: boolean) {
    if (refreshInProgress()) {
      return;
    }

    setRefreshInProgress(true);
    try {
      const refreshes: Promise<void>[] = [refreshComponents()];
      if (options.mutablePersistenceEnabled()) {
        refreshes.push(refreshSoftware(userInitiated));
      } else {
        setSoftwareObservation(profileDisabledObservation(
          "当前启动配置不加载可写软件登记。"));
      }
      await Promise.all(refreshes);
    } catch (error) {
      if (userInitiated) {
        showErrorToast(error, "刷新失败");
      }
    } finally {
      setRefreshInProgress(false);
    }
  }

  async function installComponent(component: ManagedComponent) {
    if (!options.runtimeEffectsEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: "当前启动配置只提供组件状态查看，不执行安装操作。"
      });
      return;
    }

    if (component.definition?.requiresExternalTermsAcknowledgement && !await options.confirmDialog({
      title: textOrEmpty(component.definition.name) || uiText.feedback.confirmTitle,
      message: `${textOrEmpty(component.definition.name) || "该组件"} 需要接受外部厂商条款后继续。`,
      tone: "warning"
    })) {
      return;
    }

    const id = component.definition?.id;
    const key = managementActionKey("component", id);
    try {
      if (component.canInstall || component.installerAvailable || component.canDownload) {
        if (!id) {
          throw new Error("组件缺少稳定标识，无法创建安装操作。");
        }
        setActionLabel(key, "等待中");
        const operation = await options.operations.submit(
          componentInstallCommand(id, true));
        setActionLabel(key, labelForOperation(operation));
        const completed = await options.operations.waitForTerminal(operation.id);
        showOperationResult(completed);
        return;
      }

      if (component.definition?.sourcePageUrl) {
        setActionLabel(key, "正在安装");
        window.open(component.definition.sourcePageUrl, "_blank", "noopener");
        return;
      }

      throw new Error("该组件当前没有可用安装入口。");
    } catch (error) {
      showErrorToast(error, "操作失败");
    } finally {
      clearActionLabel(key);
      const refreshes = [refreshComponents()];
      if (options.mutablePersistenceEnabled()) {
        refreshes.push(refreshSoftware());
      }
      if (id === sharedBrowserRuntimeComponentId) {
        refreshes.push(refreshBrowserRuntimes(true));
      }
      await Promise.all(refreshes);
    }
  }

  async function uninstallSoftware(softwareRecord: SoftwareRecord, actionKey = managementActionKey("software", softwareRecord.id)) {
    if (!options.runtimeEffectsEnabled() || !options.mutablePersistenceEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: "当前启动配置不执行软件卸载操作。"
      });
      return;
    }

    const operations = softwareRecord.operations ?? {};
    if (!operations.canUninstall) {
      options.showToast({ tone: "warning", title: uiText.feedback.warning, message: userFacingMessage(operations.uninstallMessage, "该软件当前不可卸载") });
      return;
    }

    const actionText = operations.uninstallKind === "WindowsUninstaller"
      ? "将打开该软件的卸载程序。未登记的软件目录不会被删除。"
      : "将删除该软件目录中的文件。此操作无法撤销。";
    if (!await options.confirmDialog({
      title: softwareRecord.name,
      message: actionText,
      tone: "warning"
    })) {
      return;
    }

    setActionLabel(actionKey, "正在卸载");
    try {
      const operation = await options.operations.submit(
        softwareUninstallCommand(softwareRecord.id, true));
      setActionLabel(actionKey, labelForOperation(operation));
      const completed = await options.operations.waitForTerminal(operation.id);
      showOperationResult(completed);
    } catch (error) {
      showErrorToast(error, "软件操作失败");
    } finally {
      clearActionLabel(actionKey);
      await Promise.all([refreshComponents(), refreshSoftware()]);
    }
  }

  function setActionLabel(key: string, label: string) {
    setTemporaryActionLabels((current) => ({ ...current, [key]: label }));
  }

  function clearActionLabel(key: string) {
    setTemporaryActionLabels((current) => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  function showErrorToast(error: unknown, fallback: string) {
    options.showToast({
      tone: "error",
      title: uiText.feedback.error,
      message: userFacingErrorMessage(error, fallback)
    });
  }

  function showOperationResult(operation: OperationSnapshot) {
    if (operation.state === "succeeded") {
      options.showToast({
        tone: "success",
        title: uiText.feedback.success,
        message: userFacingMessage(operationResultText(operation), "操作完成")
      });
      return;
    }

    if (operation.state === "stateUncertain") {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: userFacingMessage(operation.error, "操作结果不确定，请检查实际状态后再重试。")
      });
      return;
    }

    if (operation.state === "failed") {
      options.showToast({
        tone: "error",
        title: uiText.feedback.error,
        message: userFacingMessage(operation.error, "操作未能完成，请稍后重试。")
      });
    }
  }

  return {
    activeSubpage,
    setActiveSubpage,
    components,
    componentsObservation,
    software,
    softwareObservation,
    browserRuntimes,
    browserRuntimesObservation,
    operationsObservation,
    browserRuntimeRefreshInProgress,
    refreshInProgress,
    actionLabels,
    manualSoftwareKind,
    manualSoftwareActionInProgress,
    openManualSoftwareModal,
    closeManualSoftwareModal,
    submitManualSoftware,
    refreshComponents,
    refreshSoftware,
    refreshBrowserRuntimes,
    refreshState,
    installComponent,
    uninstallSoftware,
    dispose: unsubscribeOperations
  };
}

function isManualSoftwareKind(kind: ManagementKind): kind is ManualSoftwareKind {
  return kind === "Adapted" || kind === "Game" || kind === "HighPerformance" || kind === "Other";
}

function labelForOperation(operation: OperationSnapshot) {
  const stage = operation.progress?.stage ?? "";
  if (operation.state === "queued" || operation.state === "startPending") {
    return "等待中";
  }
  if (operation.state === "running") {
    const percent = typeof operation.progress?.percent === "number"
      ? ` ${operation.progress.percent.toFixed(0)}%`
      : "";
    const speedValue = operation.progress?.speedBytesPerSecond ?? null;
    const speed = speedValue !== null
      && compareUInt64Decimal(speedValue, "0") > 0
      ? ` ${formatUInt64Bytes(speedValue)}/s`
      : "";
    return `${stage || "进行中"}${percent}${speed}`;
  }
  if (operation.state === "cancelPending") {
    return "正在取消";
  }
  if (operation.state === "retryWait") {
    return "等待重试";
  }
  if (operation.state === "recoveryPending") {
    return "正在恢复";
  }
  if (operation.state === "succeeded") {
    return "已完成";
  }
  if (operation.state === "failed") {
    return "失败";
  }
  if (operation.state === "canceled") {
    return "已取消";
  }
  if (operation.state === "stateUncertain") {
    return "状态不确定";
  }

  return stage || "处理中";
}

function operationResultText(operation: OperationSnapshot) {
  return typeof operation.result === "string" ? operation.result : null;
}

function operationObservation(
  snapshot: OperationRegistrySnapshot,
  enabled: boolean
): ObservationState {
  if (!enabled) {
    return profileDisabledObservation(
      "当前启动配置不加载可执行操作状态。");
  }
  if (snapshot.status === "ready") {
    return readyObservation(snapshot.capturedAt ?? undefined);
  }
  if (snapshot.status === "disposed") {
    return failedObservation(
      loadingObservation(),
      "操作状态已经停止");
  }
  return loadingObservation();
}
