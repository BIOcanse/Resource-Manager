import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { createMemo, createSignal, For, Show } from "solid-js";
import { unrecognizedGpuNames } from "../../data/gpu/gpuSchedulingModelSource";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { useSource } from "../../frontendRuntime/source/useSource";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import {
  frontendVisibilityDemandId,
  frontendVisibilitySurface
} from "../../frontendWork/frontendVisibilitySurface";
import {
  FrontendVisibilityDemandBinding,
  useFrontendVisibilityDemand
} from "../../frontendWork/useFrontendVisibilityDemand";
import type {
  AppOptimizationMode,
  OptimizationReportItem,
  OptimizationReportOverview,
  HostManagerSmartCoordinatorStatus,
  TrustedOptimizationTarget
} from "../../types";
import { presentOptimizationReport } from "./optimizationPresentation";
import {
  smartFailureExclusionLabel,
  smartFailureExclusionSections,
  smartFailureHint,
  smartFailureNativePlanStatus,
  smartFailureTitle
} from "./smartFailurePresentation.ts";
import { UserDetailsDialog } from "../../components/UserDetailsDialog";
import {
  observationCanRender,
  type ObservationState
} from "../../observation/observationState";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../../components/ObservationStateNotice";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl.tsx";
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { uiText } from "../../text.ts";
import { optimizationEvidenceValue, optimizationTargetName } from "./optimizationPresentation";
import {
  optimizationModeHasDomain,
  toggleOptimizationDomain,
  type OptimizationDomain
} from "../settings/optimizationMode.ts";

type ReportFilter = "untrusted" | "all" | "trusted";

interface OptimizationPageProps {
  overview: OptimizationReportOverview | null;
  reportsObservation: ObservationState;
  hostManagerStatus: HostManagerSmartCoordinatorStatus | null;
  hostManagerObservation: ObservationState;
  optimizationMode: AppOptimizationMode;
  pendingOptimizationMode: AppOptimizationMode | null;
  optimizationModeApplyRemainingSeconds: number;
  loading: boolean;
  actionId: string | null;
  onRefresh: () => void;
  onOptimizationModeChange: (mode: AppOptimizationMode) => void;
  onDismiss: (report: OptimizationReportItem) => void;
  onInspect: (report: OptimizationReportItem) => void;
  onRemoveTrust: (target: TrustedOptimizationTarget) => void;
}

export function OptimizationPage(props: OptimizationPageProps) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  // 未识别显卡的提示紧挨报告标题，和报告一起随报告区可见取得；页面隐藏时退掉，回来重新读取。
  const gpuScores = useSource(runtime.sources.gpuPerformanceScores, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.optimizationReports),
    refreshIntervalMs: null
  }));
  const modeDemandId = "optimization.mode-status";
  const headerDemandId = "optimization.report-header";
  useFrontendVisibilityDemand(modeDemandId, [frontendWorkIds.optimizationSmartStatus]);
  useFrontendVisibilityDemand(headerDemandId, [
    frontendWorkIds.optimizationReports,
    frontendWorkIds.optimizationSmartStatus
  ]);
  const [reportFilter, setReportFilter] = createSignal<ReportFilter>("untrusted");
  const [searchQuery, setSearchQuery] = createSignal("");
  const [detailReport, setDetailReport] = createSignal<OptimizationReportItem | null>(null);
  const [exclusionsOpen, setExclusionsOpen] = createSignal(false);
  const allReports = () => props.overview?.reports ?? [];
  const trustedTargets = () => props.overview?.trustedTargets ?? [];
  const untrustedReports = () => allReports().filter((report) => !isTrustedReport(report));
  const reports = () => {
    const filter = reportFilter();
    if (filter === "trusted") {
      return [];
    }
    if (filter === "all") {
      return allReports();
    }
    return untrustedReports();
  };
  const visibleReports = createMemo(() => filterOptimizationReports(reports(), searchQuery()));
  const unknownGpuNames = createMemo(() => {
    const source = gpuScores.snapshot();
    return sourceCanRender(source) && source.data
      ? unrecognizedGpuNames(source.data)
      : [];
  });
  const selectedOptimizationMode = () => props.pendingOptimizationMode ?? props.optimizationMode;
  const reportsAvailable = () => observationCanRender(props.reportsObservation);
  const reportActionsAvailable = () => props.reportsObservation.status === "ready"
    || props.reportsObservation.source === "backend-retained";
  const hostManagerAvailable = () => props.hostManagerObservation.status === "ready";
  const hostManagerStatusText = () => {
    if (props.pendingOptimizationMode) {
      return uiText.optimization.modeApplyCountdown(Math.max(1, props.optimizationModeApplyRemainingSeconds), optimizationModeLabel(props.pendingOptimizationMode));
    }

    const state = props.hostManagerStatus;
    if (!state) {
      return uiText.optimization.scheduleStateUnknown;
    }

    // 当前模式已由左侧选中的分段表达，这里只补分段表达不了的运行状态。
    return [
      state.mode !== "normal"
        ? state.schedulerRunning
          ? uiText.optimization.smartSchedulerRunning
          : uiText.optimization.smartSchedulerStopped
        : "",
      state.pendingChangeCount > 0 ? uiText.optimization.pendingChanges(state.pendingChangeCount) : "",
      state.appliedTargetCount > 0 ? uiText.optimization.appliedTargets(state.appliedTargetCount) : ""
    ].filter(Boolean).join(" · ");
  };

  return (
    <section id="optimizationPage" class="page active-page">
      <ObservationStateNotice
        state={props.hostManagerObservation}
        label={uiText.optimization.scheduleStateLabel}
        presentation="announce-loading"
      />
      <nav
        {...frontendVisibilitySurface(
          "visible.optimization.mode-status.surface",
          [modeDemandId])}
        class="optimization-modebar"
        aria-label={uiText.optimization.modeBar}
      >
        <div class="segmented optimization-mode-group" role="group" aria-label={uiText.optimization.modeBar}>
          <button
            type="button"
            class="segment optimization-mode"
            classList={{
              active: selectedOptimizationMode() === "normal",
              pending: props.pendingOptimizationMode === "normal"
            }}
            aria-pressed={selectedOptimizationMode() === "normal"}
            disabled={!hostManagerAvailable() || props.actionId === "smart-mode"}
            onClick={() => props.onOptimizationModeChange("normal")}
          >{uiText.optimization.modeNormal}</button>
          <For each={(["memory", "cpu", "gpu"] as const)}>
            {(domain) => (
              <button
                type="button"
                class="segment optimization-mode"
                classList={{
                  active: optimizationModeHasDomain(selectedOptimizationMode(), domain),
                  pending: props.pendingOptimizationMode !== null
                    && optimizationModeHasDomain(props.pendingOptimizationMode, domain)
                }}
                aria-pressed={optimizationModeHasDomain(selectedOptimizationMode(), domain)}
                disabled={!hostManagerAvailable() || props.actionId === "smart-mode"}
                onClick={() => props.onOptimizationModeChange(
                  toggleOptimizationDomain(selectedOptimizationMode(), domain))}
              >{optimizationDomainLabel(domain)}</button>
            )}
          </For>
        </div>
        <div class="optimization-mode-actions">
          <label class="toolbar-search">
            <input
              type="search"
              placeholder={uiText.optimization.search}
              aria-label={uiText.optimization.searchLabel}
              value={searchQuery()}
              onInput={(event) => setSearchQuery(event.currentTarget.value)}
            />
          </label>
          <Show when={props.actionId === "smart-mode" || hostManagerStatusText()}>
            <span class="optimization-mode-status">
              {props.actionId === "smart-mode" ? uiText.optimization.modeSwitching : hostManagerStatusText()}
            </span>
          </Show>
        </div>
      </nav>
      <Show when={props.hostManagerStatus?.faultClosedReason}>
        {(reason) => (
          <div class="observation-state error" role="alert">
            <strong>{uiText.optimization.runtimeFaultClosedTitle}</strong>
            <span>{uiText.optimization.runtimeFaultClosedDetail(reason())}</span>
          </div>
        )}
      </Show>
      {/* 当前已结束的调度段失败了：只看后端的 failure，不由“调度未运行”推断；和上面的故障关闭互不影响。 */}
      <Show when={props.hostManagerStatus?.failure}>
        {(failure) => (
          <div class="observation-state error optimization-smart-failure" role="alert" data-failure-kind={failure().kind}>
            <strong>{smartFailureTitle(failure())}</strong>
            <HelpHint>{smartFailureHint(failure())}</HelpHint>
            <Show when={smartFailureNativePlanStatus(failure())}>
              {(text) => <span class="optimization-smart-failure-plan">{text()}</span>}
            </Show>
            <Show when={(props.hostManagerStatus?.factExclusionCount ?? 0) > 0}>
              <div class="optimization-smart-failure-exclusions">
                <div class="optimization-smart-failure-exclusion-total">
                  <span>{uiText.smartFailure.exclusionTotal(props.hostManagerStatus?.factExclusionCount ?? 0)}</span>
                  <HelpHint>{uiText.smartFailure.exclusionsHint}</HelpHint>
                  <Show when={(props.hostManagerStatus?.factExclusions.length ?? 0) > 0}>
                    <button class="secondary details-button" type="button" onClick={() => setExclusionsOpen(true)}>
                      {uiText.smartFailure.viewExclusions}
                    </button>
                  </Show>
                </div>
                <ul class="optimization-smart-failure-counts" role="list" aria-label={uiText.smartFailure.summaryLabel}>
                  <For each={props.hostManagerStatus?.factExclusionCounts ?? []}>
                    {(group) => (
                      <li data-fact={group.fact} data-reason={group.reason}>
                        <span>{smartFailureExclusionLabel(group.fact, group.reason)}</span>
                        <strong>{formatInterfaceNumber(group.count)}</strong>
                      </li>
                    )}
                  </For>
                </ul>
              </div>
            </Show>
          </div>
        )}
      </Show>

      <section class="optimization-surface">
        <div
          {...frontendVisibilitySurface(
            "visible.optimization.report-header.surface",
            [headerDemandId])}
          class="panel-header"
        >
          <div class="optimization-heading">
            <h2>{uiText.optimization.reportPanel}</h2>
          </div>
          <div class="panel-header-actions">
            <SegmentedControl
              value={reportFilter()}
              options={[
                { id: "untrusted", label: uiText.optimization.filterUntrusted(String(reportsAvailable() ? untrustedReports().length : "--")) },
                { id: "all", label: uiText.optimization.filterAll(String(reportsAvailable() ? allReports().length : "--")) },
                { id: "trusted", label: uiText.optimization.filterTrusted(String(reportsAvailable() ? trustedTargets().length : "--")) }
              ] satisfies Array<{ id: ReportFilter; label: string }>}
              ariaLabel={uiText.optimization.filterLabel}
              class="optimization-report-filter"
              onChange={setReportFilter}
            />
            <button class="secondary" type="button" disabled={props.loading} onClick={() => {
              props.onRefresh();
              void gpuScores.refresh();
            }}>
              <StableLabel labels={{ idle: uiText.optimization.refresh, busy: uiText.optimization.refreshing }} current={props.loading ? "busy" : "idle"} />
            </button>
          </div>
        </div>

        <Show when={unknownGpuNames().length > 0}>
          <div class="optimization-gpu-score-notice" role="status">
            <strong>{uiText.optimization.gpuScoreUnrecognizedTitle}</strong>
            <span>{uiText.optimization.gpuScoreUnrecognizedMessage(unknownGpuNames().join(", "))}</span>
          </div>
        </Show>

        <ObservationStateBoundary
          state={props.reportsObservation}
          label={uiText.optimization.reportPanel}
          onRetry={props.onRefresh}
        >
          <Show when={reportFilter() === "trusted"}>
            <div class="optimization-trusted-targets" role="group" aria-label={uiText.optimization.trusted}>
              <For each={trustedTargets().filter(target =>
                matchesSearch(normalizeSearchQuery(searchQuery()), optimizationTargetName(target), target.targetKey))}>
                {(target) => <article class="optimization-report">
                  <strong>{optimizationTargetName(target)}</strong>
                  <button class="secondary" type="button"
                    disabled={!reportActionsAvailable() || props.actionId === target.id}
                    onClick={() => props.onRemoveTrust(target)}>{uiText.optimization.removeTrust}</button>
                </article>}
              </For>
            </div>
          </Show>
          <Show
            when={visibleReports().length > 0}
            fallback={<Show when={props.reportsObservation.status === "ready" && (reportFilter() === "untrusted" || !(props.overview?.trustedTargets.length))}><div class="optimization-empty">{reports().length > 0 ? uiText.optimization.noSearchResults : uiText.optimization.empty}</div></Show>}
          >
            <div class="optimization-report-list">
              <For each={visibleReports()}>
              {(report) => {
                const demandId = frontendVisibilityDemandId("optimization.report", report.id);
                const trustedTarget = () => findTrustedTarget(props.overview?.trustedTargets ?? [], report);
                const presentation = () => presentOptimizationReport(report);
                return (
                  <article
                    {...frontendVisibilitySurface(`visible.${demandId}.surface`, [demandId])}
                    class={`optimization-report severity-${report.severity}`}
                  >
                    <FrontendVisibilityDemandBinding
                      demandId={demandId}
                      workIds={[frontendWorkIds.optimizationReports]}
                    />
                    <div class="optimization-report-main">
                      <strong>{presentation().title}</strong>
                      <span>{presentation().summary}</span>
                    </div>
                    <div class="optimization-report-actions">
                      <Show when={isTrustedReport(report)}>
                        <span class="optimization-report-state">{uiText.optimization.trusted}</span>
                      </Show>
                      <Show when={report.target.targetType === "software" && Boolean(report.target.softwareId)}>
                        <button class="secondary" type="button" onClick={() => props.onInspect(report)}>{uiText.optimization.viewSoftware}</button>
                      </Show>
                      <Show when={["device", "display", "physical_disk"].includes(report.target.targetType)}>
                        <button class="secondary" type="button" onClick={() => props.onInspect(report)}>{uiText.optimization.viewDevice}</button>
                      </Show>
                      <button class="secondary details-button" type="button" onClick={() => setDetailReport(report)}>
                        {uiText.optimization.details}
                      </button>
                      <Show
                        when={!isTrustedReport(report)}
                        fallback={
                          <button
                            class="secondary"
                            type="button"
                            disabled={!reportActionsAvailable() || !trustedTarget() || props.actionId === trustedTarget()?.id}
                            onClick={() => {
                              const target = trustedTarget();
                              if (target) {
                                props.onRemoveTrust(target);
                              }
                            }}
                          >
                            {uiText.optimization.removeTrust}
                          </button>
                        }
                      >
                        <button
                          class="secondary"
                          type="button"
                          disabled={!reportActionsAvailable() || props.actionId === report.id}
                          onClick={() => props.onDismiss(report)}
                        >
                          {uiText.optimization.dismiss}
                        </button>
                      </Show>
                    </div>
                  </article>
                );
              }}
              </For>
            </div>
          </Show>
        </ObservationStateBoundary>
      </section>
      <UserDetailsDialog
        open={Boolean(detailReport())}
        title={detailReport() ? presentOptimizationReport(detailReport()!).title : uiText.optimization.detailTitle}
        summary={detailReport() ? presentOptimizationReport(detailReport()!).summary : undefined}
        sections={detailReport() ? presentOptimizationReport(detailReport()!).details : []}
        onClose={() => setDetailReport(null)}
      />
      <UserDetailsDialog
        open={exclusionsOpen()}
        title={uiText.smartFailure.detailTitle}
        summary={uiText.smartFailure.detailSummary(
          props.hostManagerStatus?.factExclusions.length ?? 0,
          props.hostManagerStatus?.factExclusionCount ?? 0)}
        sections={props.hostManagerStatus ? smartFailureExclusionSections(props.hostManagerStatus) : []}
        onClose={() => setExclusionsOpen(false)}
      />
    </section>
  );
}

function isTrustedReport(report: OptimizationReportItem) {
  return report.state === "trusted_suppressed";
}

function findTrustedTarget(
  targets: readonly TrustedOptimizationTarget[],
  report: OptimizationReportItem
) {
  return targets.find((target) =>
    target.targetType === report.target.targetType
    && target.targetKey === report.target.targetKey
    && (report.target.targetType === "software"
      || report.target.targetType === "drive"
      || target.reportType === advisoryTrustScope(report.type)));
}

function advisoryTrustScope(reportType: string) {
  switch (reportType) {
    case "power_profile_not_performance_focused": return "power-profile";
    case "external_display_link_capability_gap": return "display-link";
    case "device_driver_problem": return "device-driver";
    case "external_disk_duplicate_security_scan": return "disk-security-scan";
    case "external_disk_idle_timeout_too_short": return "disk-power";
    case "physical_disk_latency_high": return "disk-latency";
    case "physical_disk_health_warning": return "disk-health";
    case "external_disk_link_capability_gap": return "disk-link";
    case "volume_fragmentation_high": return "disk-fragmentation";
    case "cpu_sustained_thermal_throttling": return "cpu-thermal";
    case "system_interrupt_pressure": return "system-interrupt";
    default: return reportType;
  }
}

function optimizationModeLabel(mode: AppOptimizationMode) {
  return mode === "normal"
    ? uiText.optimization.modeNormal
    : (["memory", "cpu", "gpu"] as const)
      .filter((domain) => optimizationModeHasDomain(mode, domain))
      .map(optimizationDomainLabel)
      .join(" · ");
}

function optimizationDomainLabel(domain: OptimizationDomain) {
  if (domain === "memory") return uiText.optimization.modeLimited;
  return domain === "cpu"
    ? uiText.optimization.modeCpu
    : uiText.optimization.modeGpu;
}

function filterOptimizationReports(
  reports: readonly OptimizationReportItem[],
  query: string
) {
  const normalizedQuery = normalizeSearchQuery(query);
  if (!normalizedQuery) {
    return reports;
  }

  return reports.filter((report) => matchesOptimizationReportSearch(report, normalizedQuery));
}

function matchesOptimizationReportSearch(report: OptimizationReportItem, query: string) {
  return matchesSearch(query,
    report.id,
    report.type,
    report.state,
    report.severity,
    report.confidence,
    // 标题与正文由前端成句，搜索也要按用户看到的那句话来匹配。
    presentOptimizationReport(report).title,
    presentOptimizationReport(report).summary,
    report.target.targetType,
    report.target.targetKey,
    report.target.name,
    report.target.softwareId,
    report.target.softwareName,
    report.target.softwareKind,
    report.target.displayKind,
    report.target.driveLetter,
    ...(report.target.processNames ?? []),
    report.context?.contextKind,
    report.context?.foregroundProcessName,
    report.context?.foregroundExecutablePath,
    report.context?.foregroundSoftwareId,
    report.context?.foregroundSoftwareName,
    report.context?.confidence,
    ...(report.context?.evidence ?? []),
    report.evidence.resourceKind,
    optimizationEvidenceValue(report.evidence, report.evidence.averageValue),
    optimizationEvidenceValue(report.evidence, report.evidence.peakValue),
    optimizationEvidenceValue(report.evidence, report.evidence.currentValue),
    ...(report.target.processNames ?? []));
}

function normalizeSearchQuery(value: string) {
  return value.trim().toLocaleLowerCase();
}

function matchesSearch(query: string, ...values: unknown[]) {
  return values.some((value) => String(value ?? "").toLocaleLowerCase().includes(query));
}
