import { createMemo, createSignal, For, Show } from "solid-js";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import {
  frontendVisibilityDemandId,
  frontendVisibilitySurface
} from "../frontendWork/frontendVisibilitySurface";
import {
  FrontendVisibilityDemandBinding,
  useFrontendVisibilityDemand
} from "../frontendWork/useFrontendVisibilityDemand";
import type {
  AppOptimizationMode,
  OptimizationReportItem,
  OptimizationReportOverview,
  HostManagerSmartCoordinatorStatus,
  TrustedOptimizationTarget
} from "../types";
import { presentOptimizationReport } from "../presentation/optimizationPresentation";
import { UserDetailsDialog } from "./UserDetailsDialog";
import {
  observationCanRender,
  type ObservationState
} from "../observation/observationState";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "./ObservationStateNotice";
import { SegmentedControl } from "../ui/primitives/SegmentedControl.tsx";
import { uiText } from "../text.ts";
import { optimizationEvidenceValue } from "../presentation/optimizationPresentation";
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
  const allReports = () => props.overview?.reports ?? [];
  const trustedReports = () => allReports().filter(isTrustedReport);
  const untrustedReports = () => allReports().filter((report) => !isTrustedReport(report));
  const reports = () => {
    const filter = reportFilter();
    if (filter === "trusted") {
      return trustedReports();
    }
    if (filter === "all") {
      return allReports();
    }
    return untrustedReports();
  };
  const visibleReports = createMemo(() => filterOptimizationReports(reports(), searchQuery()));
  const selectedOptimizationMode = () => props.pendingOptimizationMode ?? props.optimizationMode;
  const reportsAvailable = () => observationCanRender(props.reportsObservation);
  const reportActionsAvailable = () => props.reportsObservation.status === "ready";
  const hostManagerAvailable = () => props.hostManagerObservation.status === "ready";
  const hostManagerStatusText = () => {
    if (props.pendingOptimizationMode) {
      return uiText.optimization.modeApplyCountdown(Math.max(1, props.optimizationModeApplyRemainingSeconds), optimizationModeLabel(props.pendingOptimizationMode));
    }

    const state = props.hostManagerStatus;
    if (!state) {
      return uiText.optimization.scheduleStateUnknown;
    }

    const modeStatus = optimizationModeLabel(state.mode);
    return [
      modeStatus,
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
      />
      <nav
        {...frontendVisibilitySurface(
          "visible.optimization.mode-status.surface",
          [modeDemandId])}
        class="optimization-modebar"
        aria-label={uiText.optimization.modeBar}
      >
        <div class="optimization-mode-group" role="group" aria-label={uiText.optimization.modeBar}>
          <button
            type="button"
            class="optimization-mode"
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
                class="optimization-mode"
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
          <span class="optimization-mode-status">
            {props.actionId === "smart-mode" ? uiText.optimization.modeSwitching : hostManagerStatusText()}
          </span>
        </div>
      </nav>

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
                { id: "trusted", label: uiText.optimization.filterTrusted(String(reportsAvailable() ? trustedReports().length : "--")) }
              ] satisfies Array<{ id: ReportFilter; label: string }>}
              ariaLabel={uiText.optimization.filterLabel}
              class="optimization-report-filter"
              onChange={setReportFilter}
            />
            <button class="secondary" type="button" disabled={props.loading} onClick={props.onRefresh}>
              {props.loading ? uiText.optimization.refreshing : uiText.optimization.refresh}
            </button>
          </div>
        </div>

        <ObservationStateBoundary
          state={props.reportsObservation}
          label={uiText.optimization.reportPanel}
        >
          <Show
            when={visibleReports().length > 0}
            fallback={<div class="optimization-empty">{reports().length > 0 ? uiText.optimization.noSearchResults : uiText.optimization.empty}</div>}
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
                    class={`optimization-report severity-${report.severity.toLowerCase()}`}
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
                      <Show when={report.target.targetType === "Software"}>
                        <button class="secondary" type="button" onClick={() => props.onInspect(report)}>{uiText.optimization.viewSoftware}</button>
                      </Show>
                      <Show when={["Device", "Display", "PhysicalDisk"].includes(report.target.targetType)}>
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
    </section>
  );
}

function isTrustedReport(report: OptimizationReportItem) {
  return report.state === "TrustedSuppressed";
}

function findTrustedTarget(
  targets: readonly TrustedOptimizationTarget[],
  report: OptimizationReportItem
) {
  return targets.find((target) =>
    target.targetType === report.target.targetType
    && target.targetKey === report.target.targetKey
    && (report.target.targetType === "Software"
      || report.target.targetType === "Drive"
      || target.reportType === advisoryTrustScope(report.type)));
}

function advisoryTrustScope(reportType: string) {
  switch (reportType) {
    case "PowerProfileNotPerformanceFocused": return "power-profile";
    case "ExternalDisplayLinkCapabilityGap": return "display-link";
    case "DeviceDriverProblem": return "device-driver";
    case "ExternalDiskDuplicateSecurityScan": return "disk-security-scan";
    case "ExternalDiskIdleTimeoutTooShort": return "disk-power";
    case "PhysicalDiskLatencyHigh": return "disk-latency";
    case "PhysicalDiskHealthWarning": return "disk-health";
    case "ExternalDiskLinkCapabilityGap": return "disk-link";
    case "VolumeFragmentationHigh": return "disk-fragmentation";
    case "CpuSustainedThermalThrottling": return "cpu-thermal";
    case "SystemInterruptPressure": return "system-interrupt";
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
    report.target.displayName,
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
    ...(report.evidence.details ?? []));
}

function normalizeSearchQuery(value: string) {
  return value.trim().toLocaleLowerCase();
}

function matchesSearch(query: string, ...values: unknown[]) {
  return values.some((value) => String(value ?? "").toLocaleLowerCase().includes(query));
}
