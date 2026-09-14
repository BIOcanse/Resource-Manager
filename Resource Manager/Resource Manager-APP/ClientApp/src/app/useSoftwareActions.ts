import { createEffect, createMemo, createSignal, on, onCleanup } from "solid-js";
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
import { migrationRestoreCommand } from "../data/operations/operationCommands";
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
import type { StandardContextMenuModel } from "../components/StandardContextMenu";
import {
  isManagementInventorySubpage,
  type ManagementSubpageId
} from "../management/managementNavigation";
import type { ManagementStore } from "../stores/managementStore";
import type { MigrationWorkbenchStore } from "../stores/migrationStore";
import type { MonitorStore } from "../stores/monitorStore";
import { managementRoleLabel, softwareDisplayKindLabel, uiText } from "../text";
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
  SoftwareDataMigrationRecord,
  SoftwareDetailModel,
  SoftwareRecord,
  SystemProcessOperationResult
} from "../types";
import {
  formatBoolean,
  formatCapabilityFlags,
  getRootMigrationPaths,
  isComponentInstalled,
  normalizeName,
  pathLooksUsable,
  textOrEmpty,
  uniqueTextValues
} from "../utils";
import { pickShellFolder } from "../utils";
import { requestDeviceTopologySelection } from "../deviceTopology/deviceTopologyStore";
import {
  SoftwareMetadataLeaseBinding
} from "../softwareMetadata/SoftwareMetadataLeaseBinding";
import type { SoftwareMetadataQuery } from
  "../softwareMetadata/softwareMetadataApi";
import type {
  SoftwareMetadataLookupResult
} from "../softwareMetadata/softwareMetadataDecoder";
import { projectSoftwareMetadataDetail } from
  "../softwareMetadata/softwareMetadataDetailProjection";
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
  migration: MigrationWorkbenchStore;
  setActivePage: Setter<PageId>;
  openManagementSubpage: (subpage: ManagementSubpageId) => void;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  showErrorToast: (error: unknown, fallback: string) => void;
  mutablePersistenceEnabled: Accessor<boolean>;
  gpuPlacementEnabled: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  operations: Pick<OperationRegistry, "submit" | "waitForTerminal">;
  softwareMetadataSource: SourceFamily<
    SoftwareMetadataQuery,
    SoftwareMetadataLookupResult
  >;
  softwareMetadataLanguage: Accessor<string>;
}

type SoftwareContextMatchTarget = Pick<SoftwareContextMenuTarget, "name"> & Partial<Pick<SoftwareContextMenuTarget, "softwareId" | "softwareName">>;

export function useSoftwareActions(options: UseSoftwareActionsOptions) {
  const { monitor, management, migration, setActivePage, openManagementSubpage, confirmDialog, showToast, showErrorToast } = options;
  const [softwareDetail, setSoftwareDetail] = createSignal<SoftwareDetailModel | null>(null);
  const [softwareDetailNotice, setSoftwareDetailNotice] = createSignal<DetailNotice | null>(null);
  const [softwareDetailActionInProgress, setSoftwareDetailActionInProgress] = createSignal(false);
  const [gpuPlacementSettings, setGpuPlacementSettings] = createSignal<GpuPlacementSoftwareSettingsSnapshot | null>(null);
  const [gpuPlacementLoading, setGpuPlacementLoading] = createSignal(false);
  const [contextMenu, setContextMenu] = createSignal<StandardContextMenuModel | null>(null);
  const [highlightedSoftwareId, setHighlightedSoftwareId] = createSignal<string | null>(null);
  let highlightClearTimer: number | undefined;
  let softwareDetailRequestId = 0;
  const softwareMetadataBinding = new SoftwareMetadataLeaseBinding(
    options.softwareMetadataSource,
    projectSoftwareMetadataSnapshot);

  const softwareDetailMigrationRecords = createMemo(() => {
    const detail = softwareDetail();
    return detail ? migrationRecordsForDetail(detail, migration.records()) : [];
  });

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
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "当前启动配置不提供软件策略编辑。" });
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
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "软件列表中还没有匹配项。请先到“组件与软件”刷新。" });
      return;
    }

    openSoftwareDetail("software", matched);
  }

  async function openSoftwareSettingsById(softwareId: string, softwareName: string) {
    if (!options.mutablePersistenceEnabled()) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "当前启动配置不提供软件策略编辑。" });
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
        message: "软件列表中没有找到这项策略对应的软件。"
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
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "没有匹配到正在运行的进程。" });
      return;
    }

    if (!await confirmDialog({
      title: `结束任务：${target.softwareName ?? target.name}`,
      message: `将结束 ${processTargets.length} 个当前匹配进程。`,
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await terminateProcesses(processTargets);
      showSystemProcessResult(result, "结束任务完成");
    } catch (error) {
      showErrorToast(error, "结束任务失败");
    }
  }

  async function createDumpForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const processTargets = normalizedProcessTargets(target);
    if (processTargets.length === 0) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "没有可创建转储的进程。" });
      return;
    }

    if (!await confirmDialog({
      title: `创建内存转储：${target.softwareName ?? target.name}`,
      message: `将为 ${processTargets.length} 个进程创建完整内存转储，文件可能很大。`,
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await createProcessDumps(processTargets);
      showSystemProcessResult(result, "内存转储完成");
    } catch (error) {
      showErrorToast(error, "创建内存转储文件失败");
    }
  }

  function goToProcessDetailsForSoftware(target: SoftwareContextMenuTarget) {
    if (!target.softwareId) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "当前软件没有稳定的软件标识，无法标记进程。" });
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
    showToast({ tone: "info", title: uiText.feedback.info, message: `已转到进程级，并标记 ${target.softwareName ?? target.name} 的进程。` });
  }

  async function openFileLocationForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryFileLocation(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "没有可打开的安装目录或进程路径。" });
      return;
    }

    try {
      const result = await openPath(path, fileLocationSelectMode(target));
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, "已打开文件所在的位置"), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, "打开文件所在的位置失败");
    }
  }

  async function searchSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    try {
      const result = await searchOnline(target.softwareName ?? target.name);
      showToast({ tone: "info", title: uiText.feedback.info, message: "已打开在线搜索。", details: [result.url] });
    } catch (error) {
      showErrorToast(error, "在线搜索失败");
    }
  }

  async function openPropertiesForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryPropertiesPath(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: "没有可打开系统属性的文件或目录路径。" });
      return;
    }

    try {
      const result = await openProperties(path);
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, "已打开属性窗口"), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, "打开属性失败");
    }
  }

  function enrichSoftwareContextTarget(target: SoftwareContextMenuTarget): SoftwareContextMenuTarget {
    const liveRows = monitor.resourceTableSnapshot()?.rows ?? [];
    const matchingRows = liveRows.filter((row) => row.kind === "software" || row.kind === "process")
      .filter((row) => softwareContextTargetMatchesRow(target, row));
    const softwareRow = matchingRows.find((row) => row.kind === "software");
    const processRows = matchingRows.filter((row) => row.kind === "process");
    return {
      ...target,
      softwareId: target.softwareId ?? softwareRow?.softwareId,
      softwareName: target.softwareName ?? softwareRow?.softwareName ?? softwareRow?.name,
      processIds: uniqueNumbers([
        ...(target.processIds ?? []),
        ...(softwareRow?.processIds ?? []),
        ...processRows.flatMap((row) => row.processIds?.length ? row.processIds : row.processId ? [row.processId] : [])
      ]),
      processTargets: uniqueProcessTargets([
        ...(target.processTargets ?? []),
        ...processRows.flatMap((row) => row.processId && row.processStartKey
          ? [{
              processId: row.processId,
              processStartKey: row.processStartKey
            }]
          : [])
      ]),
      processNames: uniqueStrings([
        ...(target.processNames ?? []),
        ...(softwareRow?.processNames ?? []),
        ...processRows.flatMap((row) => row.processNames?.length ? row.processNames : [row.name])
      ]),
      executablePaths: uniqueStrings([
        ...(target.executablePaths ?? []),
        ...(softwareRow?.executablePaths ?? []),
        ...processRows.flatMap((row) => row.executablePaths ?? [])
      ]),
      rootPaths: uniqueStrings(target.rootPaths ?? [])
    };
  }

  function uniqueProcessTargets(
    values: Array<{ processId: number; processStartKey: string }>
  ) {
    return [...new Map(values.map((value) => [
      `${value.processId}:${value.processStartKey}`,
      value
    ])).values()];
  }

  function showSystemProcessResult(result: SystemProcessOperationResult, title: string) {
    const failed = result.items.filter((item) => item.state === "Failed");
    showToast({
      tone: failed.length > 0 ? "warning" : "success",
      title,
      message: userFacingMessage(result.message, "进程操作已完成"),
      details: uniqueStrings([
        result.directoryPath ?? "",
        ...result.items.map((item) => item.path || item.processName || (item.processId ? `进程 ${item.processId}` : ""))
      ]).slice(0, 10)
    });
  }

  function openSoftwareDetail(type: "component" | "software", value: ManagedComponent | SoftwareRecord) {
    const model = createSoftwareDetailModel(type, value);
    setSoftwareDetail(model);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    if (options.runtimeEffectsEnabled()) {
      void migration.refreshRecords();
    }
    const requestId = ++softwareDetailRequestId;
    if (options.gpuPlacementEnabled() && model.type === "software" && model.id) {
      void loadGpuPlacementSettingsForDetail(model, requestId);
    } else {
      setGpuPlacementLoading(false);
    }
    if (model.type === "software" && model.softwareIdentityId) {
      bindSoftwareMetadataForDetail(
        model,
        options.softwareMetadataLanguage());
    } else {
      softwareMetadataBinding.release();
    }
  }

  function closeSoftwareDetail() {
    setSoftwareDetail(null);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    setGpuPlacementLoading(false);
    softwareDetailRequestId += 1;
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

  async function loadGpuPlacementSettingsForDetail(model: SoftwareDetailModel, requestId: number) {
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
          });
        } catch (error) {
          if (isCurrentSoftwareDetailRequest(model, requestId)) {
            setSoftwareDetailNotice({
              message: "进程历史记录失败，但策略仍可读取。",
              tone: "warning",
              details: []
            });
          }
        }
      }

      const settingsSnapshot = await getGpuPlacementSoftwareSettings(model.id, model.name, model.kind);
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setGpuPlacementSettings(settingsSnapshot);
      }
    } catch (error) {
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setSoftwareDetailNotice({
          message: "读取软件调度策略失败。",
          tone: "error",
          details: []
        });
      }
    } finally {
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setGpuPlacementLoading(false);
      }
    }
  }

  function isCurrentSoftwareDetailRequest(model: SoftwareDetailModel, requestId: number) {
    const current = softwareDetail();
    return requestId === softwareDetailRequestId
      && current?.type === model.type
      && current?.id === model.id;
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
      setSoftwareDetailNotice({ message: "已保存软件级调度策略。", tone: "success" });
    } catch (error) {
      setSoftwareDetailNotice({
        message: "保存软件级调度策略失败。",
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
          ? "已保存策略，但运行调度配置尚未完全应用。"
          : interceptionPending
            ? "已保存策略，但启动拦截尚未生效。"
            : "已保存进程级调度策略。",
        tone: runtimePending || interceptionPending ? "warning" : "success",
        details: undefined
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: "保存进程级调度策略失败。",
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
      ? "已安装"
      : userFacingState(component.stateLabel || component.state);
    const roleLabel = managementRoleLabel(definition.managementRole);
    const rootPaths = uniqueTextValues([installRoot]).filter(pathLooksUsable);
    const rootMigrationPaths = getRootMigrationPaths(rootPaths, migration.roots());
    return {
      type: "component",
      id: textOrEmpty(definition.id),
      name,
      kind: "Managed",
      displayKind: roleLabel,
      state,
      message: purpose,
      dataSearchName: name,
      rootPaths,
      suggestedRootPaths: [],
      executablePaths: [],
      requiresRootPathConfirmation: false,
      identityConfirmed: true,
      issues: [],
      rootMigrationPaths,
      rootMigrationDisabledReason: rootMigrationPaths.length > 0
        ? ""
        : rootPaths.length > 0
          ? "该目录已经是目标位置，或不适合迁移。"
          : "没有已知软件根目录。",
      baseRows: [
        ["名称", name],
        ["关系", roleLabel],
        ["状态", state],
        ["供应商", definition.vendor],
        ["分类", componentCategory(definition.id)],
        ["用途", purpose]
      ],
      softwareIdentityId: "",
      metadataRows: [],
      pathRows: [
        ["安装根目录", rootPaths],
        ["来源页", definition.sourcePageUrl],
        ["协议/条款", definition.externalTermsUrl]
      ],
      operationRows: [
        ["下载安装", formatCapabilityFlags([
          [component.canDownload, "可下载"],
          [component.installerAvailable, "安装器已就绪"],
          [component.canInstall, "可安装"],
          [component.canVerify, "可验证"]
        ])],
        ["需要提权", formatBoolean(definition.requiresElevation)],
        ["需要外部协议确认", formatBoolean(definition.requiresExternalTermsAcknowledgement)]
      ],
      capabilities: [],
      providers: []
    };
  }

  function createInstalledSoftwareDetailModel(record: SoftwareRecord): SoftwareDetailModel {
    const name = textOrEmpty(record.name) || "--";
    const rootPaths = uniqueTextValues(record.rootPaths ?? []).filter(pathLooksUsable);
    const rootMigrationPaths = getRootMigrationPaths(rootPaths, migration.roots());
    const operations = record.operations ?? {};
    const displayKind = softwareDisplayKindLabel(record.kind, record.displayKind);

    return {
      type: "software",
      id: textOrEmpty(record.id),
      name,
      kind: textOrEmpty(record.kind) || "Other",
      displayKind,
      state: userFacingState(record.state),
      message: userFacingMessage(record.message, displayKind),
      dataSearchName: name,
      rootPaths,
      suggestedRootPaths: uniqueTextValues(record.suggestedRootPaths ?? []).filter(pathLooksUsable),
      executablePaths: uniqueTextValues(record.executablePaths ?? []).filter(pathLooksUsable),
      requiresRootPathConfirmation: record.requiresRootPathConfirmation === true,
      identityConfirmed: record.identityConfirmed !== false,
      issues: record.issues ?? [],
      rootMigrationPaths,
      rootMigrationDisabledReason: rootMigrationPaths.length > 0
        ? ""
        : rootPaths.length > 0
          ? "该目录已经是目标位置，或不适合迁移。"
          : "没有找到可用的软件根目录。",
      baseRows: [
        ["名称", name],
        ["类型", displayKind],
        ["状态", userFacingState(record.state)],
        ["状态说明", userFacingMessage(record.message, displayKind)]
      ],
      softwareIdentityId: textOrEmpty(record.softwareIdentityId),
      metadataRows: [],
      pathRows: [
        ["根目录", rootPaths],
        ["待确认目录", record.requiresRootPathConfirmation ? record.suggestedRootPaths ?? [] : []],
        ["可执行文件", record.executablePaths ?? []]
      ],
      operationRows: [
        ["卸载能力", operations.canUninstall
          ? "可以卸载"
          : userFacingMessage(operations.uninstallMessage, "当前不可卸载")],
        ["卸载说明", userFacingMessage(operations.uninstallMessage, operations.canUninstall ? "可以使用软件提供的卸载方式" : "当前不可卸载")]
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
      const selectedPath = await pickShellFolder("检查并选择软件根目录", detail.suggestedRootPaths[0]);
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
        message: userFacingMessage(result.message, "软件根目录已确认"),
        tone: result.requiresRootPathConfirmation ? "warning" : "success",
        details: [result.rootPath]
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: userFacingErrorMessage(error, "确认软件根目录失败"),
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
      const result = await postJson<{ message?: string; openedPath?: string }>("/api/system/open-path", { path }, "打开路径失败");
      setSoftwareDetailNotice({ message: userFacingMessage(result.message, "已打开路径"), tone: "success", details: [result.openedPath ?? path] });
    } catch (error) {
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, "打开路径失败"), tone: "error", details: [path] });
    }
  }

  async function migrateSoftwareDataFromDetail() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail) {
      return;
    }

    await runSoftwareDetailAction("正在查找可迁移内容", async () => {
      migration.setSoftwareName(detail.dataSearchName);
      await migration.findCandidates();
      const candidates = migration.candidates();
      if (!candidates.length) {
        setSoftwareDetailNotice({ message: "没有发现可迁移内容。", tone: "warning" });
        return;
      }

      const first = candidates[0];
      openManagementSubpage("Migration");
      migration.fillDataMigration(detail.dataSearchName, [first.path ?? ""]);
      migration.setKind(first.recommendedMigrationKind ?? "Data");
      migration.setTargetCategory(first.recommendedTargetCategory ?? "UserData");
      setSoftwareDetailNotice({
        message: "已找到可迁移内容，并填入迁移设置。",
        tone: "success",
        details: candidates.slice(0, 6).map((candidate) => candidate.path ?? candidate.name ?? "")
      });
    });
  }

  async function migrateSoftwareRootFromDetail() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail || detail.rootMigrationPaths.length === 0) {
      return;
    }

    openManagementSubpage("Migration");
    migration.fillRootMigration(detail.name, detail.rootMigrationPaths);
    setSoftwareDetailNotice({
      message: "已填入根目录迁移面板，请预览确认后执行。",
      tone: "success",
      details: detail.rootMigrationPaths
    });
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
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, "操作失败"), tone: "error" });
    } finally {
      setSoftwareDetailActionInProgress(false);
    }
  }

  async function restoreSoftwareDetailMigrationRecord(record: SoftwareDataMigrationRecord) {
    if (!options.runtimeEffectsEnabled() || !await confirmDialog({
      title: "恢复迁移",
      message: "恢复会移除原位置的目录链接，并把当前数据复制回原位置。",
      tone: "warning"
    })) {
      return;
    }

    await runSoftwareDetailAction("正在恢复迁移记录", async () => {
      const result = await restoreMigrationRecord(record);
      setSoftwareDetailNotice({
        message: userFacingMessage(operationResultText(result), "恢复完成"),
        tone: "success",
        details: [textOrEmpty(record.sourcePath)].filter(Boolean)
      });
      await Promise.all([migration.refreshRecords(), management.refreshSoftware()]);
    });
  }

  async function restoreAllSoftwareDetailMigrations() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const records = softwareDetailMigrationRecords().filter(isActiveMigrationRecord);
    if (records.length === 0) {
      setSoftwareDetailNotice({ message: "当前软件没有可恢复的迁移记录。", tone: "warning" });
      return;
    }

    if (!await confirmDialog({
      title: "恢复软件迁移记录",
      message: `将恢复当前软件的 ${records.length} 条迁移记录，并把当前数据复制回原位置。`,
      tone: "warning"
    })) {
      return;
    }

    await runSoftwareDetailAction("正在恢复软件迁移记录", async () => {
      const restoredPaths: string[] = [];
      for (const record of records) {
        await restoreMigrationRecord(record);
        restoredPaths.push(textOrEmpty(record.sourcePath));
      }

      setSoftwareDetailNotice({
        message: `已恢复 ${restoredPaths.length} 条迁移记录。`,
        tone: "success",
        details: restoredPaths.filter(Boolean)
      });
      await Promise.all([migration.refreshRecords(), management.refreshSoftware()]);
    });
  }

  async function restoreMigrationRecord(record: SoftwareDataMigrationRecord) {
    let operation = await options.operations.submit(
      migrationRestoreCommand(record.id, true));
    if (!isTerminalOperation(operation)) {
      operation = await options.operations.waitForTerminal(operation.id);
    }
    if (operation.state !== "succeeded") {
      throw new Error(operation.error
        ?? (operation.state === "stateUncertain"
          ? "恢复结果不确定，请先检查实际状态。"
          : "恢复失败"));
    }
    return operation;
  }

  return {
    softwareDetail,
    softwareDetailNotice,
    softwareDetailMigrationRecords,
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
    migrateSoftwareDataFromDetail,
    migrateSoftwareRootFromDetail,
    restoreSoftwareDetailMigrationRecord,
    restoreAllSoftwareDetailMigrations,
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

function softwareContextTargetMatchesRow(
  target: SoftwareContextMatchTarget,
  row: { softwareId?: string | null; softwareName?: string | null; name: string })
{
  if (target.softwareId && row.softwareId && target.softwareId.toLowerCase() === row.softwareId.toLowerCase()) {
    return true;
  }

  const targetNames = [target.softwareName, target.name].map(normalizeName).filter(Boolean);
  if (targetNames.length === 0) {
    return false;
  }

  const rowNames = [row.softwareName, row.name].map(normalizeName).filter(Boolean);
  return targetNames.some((targetName) => rowNames.some((rowName) => rowName === targetName));
}

function migrationRecordsForDetail(
  detail: SoftwareDetailModel,
  records: SoftwareDataMigrationRecord[])
{
  const detailNames = new Set([
    detail.name,
    detail.dataSearchName,
    detail.id
  ].map(normalizeName).filter(Boolean));

  return records
    .filter((record) => detailNames.has(normalizeName(record.softwareName)))
    .sort(compareMigrationRecords);
}

function compareMigrationRecords(left: SoftwareDataMigrationRecord, right: SoftwareDataMigrationRecord) {
  const leftActive = isActiveMigrationRecord(left);
  const rightActive = isActiveMigrationRecord(right);
  if (leftActive !== rightActive) {
    return leftActive ? -1 : 1;
  }

  return Date.parse(right.createdAt ?? "") - Date.parse(left.createdAt ?? "");
}

function isActiveMigrationRecord(record: SoftwareDataMigrationRecord) {
  return textOrEmpty(record.state).toLowerCase() !== "restored";
}

function uniqueNumbers(values: number[]) {
  return [...new Set(values.filter((value) => Number.isFinite(value) && value > 0))];
}

function uniqueStrings(values: string[]) {
  return [...new Set(values.map(textOrEmpty).filter(Boolean))];
}
