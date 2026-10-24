import { Accessor, createMemo, createSignal, Setter } from "solid-js";
import {
  addManualSoftware,
  fetchComponentVersionOptions,
  getComponents,
  getSoftware,
  openPath,
  verifyComponent as requestComponentVerification,
} from "../api";
import { getBrowserRuntimeSnapshot } from "../features/browserRuntimes/browserRuntimeApi";
import {
  sharedBrowserRuntimeComponentId,
  type BrowserRuntimeSnapshot
} from "../features/browserRuntimes/browserRuntimeTypes";
import type { ComponentAcquisitionRequest } from "../features/management/components/ComponentAcquisitionDialog";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import { managementActionKey, managementActionKeyFromOperation } from "../features/management/components/ManagementPage";
import {
  browserRuntimeManagementSubpageId,
  type ManagementSubpageId
} from "../features/management/managementNavigation";
import { readManagementSnapshotCache, writeManagementSnapshotCache } from "../features/management/managementSnapshotCache";
import { uiText } from "../text.ts";
import {
  cachedObservation,
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { renderBackendMessage } from "../presentation/backendMessage.ts";
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
import type { CurrentValueSource } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import type { ManagementInventoryRevisionSnapshot } from "../data/managementInventoryRevision.ts";
import { bindManagementInventoryRevisions } from "./managementInventoryRevisionBinding.ts";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import type {
  ManagedComponent,
  ManagementKind,
  ManualSoftwareKind,
  ManualSoftwareRequest,
  OperationSnapshot,
  SoftwareRecord
} from "../types";
import type { PageId } from "../types";
import { textOrEmpty } from "../utils";

export interface ManagementStoreOptions {
  activePage: Accessor<PageId>;
  inventoryRevisionSource: CurrentValueSource<ManagementInventoryRevisionSnapshot>;
  inventoryActive: Accessor<boolean>;
  browserRuntimesActive: Accessor<boolean>;
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
  manualSoftwareSource: Accessor<SoftwareRecord | null>;
  classifySoftware: (software: SoftwareRecord) => void;
  manualSoftwareActionInProgress: Accessor<boolean>;
  openManualSoftwareModal: (kind: ManagementKind) => void;
  closeManualSoftwareModal: () => void;
  submitManualSoftware: (request: ManualSoftwareRequest) => Promise<void>;
  refreshComponents: () => Promise<void>;
  refreshSoftware: (refreshPortableRegistrations?: boolean) => Promise<void>;
  refreshBrowserRuntimes: (forceRefresh?: boolean) => Promise<void>;
  refreshState: (userInitiated: boolean) => Promise<void>;
  installComponent: (component: ManagedComponent) => Promise<void>;
  verifyComponent: (component: ManagedComponent) => Promise<void>;
  /** 当前打开的组件获取对话框（条款 + 版本选择）；null 表示没有打开。 */
  acquisitionRequest: Accessor<ComponentAcquisitionRequest | null>;
  cancelAcquisition: () => void;
  confirmAcquisition: (versionChoice: string | null) => Promise<void>;
  openAcquisitionLink: (url: string) => Promise<void>;
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
        uiText.stores.writableRegistryDisabled));
  const [browserRuntimes, setBrowserRuntimes] = createSignal<BrowserRuntimeSnapshot | null>(null);
  const [browserRuntimesObservation, setBrowserRuntimesObservation] =
    createSignal<ObservationState>(loadingObservation());
  const [browserRuntimeRefreshInProgress, setBrowserRuntimeRefreshInProgress] = createSignal(false);
  const [refreshInProgress, setRefreshInProgress] = createSignal(false);
  const [temporaryActionLabels, setTemporaryActionLabels] =
    createSignal<Record<string, string>>({});
  const [operationProjection, setOperationProjection] =
    createSignal<OperationRegistrySnapshot>(options.operations.snapshot);
  const [manualSoftwareSource, setManualSoftwareSource] = createSignal<SoftwareRecord | null>(null);
  const [manualSoftwareKind, setManualSoftwareKind] = createSignal<ManualSoftwareKind | null>(null);
  const [manualSoftwareActionInProgress, setManualSoftwareActionInProgress] = createSignal(false);
  let componentRefreshPromise: Promise<void> | null = null;
  let componentRefreshPending: { readonly visibleOnly: boolean } | null = null;
  let softwareRefreshPromise: Promise<void> | null = null;
  let softwareRefreshPending: {
    readonly visibleOnly: boolean;
    readonly portable: boolean;
  } | null = null;
  let browserRuntimeRefreshPromise: Promise<void> | null = null;
  let browserRuntimeRefreshPending: {
    readonly visibleOnly: boolean;
    readonly force: boolean;
  } | null = null;
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

  bindManagementInventoryRevisions({
    activePage: options.activePage,
    activeSubpage,
    inventoryRevisionSource: options.inventoryRevisionSource,
    inventoryActive: options.inventoryActive,
    browserRuntimesActive: options.browserRuntimesActive,
    mutablePersistenceEnabled: options.mutablePersistenceEnabled,
    refreshComponents: refreshComponentsForRevision,
    refreshSoftware: () => requestSoftwareRefresh(false, true),
    refreshBrowserRuntimes: () => requestBrowserRuntimeRefresh(false, true)
  });

  function openManualSoftwareModal(kind: ManagementKind) {
    if (!options.mutablePersistenceEnabled() || !isManualSoftwareKind(kind)) {
      return;
    }

    setManualSoftwareSource(null);
    setManualSoftwareKind(kind);
    if (software().length === 0) {
      void refreshSoftware();
    }
  }

  function classifySoftware(software: SoftwareRecord) {
    if (!options.mutablePersistenceEnabled()) return;
    setManualSoftwareSource(software);
    setManualSoftwareKind("other");
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
      showErrorToast(error, uiText.stores.addSoftwareFailed);
    } finally {
      setManualSoftwareActionInProgress(false);
    }
  }

  function refreshComponents(): Promise<void> {
    return requestComponentRefresh(false);
  }

  function refreshComponentsForRevision(): Promise<void> {
    return requestComponentRefresh(true);
  }

  function requestComponentRefresh(visibleOnly: boolean): Promise<void> {
    if (componentRefreshPromise) {
      componentRefreshPending = {
        visibleOnly: (componentRefreshPending?.visibleOnly ?? true) && visibleOnly
      };
      return componentRefreshPromise;
    }

    let tracked: Promise<void>;
    componentRefreshPending = null;
    tracked = Promise.resolve().then(async () => {
      try {
        await refreshComponentsCore();
        while (componentRefreshPending) {
          const pending = componentRefreshPending;
          componentRefreshPending = null;
          if (pending.visibleOnly && !componentListIsDemanded()) {
            continue;
          }
          await refreshComponentsCore();
        }
      } finally {
        if (componentRefreshPromise === tracked) {
          componentRefreshPromise = null;
        }
      }
    });
    componentRefreshPromise = tracked;
    return tracked;
  }

  function componentListIsDemanded(): boolean {
    const subpage = activeSubpage();
    return options.activePage() === "components"
      && options.inventoryActive()
      && (subpage === "Dependency" || subpage === "Support");
  }

  async function refreshComponentsCore() {
    setComponentsObservation(refreshingObservation);
    try {
      const next = await getComponents();
      setComponents(next);
      writeManagementSnapshotCache(next, software());
      setComponentsObservation(readyObservation());
    } catch (error) {
      setComponentsObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, uiText.stores.componentCatalogRefreshFailed)));
    }
  }

  function refreshSoftware(refreshPortableRegistrations = false): Promise<void> {
    return requestSoftwareRefresh(refreshPortableRegistrations, false);
  }

  function requestSoftwareRefresh(
    refreshPortableRegistrations: boolean,
    visibleOnly: boolean
  ): Promise<void> {
    if (softwareRefreshPromise) {
      softwareRefreshPending = {
        visibleOnly: (softwareRefreshPending?.visibleOnly ?? true) && visibleOnly,
        portable: (softwareRefreshPending?.portable ?? false) || refreshPortableRegistrations
      };
      return softwareRefreshPromise;
    }

    softwareRefreshPending = null;
    let tracked: Promise<void>;
    tracked = Promise.resolve().then(async () => {
      let firstError: unknown;
      let hasError = false;
      const refreshAndCaptureError = async (portable: boolean) => {
        try {
          await refreshSoftwareCore(portable);
        } catch (error) {
          if (!hasError) {
            firstError = error;
            hasError = true;
          }
        }
      };

      try {
        await refreshAndCaptureError(refreshPortableRegistrations);
        while (softwareRefreshPending) {
          const pending = softwareRefreshPending;
          softwareRefreshPending = null;
          if (pending.visibleOnly && !softwareListIsDemanded()) {
            continue;
          }
          await refreshAndCaptureError(pending.portable);
        }
        if (hasError) {
          throw firstError;
        }
      } finally {
        if (softwareRefreshPromise === tracked) {
          softwareRefreshPromise = null;
        }
      }
    });
    softwareRefreshPromise = tracked;
    return tracked;
  }

  function softwareListIsDemanded(): boolean {
    return options.activePage() === "components"
      && options.inventoryActive()
      && activeSubpage() !== browserRuntimeManagementSubpageId
      && options.mutablePersistenceEnabled();
  }

  async function refreshSoftwareCore(refreshPortableRegistrations: boolean) {
    if (!options.mutablePersistenceEnabled()) {
      setSoftwareObservation(profileDisabledObservation(
        uiText.stores.writableRegistryDisabled));
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
        userFacingErrorMessage(error, uiText.stores.softwareRegistryRefreshFailed)));
      if (refreshPortableRegistrations) {
        throw error;
      }
    }
  }

  function refreshBrowserRuntimes(forceRefresh = false): Promise<void> {
    return requestBrowserRuntimeRefresh(forceRefresh, false);
  }

  function requestBrowserRuntimeRefresh(
    forceRefresh: boolean,
    visibleOnly: boolean
  ): Promise<void> {
    if (browserRuntimeRefreshPromise) {
      browserRuntimeRefreshPending = {
        visibleOnly: (browserRuntimeRefreshPending?.visibleOnly ?? true) && visibleOnly,
        force: (browserRuntimeRefreshPending?.force ?? false) || forceRefresh
      };
      return browserRuntimeRefreshPromise;
    }

    browserRuntimeRefreshPending = null;
    let tracked: Promise<void>;
    tracked = Promise.resolve().then(async () => {
      setBrowserRuntimeRefreshInProgress(true);
      try {
        let nextForceRefresh = forceRefresh;
        while (true) {
          setBrowserRuntimesObservation(refreshingObservation);
          try {
            const next = await getBrowserRuntimeSnapshot(nextForceRefresh);
            setBrowserRuntimes(next);
            setBrowserRuntimesObservation(readyObservation(next.capturedAt));
          } catch (error) {
            setBrowserRuntimesObservation((previous) => failedObservation(
              previous,
              userFacingErrorMessage(error, uiText.stores.browserRuntimeRefreshFailed)));
          }

          const pending = browserRuntimeRefreshPending;
          browserRuntimeRefreshPending = null;
          if (!pending || (pending.visibleOnly && !browserRuntimeListIsDemanded())) {
            break;
          }
          nextForceRefresh = pending.force;
        }
      } finally {
        setBrowserRuntimeRefreshInProgress(false);
        if (browserRuntimeRefreshPromise === tracked) {
          browserRuntimeRefreshPromise = null;
        }
      }
    });
    browserRuntimeRefreshPromise = tracked;
    return tracked;
  }

  function browserRuntimeListIsDemanded(): boolean {
    return options.activePage() === "components"
      && options.browserRuntimesActive()
      && activeSubpage() === browserRuntimeManagementSubpageId;
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
          uiText.stores.writableRegistryDisabled));
      }
      await Promise.all(refreshes);
    } catch (error) {
      if (userInitiated) {
        showErrorToast(error, uiText.stores.refreshFailed);
      }
    } finally {
      setRefreshInProgress(false);
    }
  }

  // 组件获取：条款确认与版本选择由对话框一次收齐，之后才发命令。
  // 对话框里的"同意"是这次操作的一次性授权，不做持久化。
  const [acquisitionRequest, setAcquisitionRequest] =
    createSignal<ComponentAcquisitionRequest | null>(null);

  function componentSourceKind(component: ManagedComponent) {
    return component.installerSourceKind ?? "manual";
  }

  function cancelAcquisition() {
    setAcquisitionRequest(null);
  }

  async function openAcquisitionLink(url: string) {
    window.open(url, "_blank", "noopener");
  }

  async function installComponent(component: ManagedComponent) {
    if (!options.runtimeEffectsEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.stores.installDisabled
      });
      return;
    }

    const id = component.definition?.id;
    if (!id) {
      showErrorToast(new Error(uiText.stores.componentMissingIdentity), uiText.stores.actionFailed);
      return;
    }

    // 安装器已经在缓存里：不用联网，也没有版本可选，但确认这一步不能省。
    // 厂商、条款、是否需要提权都只在这个弹窗里说过，跳过它等于替用户按了「装」。
    if (component.installerAvailable && !component.installed) {
      setAcquisitionRequest({ component, versions: [], loading: false, manual: false });
      return;
    }

    const manual = componentSourceKind(component) === "manual";
    setAcquisitionRequest({ component, versions: [], loading: !manual, manual });

    if (manual) {
      return;
    }

    try {
      const versionOptions = await fetchComponentVersionOptions(id);
      setAcquisitionRequest((current) => current && current.component.definition?.id === id
        ? { ...current, versions: versionOptions.options ?? [], loading: false,
            catalogStatus: versionOptions.catalogStatus, complete: versionOptions.complete }
        : current);
    } catch (error) {
      // 版本列表拿不到时不把对话框关掉：用户仍然可以读条款并按已验证版本继续。
      const reason = userFacingErrorMessage(error, uiText.componentAcquisition.versionLookupFailed);
      setAcquisitionRequest((current) => current && current.component.definition?.id === id
        ? {
          ...current,
          loading: false,
          catalogStatus: "error",
          complete: false,
          versions: [
            { choice: "verified", available: true, version: null, assetName: null, unavailableReason: null },
            { choice: "latest", available: false, version: null, assetName: null, unavailableReason: reason }
          ]
        }
        : current);
    }
  }

  async function confirmAcquisition(versionChoice: string | null) {
    const request = acquisitionRequest();
    setAcquisitionRequest(null);
    if (!request) {
      return;
    }

    if (request.manual) {
      await guideManualAcquisition(request.component);
      return;
    }

    await runComponentInstall(request.component, versionChoice);
  }

  /** 手动获取：同时打开来源页和安装器缓存目录，并说清楚下一步做什么。 */
  async function guideManualAcquisition(component: ManagedComponent) {
    const sourcePageUrl = component.definition?.sourcePageUrl;
    if (sourcePageUrl) {
      window.open(sourcePageUrl, "_blank", "noopener");
    }

    const installerDirectory = component.installerDirectory;
    if (installerDirectory) {
      try {
        await openPath(installerDirectory);
      } catch (error) {
        showErrorToast(error, uiText.softwareActions.openPathFailed);
      }
    }

    options.showToast({
      tone: "info",
      title: uiText.feedback.info,
      message: uiText.componentAcquisition.manualNextStep,
      details: installerDirectory ? [installerDirectory] : undefined
    });
  }

  async function runComponentInstall(component: ManagedComponent, versionChoice: string | null) {
    const id = component.definition?.id;
    const key = managementActionKey("component", id);
    try {
      if (!id) {
        throw new Error(uiText.stores.componentMissingIdentity);
      }

      setActionLabel(key, uiText.stores.waiting);
      const operation = await options.operations.submit(
        componentInstallCommand(id, true, versionChoice));
      setActionLabel(key, labelForOperation(operation));
      const completed = await options.operations.waitForTerminal(operation.id);
      showOperationResult(completed);
    } catch (error) {
      showErrorToast(error, uiText.stores.actionFailed);
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

  async function verifyComponent(component: ManagedComponent) {
    const id = component.definition?.id;
    if (!options.runtimeEffectsEnabled() || !component.canVerify || !id) return;
    const key = managementActionKey("component", id);
    if (actionLabels()[key]) return;
    setActionLabel(key, uiText.stores.progressWorking);
    try {
      const result = await requestComponentVerification(id);
      options.showToast({
        tone: result.state === "runtimeFilesUnavailable" ? "warning" : "info",
        title: uiText.management.verify,
        message: renderBackendMessage(result.message)
      });
    } catch (error) {
      showErrorToast(error, uiText.stores.actionFailed);
    } finally {
      clearActionLabel(key);
      await refreshComponents();
    }
  }

  async function uninstallSoftware(softwareRecord: SoftwareRecord, actionKey = managementActionKey("software", softwareRecord.id)) {
    if (!options.runtimeEffectsEnabled() || !options.mutablePersistenceEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.stores.uninstallDisabled
      });
      return;
    }

    const operations = softwareRecord.operations ?? {};
    if (!operations.canUninstall) {
      options.showToast({ tone: "warning", title: uiText.feedback.warning, message: operations.uninstallMessageCode
          ? renderBackendMessage(operations.uninstallMessageCode)
          : userFacingMessage(operations.uninstallMessage, uiText.stores.cannotUninstall) });
      return;
    }

    const actionText = operations.uninstallKind === "WindowsUninstaller"
      ? uiText.stores.uninstallerWarning
      : uiText.stores.deleteDirectoryWarning;
    if (!await options.confirmDialog({
      title: softwareRecord.name,
      message: actionText,
      tone: "warning"
    })) {
      return;
    }

    setActionLabel(actionKey, uiText.stores.uninstalling);
    try {
      const operation = await options.operations.submit(
        softwareUninstallCommand(softwareRecord.id, true));
      setActionLabel(actionKey, labelForOperation(operation));
      const completed = await options.operations.waitForTerminal(operation.id);
      showOperationResult(completed);
    } catch (error) {
      showErrorToast(error, uiText.stores.softwareActionFailed);
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
        message: userFacingMessage(operationResultText(operation), uiText.stores.operationCompleted)
      });
      return;
    }

    if (operation.state === "stateUncertain") {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: userFacingMessage(operation.error, uiText.stores.operationUncertain)
      });
      return;
    }

    if (operation.state === "failed") {
      options.showToast({
        tone: "error",
        title: uiText.feedback.error,
        message: userFacingMessage(operation.error, uiText.stores.operationIncomplete)
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
    manualSoftwareSource,
    classifySoftware,
    manualSoftwareActionInProgress,
    openManualSoftwareModal,
    closeManualSoftwareModal,
    submitManualSoftware,
    refreshComponents,
    refreshSoftware,
    refreshBrowserRuntimes,
    refreshState,
    installComponent,
    verifyComponent,
    acquisitionRequest,
    cancelAcquisition,
    confirmAcquisition,
    openAcquisitionLink,
    uninstallSoftware,
    dispose: unsubscribeOperations
  };
}

function isManualSoftwareKind(kind: ManagementKind): kind is ManualSoftwareKind {
  return kind === "adapted" || kind === "game" || kind === "high_performance" || kind === "other";
}

function labelForOperation(operation: OperationSnapshot) {
  const stage = operation.progress?.stage ?? "";
  if (operation.state === "queued" || operation.state === "startPending") {
    return uiText.stores.waiting;
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
    return `${stage || uiText.stores.progressRunning}${percent}${speed}`;
  }
  if (operation.state === "cancelPending") {
    return uiText.stores.progressCanceling;
  }
  if (operation.state === "retryWait") {
    return uiText.stores.progressRetryWait;
  }
  if (operation.state === "recoveryPending") {
    return uiText.stores.progressRecovering;
  }
  if (operation.state === "succeeded") {
    return uiText.stores.progressCompleted;
  }
  if (operation.state === "failed") {
    return uiText.stores.progressFailed;
  }
  if (operation.state === "canceled") {
    return uiText.stores.progressCanceled;
  }
  if (operation.state === "stateUncertain") {
    return uiText.stores.progressUncertain;
  }

  return stage || uiText.stores.progressWorking;
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
      uiText.stores.operationStateDisabled);
  }
  if (snapshot.status === "ready") {
    return readyObservation(snapshot.capturedAt ?? undefined);
  }
  if (snapshot.status === "disposed" || snapshot.status === "error" || snapshot.status === "unavailable") {
    return failedObservation(
      loadingObservation(),
      uiText.stores.operationStateStopped);
  }
  return loadingObservation();
}
