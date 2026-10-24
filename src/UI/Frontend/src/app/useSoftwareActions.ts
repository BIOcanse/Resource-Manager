import { createEffect, createMemo, createSignal, on, onCleanup } from "solid-js";
import { softwareCategorySourceLabel } from "../presentation/softwareClassification.ts";
import { systemProcessResultDetail, systemProcessResultSummary } from "../presentation/systemProcessResult";
import type { Accessor, Setter } from "solid-js";
import {
  confirmPortableSoftwareRoot,
  createProcessDumps,
  getGpuPlacementSoftwareSettings,
  observeGpuPlacementProcesses,
  openPath,
  openProperties,
  postJson,
  saveGpuPlacementProcessPolicy,
  saveGpuPlacementSoftwarePolicy,
  searchOnline,
  terminateProcesses
} from "../api";
import type { OperationRegistry } from "../frontendRuntime/operations/OperationRegistry";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import type { SourceFamily } from
  "../frontendRuntime/source/SourceDescriptor";
import type { SourceSnapshot } from
  "../frontendRuntime/source/SourceSnapshot";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import {
  createSoftwareContextMenuModel,
  fileLocationSelectMode,
  normalizedProcessTargets,
  primaryFileLocation,
  primaryPropertiesPath
} from "../components/SoftwareContextMenu";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { enrichSoftwareContextTarget as enrichContextTarget, softwareContextTargetMatchesRow } from "../components/softwareContextTarget.ts";
import type { StandardContextMenuModel } from "../components/StandardContextMenu";
import {
  isManagementInventorySubpage,
  type ManagementSubpageId
} from "../features/management/managementNavigation";
import type { ManagementStore } from "../stores/managementStore";
import type { MonitorStore } from "../stores/monitorStore";
import { managementRoleLabel, softwareDisplayKindLabel, uiText } from "../text.ts";
import type {
  DetailNotice,
  GpuPlacementObservedProcessInput,
  GpuPlacementProcessPolicy,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareSettingsSnapshot,
  ManagedComponent,
  OperationSnapshot,
  OptimizationReportItem,
  PageId,
  SoftwareDetailModel,
  SoftwareRecord,
  SystemProcessOperationResult
} from "../types";
import {
  formatBoolean,
  formatCapabilityFlags,
  isComponentInstalled,
  normalizeName,
  pathLooksUsable,
  textOrEmpty,
  uniqueTextValues
} from "../utils";
import { pickShellFolder } from "../utils";
import { requestDeviceTopologySelection } from "../features/deviceTopology/deviceTopologyStore";
import {
  SoftwareMetadataLeaseBinding
} from "../features/softwareMetadata/SoftwareMetadataLeaseBinding";
import type { SoftwareMetadataQuery } from
  "../features/softwareMetadata/softwareMetadataApi";
import type {
  SoftwareMetadataLookupResult
} from "../features/softwareMetadata/softwareMetadataDecoder";
import { projectSoftwareMetadataDetail } from
  "../features/softwareMetadata/softwareMetadataDetailProjection";
import {
  componentCategory,
  componentDisplayName,
  componentPurpose,
  userFacingErrorMessage,
  userFacingMessage,
  userFacingState
} from "../presentation/userFacingText";

interface UseSoftwareActionsOptions {
  monitor: MonitorStore;
  management: ManagementStore;
  setActivePage: Setter<PageId>;
  openManagementSubpage: (subpage: ManagementSubpageId) => void;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  showErrorToast: (error: unknown, fallback: string) => void;
  mutablePersistenceEnabled: Accessor<boolean>;
  gpuPlacementEnabled: Accessor<boolean>;
  frontendVisible: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  operations: Pick<OperationRegistry, "submit" | "waitForTerminal">;
  softwareMetadataSource: SourceFamily<
    SoftwareMetadataQuery,
    SoftwareMetadataLookupResult
  >;
  softwareMetadataLanguage: Accessor<string>;
}

export function useSoftwareActions(options: UseSoftwareActionsOptions) {
  const { monitor, management, setActivePage, openManagementSubpage, confirmDialog, showToast, showErrorToast } = options;
  const [softwareDetail, setSoftwareDetail] = createSignal<SoftwareDetailModel | null>(null);
  const [softwareDetailNotice, setSoftwareDetailNotice] = createSignal<DetailNotice | null>(null);
  const [softwareDetailActionInProgress, setSoftwareDetailActionInProgress] = createSignal(false);
  const [gpuPlacementSettings, setGpuPlacementSettings] = createSignal<GpuPlacementSoftwareSettingsSnapshot | null>(null);
  const [gpuPlacementLoading, setGpuPlacementLoading] = createSignal(false);
  const [contextMenu, setContextMenu] = createSignal<StandardContextMenuModel | null>(null);
  const [highlightedSoftwareId, setHighlightedSoftwareId] = createSignal<string | null>(null);
  let highlightClearTimer: number | undefined;
  const softwareMetadataBinding = new SoftwareMetadataLeaseBinding(
    options.softwareMetadataSource,
    projectSoftwareMetadataSnapshot);

  onCleanup(() => {
    if (highlightClearTimer !== undefined) {
      window.clearTimeout(highlightClearTimer);
    }
    softwareMetadataBinding.release();
  });

  createEffect(on(options.softwareMetadataLanguage, (language) => {
    const model = softwareDetail();
    if (model?.type === "software" && model.softwareIdentityId) {
      bindSoftwareMetadataForDetail(model, language);
    }
  }, { defer: true }));

  async function inspectOptimizationTarget(report: OptimizationReportItem) {
    if (report.target.targetType !== "Software") {
      requestDeviceTopologySelection(report.target.targetKey);
      setActivePage("details");
      return;
    }

    if (!options.mutablePersistenceEnabled()) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.policyEditUnavailable });
      return;
    }

    if (management.software().length === 0) {
      await management.refreshSoftware();
    }

    const target = report.target;
    const matched = management.software().find((item) =>
      textOrEmpty(item.id).toLowerCase() === textOrEmpty(target.softwareId ?? target.targetKey).toLowerCase()
      || normalizeName(item.name) === normalizeName(target.displayName));
    if (!matched) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.softwareListNoMatch });
      return;
    }

    openSoftwareDetail("software", matched);
  }

  async function openSoftwareSettingsById(softwareId: string, softwareName: string) {
    if (!options.mutablePersistenceEnabled()) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.policyEditUnavailable });
      return;
    }

    let matched = management.software().find((item) =>
      item.id.localeCompare(softwareId, undefined, { sensitivity: "accent" }) === 0
      || normalizeName(item.name) === normalizeName(softwareName));
    if (!matched) {
      await management.refreshSoftware();
      matched = management.software().find((item) =>
        item.id.localeCompare(softwareId, undefined, { sensitivity: "accent" }) === 0
        || normalizeName(item.name) === normalizeName(softwareName));
    }
    if (!matched) {
      showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.softwareActions.policySoftwareMissing
      });
      return;
    }

    if (isManagementInventorySubpage(matched.kind as ManagementSubpageId)) {
      openManagementSubpage(matched.kind as ManagementSubpageId);
    }
    setActivePage("components");
    openSoftwareDetail("software", matched);
  }

  function openResourceTableSoftwareContextMenu(
    event: MouseEvent,
    target: SoftwareContextMenuTarget,
    returnFocusTarget?: HTMLElement | null)
  {
    openSoftwareContextMenu(event, target, returnFocusTarget);
  }

  function openManagementSoftwareContextMenu(event: MouseEvent, target: SoftwareContextMenuTarget) {
    openSoftwareContextMenu(event, target);
  }

  function openSoftwareContextMenu(
    event: MouseEvent,
    target: SoftwareContextMenuTarget,
    explicitReturnFocusTarget?: HTMLElement | null)
  {
    event.preventDefault();
    const enrichedTarget = enrichSoftwareContextTarget(target);
    const returnFocusTarget = explicitReturnFocusTarget
      ?? (event.currentTarget instanceof HTMLElement
      ? event.currentTarget
      : document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null);
    setContextMenu(createSoftwareContextMenuModel(event.clientX, event.clientY, enrichedTarget, {
      allowSystemActions: options.runtimeEffectsEnabled(),
      onExpand: expandSoftwareContextTarget,
      onTerminate: (current) => void terminateSoftwareContextTarget(current),
      onCreateDump: (current) => void createDumpForSoftwareContextTarget(current),
      onGoToDetails: goToProcessDetailsForSoftware,
      onOpenFileLocation: (current) => void openFileLocationForSoftwareContextTarget(current),
      onSearchOnline: (current) => void searchSoftwareContextTarget(current),
      onProperties: (current) => void openPropertiesForSoftwareContextTarget(current)
    }, returnFocusTarget));
  }

  function expandSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (target.softwareId) {
      monitor.toggleResourceTableExpand(target.softwareId);
    }
  }

  async function terminateSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const processTargets = normalizedProcessTargets(target);
    if (processTargets.length === 0) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noRunningProcess });
      return;
    }

    if (!await confirmDialog({
      title: uiText.softwareActions.endTaskTitle(target.kind === "process" ? target.name : target.softwareName ?? target.name),
      message: uiText.softwareActions.endTaskMessage(processTargets.length),
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await terminateProcesses(processTargets);
      showSystemProcessResult(result, uiText.softwareActions.endTaskDone);
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.endTaskFailed);
    }
  }

  async function createDumpForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const processTargets = normalizedProcessTargets(target);
    if (processTargets.length === 0) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noDumpProcess });
      return;
    }

    if (!await confirmDialog({
      title: uiText.softwareActions.dumpTitle(target.kind === "process" ? target.name : target.softwareName ?? target.name),
      message: uiText.softwareActions.dumpMessage(processTargets.length),
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await createProcessDumps(processTargets);
      showSystemProcessResult(result, uiText.softwareActions.dumpDone);
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.dumpFailed);
    }
  }

  function goToProcessDetailsForSoftware(target: SoftwareContextMenuTarget) {
    if (!target.softwareId) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noStableSoftwareIdentity });
      return;
    }

    setActivePage("monitor");
    monitor.changeResourceTableMode("process");
    setHighlightedSoftwareId(target.softwareId);
    if (highlightClearTimer !== undefined) {
      window.clearTimeout(highlightClearTimer);
    }
    highlightClearTimer = window.setTimeout(() => {
      setHighlightedSoftwareId((current) => current === target.softwareId ? null : current);
    }, 9000);
    showToast({ tone: "info", title: uiText.feedback.info, message: uiText.softwareActions.markedProcesses(target.softwareName ?? target.name) });
  }

  async function openFileLocationForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryFileLocation(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noInstallDirectory });
      return;
    }

    try {
      const result = await openPath(path, fileLocationSelectMode(target));
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, uiText.softwareActions.openedFileLocation), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.openFileLocationFailed);
    }
  }

  async function searchSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    try {
      const result = await searchOnline(target.softwareName ?? target.name);
      showToast({ tone: "info", title: uiText.feedback.info, message: uiText.softwareActions.openedOnlineSearch, details: [result.url] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.onlineSearchFailed);
    }
  }

  async function openPropertiesForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryPropertiesPath(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noPropertiesPath });
      return;
    }

    try {
      const result = await openProperties(path);
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, uiText.softwareActions.openedProperties), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.openPropertiesFailed);
    }
  }

  function enrichSoftwareContextTarget(target: SoftwareContextMenuTarget): SoftwareContextMenuTarget {
    return enrichContextTarget(target, monitor.resourceTableSnapshot()?.rows ?? []);
  }

  function showSystemProcessResult(result: SystemProcessOperationResult, title: string) {
    const summary = systemProcessResultSummary(result);
    showToast({
      tone: summary.complete ? "success" : "warning",
      title,
      message: summary.message,
      details: uniqueStrings([
        result.directoryPath ?? "",
        ...result.items.map(systemProcessResultDetail)
      ]).slice(0, 10)
    });
  }

  function openSoftwareDetail(type: "component" | "software", value: ManagedComponent | SoftwareRecord) {
    const model = createSoftwareDetailModel(type, value);
    setSoftwareDetail(model);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    if (model.type === "software" && model.softwareIdentityId) {
      bindSoftwareMetadataForDetail(
        model,
        options.softwareMetadataLanguage());
    } else {
      softwareMetadataBinding.release();
    }
  }

  async function removeManualRegistrationFromDetail() {
    const current = softwareDetail();
    const record = management.software().find((item) => item.id === current?.id);
    if (record && await management.removeManualRegistration(record) && softwareDetail()?.id === record.id) {
      closeSoftwareDetail();
    }
  }

  async function retrySoftwareMetadata() {
    try {
      await softwareMetadataBinding.refresh();
    } catch (error) {
      showErrorToast(error, uiText.managementFeedback.metadataFailed);
    }
  }

  function closeSoftwareDetail() {
    setSoftwareDetail(null);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    setGpuPlacementLoading(false);
    softwareMetadataBinding.release();
  }

  function createSoftwareDetailModel(type: "component" | "software", value: ManagedComponent | SoftwareRecord): SoftwareDetailModel {
    return type === "component"
      ? createComponentDetailModel(value as ManagedComponent)
      : createInstalledSoftwareDetailModel(value as SoftwareRecord);
  }

  function bindSoftwareMetadataForDetail(
    model: SoftwareDetailModel,
    language: string)
  {
    softwareMetadataBinding.switch({
      softwareIdentityId: model.softwareIdentityId,
      language
    });
  }

  function projectSoftwareMetadataSnapshot(
    query: SoftwareMetadataQuery,
    snapshot: SourceSnapshot<SoftwareMetadataLookupResult>)
  {
    setSoftwareDetail((current) =>
      projectSoftwareMetadataDetail(current, query, snapshot));
  }

  const gpuPlacementReadKey = createMemo(() => JSON.stringify([
    options.frontendVisible(), options.gpuPlacementEnabled(),
    softwareDetail()?.type, softwareDetail()?.id
  ]));
  createEffect(on(gpuPlacementReadKey, () => {
    const model = softwareDetail();
    if (!options.frontendVisible()) return;
    if (!options.gpuPlacementEnabled() || model?.type !== "software" || !model.id) {
      setGpuPlacementSettings(null);
      setGpuPlacementLoading(false);
      return;
    }
    const cancellation = new AbortController();
    void loadGpuPlacementSettingsForDetail(model, cancellation.signal);
    onCleanup(() => cancellation.abort());
  }));

  async function loadGpuPlacementSettingsForDetail(model: SoftwareDetailModel, signal: AbortSignal) {
    if (!options.gpuPlacementEnabled()) {
      setGpuPlacementSettings(null);
      setGpuPlacementLoading(false);
      return;
    }

    setGpuPlacementLoading(true);
    try {
      const observations = collectGpuPlacementProcessObservations(model);
      if (observations.length > 0) {
        try {
          await observeGpuPlacementProcesses({
            softwareId: model.id,
            softwareName: model.name,
            processes: observations
          }, signal);
        } catch (error) {
          if (!signal.aborted) {
            setSoftwareDetailNotice({
              message: uiText.softwareActions.processHistoryFailed,
              tone: "warning",
              details: []
            });
          }
        }
      }

      signal.throwIfAborted();
      const settingsSnapshot = await getGpuPlacementSoftwareSettings(model.id, model.name, model.kind, signal);
      if (!signal.aborted) {
        setGpuPlacementSettings(settingsSnapshot);
      }
    } catch (error) {
      if (!signal.aborted) {
        setSoftwareDetailNotice({
          message: uiText.softwareActions.policyReadFailed,
          tone: "error",
          details: []
        });
      }
    } finally {
      if (!signal.aborted) {
        setGpuPlacementLoading(false);
      }
    }
  }

  function collectGpuPlacementProcessObservations(model: SoftwareDetailModel): GpuPlacementObservedProcessInput[] {
    const liveRows = monitor.resourceTableSnapshot()?.rows ?? [];
    const target = {
      name: model.name,
      softwareId: model.id,
      softwareName: model.name
    };
    const matchingRows = liveRows
      .filter((row) => row.kind === "software" || row.kind === "process")
      .filter((row) => softwareContextTargetMatchesRow(target, row));
    const processRows = matchingRows.filter((row) => row.kind === "process");
    const softwareRows = matchingRows.filter((row) => row.kind === "software");
    const observations = [
      ...processRows.map((row) => ({
        processName: textOrEmpty(row.processNames?.[0]) || textOrEmpty(row.name),
        executablePath: textOrEmpty(row.executablePaths?.[0]) || null,
        architecture: null,
        processId: row.processId ?? row.processIds?.[0] ?? null,
        evidenceSources: ["resource-table", "software-detail"]
      })),
      ...softwareRows.flatMap((row) => {
        const names = row.processNames ?? [];
        const paths = row.executablePaths ?? [];
        const ids = row.processIds ?? [];
        return names.map((name, index) => ({
          processName: textOrEmpty(name),
          executablePath: textOrEmpty(paths[index]) || null,
          architecture: null,
          processId: ids[index] ?? null,
          evidenceSources: ["resource-table-software-row", "software-detail"]
        }));
      })
    ];
    const seen = new Set<string>();
    return observations
      .filter((item) => item.processName || item.executablePath)
      .filter((item) => {
        const key = `${normalizeName(item.processName)}|${textOrEmpty(item.executablePath).toLowerCase()}`;
        if (seen.has(key)) {
          return false;
        }

        seen.add(key);
        return true;
      });
  }

  async function saveGpuPlacementSoftwarePolicyFromDetail(policy: GpuPlacementSoftwarePolicy) {
    if (!options.gpuPlacementEnabled()) {
      return;
    }

    try {
      const saved = await saveGpuPlacementSoftwarePolicy(policy);
      setGpuPlacementSettings((current) => current
        ? {
          ...current,
          softwareId: saved.softwareId,
          softwareName: saved.softwareName,
          softwarePolicy: saved
        }
        : current);
      setSoftwareDetailNotice({ message: uiText.softwareActions.softwarePolicySaved, tone: "success" });
    } catch (error) {
      setSoftwareDetailNotice({
        message: uiText.softwareActions.softwarePolicySaveFailed,
        tone: "error",
        details: []
      });
    }
  }

  async function saveGpuPlacementProcessPolicyFromDetail(policy: GpuPlacementProcessPolicy) {
    if (!options.gpuPlacementEnabled()) {
      return;
    }

    try {
      const saved = await saveGpuPlacementProcessPolicy(policy);
      setGpuPlacementSettings((current) => {
        if (!current) {
          return current;
        }

        const nextInterceptions = saved.startupInterception
          ? [
            ...(current.startupInterceptions ?? []).filter(
              (item) => item.processKey !== saved.startupInterception?.processKey),
            saved.startupInterception
          ]
          : current.startupInterceptions;
        return {
          ...current,
          processPolicies: [
            ...current.processPolicies.filter((item) => item.processKey !== saved.policy.processKey),
            saved.policy
          ].sort((left, right) => left.processName.localeCompare(right.processName)),
          startupInterceptions: nextInterceptions
        };
      });
      const runtimePending = saved.runtimeApplicationDisposition !== "committedAndApplied";
      const interceptionPending = saved.startupInterceptionDisposition !== "applied"
        || Boolean(saved.startupInterception?.requested && !saved.startupInterception.registered);
      setSoftwareDetailNotice({
        message: runtimePending
          ? uiText.softwareActions.processPolicySavedRuntimePending
          : interceptionPending
            ? uiText.softwareActions.processPolicySavedLaunchPending
            : uiText.softwareActions.processPolicySaved,
        tone: runtimePending || interceptionPending ? "warning" : "success",
        details: undefined
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: uiText.softwareActions.processPolicySaveFailed,
        tone: "error",
        details: []
      });
    }
  }

  function createComponentDetailModel(component: ManagedComponent): SoftwareDetailModel {
    const definition = component.definition ?? {};
    const name = componentDisplayName(definition.id, definition.name);
    const purpose = componentPurpose(definition.id);
    const installRoot = textOrEmpty(component.installRoot);
    const state = isComponentInstalled(component)
      ? uiText.softwareActions.installed
      : userFacingState(component.stateLabel || component.state);
    const roleLabel = managementRoleLabel(definition.managementRole);
    const rootPaths = uniqueTextValues([installRoot]).filter(pathLooksUsable);
    return {
      type: "component",
      id: textOrEmpty(definition.id),
      name,
      kind: "managed",
      displayKind: roleLabel,
      state,
      message: purpose,
      rootPaths,
      suggestedRootPaths: [],
      executablePaths: [],
      requiresRootPathConfirmation: false,
      identityConfirmed: true,
      issues: [],
      baseRows: [
        [uiText.softwareActions.field.name, name],
        [uiText.softwareActions.field.relation, roleLabel],
        [uiText.softwareActions.field.state, state],
        [uiText.softwareActions.field.vendor, definition.vendor],
        [uiText.softwareActions.field.category, componentCategory()],
        [uiText.softwareActions.field.purpose, purpose]
      ],
      softwareIdentityId: "",
      metadataRows: [],
      pathRows: [
        [uiText.softwareActions.field.installRoot, rootPaths],
        [uiText.softwareActions.field.sourcePage, definition.sourcePageUrl],
        [uiText.softwareActions.field.terms, definition.externalTermsUrl]
      ],
      operationRows: [
        [uiText.softwareActions.field.downloadInstall, formatCapabilityFlags([
          [component.canDownload, uiText.softwareActions.capability.downloadable],
          [component.installerAvailable, uiText.softwareActions.capability.installerReady],
          [component.canInstall, uiText.softwareActions.capability.installable],
          [component.canVerify, uiText.softwareActions.capability.verifiable]
        ])],
        [uiText.softwareActions.field.requiresElevation, formatBoolean(definition.requiresElevation)],
        [uiText.softwareActions.field.requiresExternalTerms, formatBoolean(definition.requiresExternalTermsAcknowledgement)]
      ],
      capabilities: [],
      providers: []
    };
  }

  function createInstalledSoftwareDetailModel(record: SoftwareRecord): SoftwareDetailModel {
    const name = textOrEmpty(record.name) || "--";
    const rootPaths = uniqueTextValues(record.rootPaths ?? []).filter(pathLooksUsable);
    const operations = record.operations ?? {};
    const displayKind = softwareDisplayKindLabel(record.kind, record.displayKind);

    return {
      type: "software",
      manualRegistration: record.id.startsWith("manual:"),
      id: textOrEmpty(record.id),
      name,
      kind: record.kind,
      displayKind,
      state: userFacingState(record.state),
      message: userFacingMessage(record.message, displayKind),
      rootPaths,
      suggestedRootPaths: uniqueTextValues(record.suggestedRootPaths ?? []).filter(pathLooksUsable),
      executablePaths: uniqueTextValues(record.executablePaths ?? []).filter(pathLooksUsable),
      requiresRootPathConfirmation: record.requiresRootPathConfirmation === true,
      identityConfirmed: record.identityConfirmed !== false,
      issues: record.issues ?? [],
      baseRows: [
        [uiText.softwareActions.field.name, name],
        [uiText.softwareActions.field.type, displayKind],
        [uiText.management.categorySource, softwareCategorySourceLabel(record.categorySource)],
        [uiText.softwareActions.field.state, userFacingState(record.state)],
        [uiText.softwareActions.field.stateDescription, userFacingMessage(record.message, displayKind)]
      ],
      softwareIdentityId: textOrEmpty(record.softwareIdentityId),
      metadataRows: [],
      pathRows: [
        [uiText.softwareActions.field.rootDirectory, rootPaths],
        [uiText.softwareActions.field.pendingDirectory, record.requiresRootPathConfirmation ? record.suggestedRootPaths ?? [] : []],
        [uiText.softwareActions.field.executable, record.executablePaths ?? []]
      ],
      operationRows: [
        [uiText.softwareActions.field.uninstallCapability, operations.canUninstall
          ? uiText.softwareActions.canUninstall
          : userFacingMessage(operations.uninstallMessage, uiText.softwareActions.cannotUninstall)],
        [uiText.softwareActions.field.uninstallDescription, userFacingMessage(operations.uninstallMessage, operations.canUninstall ? uiText.softwareActions.uninstallViaSoftware : uiText.softwareActions.cannotUninstall)]
      ],
      capabilities: [],
      providers: []
    };
  }

  async function confirmPortableRootFromDetail() {
    if (!options.mutablePersistenceEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail || !detail.requiresRootPathConfirmation || !detail.id) {
      return;
    }

    try {
      const selectedPath = await pickShellFolder(uiText.softwareActions.pickRootTitle, detail.suggestedRootPaths[0]);
      if (!selectedPath) {
        return;
      }

      setSoftwareDetailActionInProgress(true);
      const result = await confirmPortableSoftwareRoot(detail.id, selectedPath);
      await management.refreshSoftware(true);
      const updated = management.software().find((record) => record.id === detail.id);
      if (updated) {
        const updatedModel = createInstalledSoftwareDetailModel(updated);
        setSoftwareDetail(updatedModel);
        if (updatedModel.softwareIdentityId) {
          bindSoftwareMetadataForDetail(
            updatedModel,
            options.softwareMetadataLanguage());
        }
      }
      setSoftwareDetailNotice({
        message: userFacingMessage(result.message, uiText.softwareActions.rootConfirmed),
        tone: result.requiresRootPathConfirmation ? "warning" : "success",
        details: [result.rootPath]
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: userFacingErrorMessage(error, uiText.softwareActions.rootConfirmFailed),
        tone: "error"
      });
    } finally {
      setSoftwareDetailActionInProgress(false);
    }
  }

  async function openLocalPath(path: string) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    try {
      const result = await postJson<{ message?: string; openedPath?: string }>("/api/system/open-path", { path }, uiText.softwareActions.openPathFailed);
      setSoftwareDetailNotice({ message: userFacingMessage(result.message, uiText.softwareActions.openedPath), tone: "success", details: [result.openedPath ?? path] });
    } catch (error) {
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, uiText.softwareActions.openPathFailed), tone: "error", details: [path] });
    }
  }

  async function runSoftwareDetailAction(progressMessage: string, action: () => Promise<void>) {
    if (softwareDetailActionInProgress()) {
      return;
    }

    setSoftwareDetailActionInProgress(true);
    setSoftwareDetailNotice({ message: progressMessage, tone: "progress" });
    try {
      await action();
    } catch (error) {
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, uiText.softwareActions.actionFailed), tone: "error" });
    } finally {
      setSoftwareDetailActionInProgress(false);
    }
  }

  return {
    softwareDetail,
    softwareDetailNotice,
    softwareDetailActionInProgress,
    gpuPlacementSettings,
    gpuPlacementLoading,
    contextMenu,
    highlightedSoftwareId,
    inspectOptimizationTarget,
    openSoftwareSettingsById,
    openResourceTableSoftwareContextMenu,
    openManagementSoftwareContextMenu,
    openSoftwareDetail,
    closeSoftwareDetail,
    removeManualRegistrationFromDetail,
    retrySoftwareMetadata,
    openLocalPath,
    confirmPortableRootFromDetail,
    saveGpuPlacementSoftwarePolicyFromDetail,
    saveGpuPlacementProcessPolicyFromDetail,
    closeContextMenu: () => setContextMenu(null)
  };
}

function operationResultText(operation: OperationSnapshot) {
  return typeof operation.result === "string" ? operation.result : null;
}

function uniqueStrings(values: string[]) {
  return [...new Set(values.map(textOrEmpty).filter(Boolean))];
}
