import { createEffect, createMemo, createSignal } from "solid-js";
import type { Accessor } from "solid-js";
import {
  dismissOptimizationReport,
  getHostManagerSmartCoordinatorStatus,
  removeOptimizationTrust,
  setHostManagerSmartCoordinatorMode
} from "../api";
import { refreshOptimizationReports } from
  "../data/optimization/optimizationReportsApi.ts";
import type { RequestClient } from
  "../frontendRuntime/request/RequestClient.ts";
import type { SourceHandle } from
  "../frontendRuntime/source/SourceDescriptor.ts";
import {
  sourceCanRender,
  type SourceSnapshot
} from "../frontendRuntime/source/SourceSnapshot.ts";
import type { ToastInput } from "../components/AppFeedback";
import { uiText } from "../text.ts";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import {
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import type {
  AppOptimizationMode,
  OptimizationReportItem,
  OptimizationReportOverview,
  HostManagerSmartCoordinatorStatus,
  TrustedOptimizationTarget
} from "../types";

const optimizationModeApplyDelayMs = 3000;

export interface OptimizationStoreOptions {
  showToast: (input: ToastInput) => void;
  enabled: Accessor<boolean>;
  reportSource: SourceHandle<OptimizationReportOverview>;
  requestClient: Pick<RequestClient, "request">;
}

export interface OptimizationStore {
  overview: Accessor<OptimizationReportOverview | null>;
  reportsObservation: Accessor<ObservationState>;
  hostManagerStatus: Accessor<HostManagerSmartCoordinatorStatus | null>;
  hostManagerObservation: Accessor<ObservationState>;
  optimizationMode: Accessor<AppOptimizationMode>;
  loading: Accessor<boolean>;
  actionId: Accessor<string | null>;
  pendingOptimizationMode: Accessor<AppOptimizationMode | null>;
  optimizationModeApplyRemainingSeconds: Accessor<number>;
  changeHostManagerSmartCoordinatorMode: (mode: AppOptimizationMode) => Promise<void>;
  refreshReports: (userInitiated: boolean) => Promise<void>;
  refreshHostManagerStatus: () => Promise<void>;
  dismissReport: (report: OptimizationReportItem) => Promise<void>;
  removeTrustedTarget: (target: TrustedOptimizationTarget) => Promise<void>;
  dispose: () => void;
}

export function createOptimizationStore(options: OptimizationStoreOptions): OptimizationStore {
  const reportSource = options.reportSource.acquire({
    active: false,
    refreshIntervalMs: null
  });
  const [reportSnapshot, setReportSnapshot] = createSignal(reportSource.snapshot);
  const unsubscribeReportSource = reportSource.subscribe(setReportSnapshot);
  const overview = createMemo<OptimizationReportOverview | null>(() => {
    const snapshot = reportSnapshot();
    return options.enabled() && sourceCanRender(snapshot)
      ? snapshot.data
      : null;
  });
  const reportsObservation = createMemo<ObservationState>(() =>
    reportSourceObservation(options.enabled(), reportSnapshot()));
  const [hostManagerStatus, setHostManagerStatus] = createSignal<HostManagerSmartCoordinatorStatus | null>(null);
  const [hostManagerObservation, setHostManagerObservation] =
    createSignal<ObservationState>(options.enabled()
      ? loadingObservation()
      : profileDisabledObservation(
        uiText.stores.optimizationSchedulingDisabled));
  const optimizationMode = createMemo<AppOptimizationMode>(() =>
    hostManagerStatus()?.mode ?? "normal");
  const [loading, setLoading] = createSignal(false);
  const [actionId, setActionId] = createSignal<string | null>(null);
  const [pendingOptimizationMode, setPendingOptimizationMode] = createSignal<AppOptimizationMode | null>(null);
  const [optimizationModeApplyRemainingSeconds, setOptimizationModeApplyRemainingSeconds] = createSignal(0);
  let optimizationModeApplyTimer: number | undefined;
  let optimizationModeCountdownTimer: number | undefined;

  createEffect(() => {
    reportSource.setDemand({
      active: options.enabled(),
      refreshIntervalMs: null
    });
  });

  async function changeHostManagerSmartCoordinatorMode(mode: AppOptimizationMode) {
    if (!options.enabled()) {
      clearPendingOptimizationMode();
      return;
    }

    const currentMode = optimizationMode();
    if (mode === currentMode) {
      clearPendingOptimizationMode();
      return;
    }

    if (pendingOptimizationMode() !== mode) {
      scheduleOptimizationModeApply(mode);
    }
  }

  async function applyHostManagerSmartCoordinatorMode(mode: AppOptimizationMode) {
    if (!options.enabled() || actionId()) {
      return;
    }

    setActionId("smart-mode");
    try {
      const status = await setHostManagerSmartCoordinatorMode(mode);
      setHostManagerStatus(status);
      setHostManagerObservation(readyObservation());
      await refreshReports(false);
    } catch (error) {
      showErrorToast(error, uiText.stores.smartModeSwitchFailed);
    } finally {
      setActionId(null);
    }
  }

  function scheduleOptimizationModeApply(mode: AppOptimizationMode) {
    clearPendingOptimizationMode();
    setPendingOptimizationMode(mode);
    setOptimizationModeApplyRemainingSeconds(3);

    const targetAt = Date.now() + optimizationModeApplyDelayMs;
    optimizationModeCountdownTimer = window.setInterval(() => {
      const remainingSeconds = Math.max(0, Math.ceil((targetAt - Date.now()) / 1000));
      setOptimizationModeApplyRemainingSeconds(remainingSeconds);
    }, 250);

    optimizationModeApplyTimer = window.setTimeout(() => {
      void applyPendingOptimizationMode(mode);
    }, optimizationModeApplyDelayMs);
  }

  async function applyPendingOptimizationMode(mode: AppOptimizationMode) {
    if (pendingOptimizationMode() !== mode) {
      return;
    }

    if (actionId()) {
      scheduleOptimizationModeApply(mode);
      return;
    }

    clearPendingOptimizationMode();
    await applyHostManagerSmartCoordinatorMode(mode);
  }

  function clearPendingOptimizationMode() {
    if (optimizationModeApplyTimer !== undefined) {
      window.clearTimeout(optimizationModeApplyTimer);
      optimizationModeApplyTimer = undefined;
    }

    if (optimizationModeCountdownTimer !== undefined) {
      window.clearInterval(optimizationModeCountdownTimer);
      optimizationModeCountdownTimer = undefined;
    }

    setPendingOptimizationMode(null);
    setOptimizationModeApplyRemainingSeconds(0);
  }

  async function refreshReports(userInitiated: boolean) {
    if (!options.enabled()) {
      return;
    }

    if (loading()) {
      return;
    }

    setLoading(true);
    try {
      if (userInitiated) {
        await refreshOptimizationReports(options.requestClient);
        await reportSource.refresh();
      } else {
        await reportSource.refresh();
      }
    } catch (error) {
      if (userInitiated) {
        showErrorToast(error, uiText.stores.optimizationReportRefreshFailed);
      }
    } finally {
      setLoading(false);
    }
  }

  async function refreshHostManagerStatus() {
    if (!options.enabled()) {
      clearPendingOptimizationMode();
      setHostManagerStatus(null);
      setHostManagerObservation(profileDisabledObservation(
        uiText.stores.optimizationSchedulingDisabled));
      return;
    }

    setHostManagerObservation(refreshingObservation);
    try {
      const status = await getHostManagerSmartCoordinatorStatus();
      setHostManagerStatus(status);
      setHostManagerObservation(readyObservation());
      if (pendingOptimizationMode() === status.mode) {
        clearPendingOptimizationMode();
      }
    } catch (error) {
      setHostManagerObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, uiText.stores.optimizationScheduleRefreshFailed)));
    }
  }

  async function dismissReport(report: OptimizationReportItem) {
    await runReportAction(report.id, async () => {
      await dismissOptimizationReport(report.id);
      await refreshReports(false);
    });
  }

  async function removeTrustedTarget(target: TrustedOptimizationTarget) {
    await runReportAction(target.id, async () => {
      await removeOptimizationTrust(target.id);
      await refreshReports(false);
    });
  }

  async function runReportAction(targetId: string, action: () => Promise<void>) {
    if (!options.enabled() || actionId()) {
      return;
    }

    setActionId(targetId);
    try {
      await action();
    } catch (error) {
      showErrorToast(error, uiText.stores.actionFailed);
    } finally {
      setActionId(null);
    }
  }

  function showErrorToast(error: unknown, fallback: string) {
    options.showToast({
      tone: "error",
      title: uiText.feedback.error,
      message: userFacingErrorMessage(error, fallback)
    });
  }

  function dispose() {
    clearPendingOptimizationMode();
    unsubscribeReportSource();
    reportSource.release();
  }

  return {
    overview,
    reportsObservation,
    hostManagerStatus,
    hostManagerObservation,
    optimizationMode,
    loading,
    actionId,
    pendingOptimizationMode,
    optimizationModeApplyRemainingSeconds,
    changeHostManagerSmartCoordinatorMode,
    refreshReports,
    refreshHostManagerStatus,
    dismissReport,
    removeTrustedTarget,
    dispose
  };
}

function reportSourceObservation(
  enabled: boolean,
  snapshot: SourceSnapshot<OptimizationReportOverview>
): ObservationState {
  if (!enabled) {
    return profileDisabledObservation(uiText.stores.optimizationReportDisabled);
  }
  if ((snapshot.status === "ready" || snapshot.status === "refreshing")
    && snapshot.data) {
    return readyObservation(snapshot.capturedAt ?? snapshot.data.capturedAt);
  }
  if (snapshot.status === "stale" && snapshot.data) {
    return failedObservation(
      readyObservation(snapshot.capturedAt ?? snapshot.data.capturedAt),
      userFacingErrorMessage(snapshot.error, uiText.stores.optimizationReportRefreshFailed));
  }
  if (snapshot.status === "error"
    || snapshot.status === "unavailable"
    || snapshot.status === "disposed") {
    return failedObservation(
      loadingObservation(),
      userFacingErrorMessage(snapshot.error, uiText.stores.optimizationReportRefreshFailed));
  }
  return loadingObservation();
}
