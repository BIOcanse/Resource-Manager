import { createEffect, createMemo, For, on, Show } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { softwareRecordName } from "../../../presentation/softwareFacts.ts";
import { userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { uiText } from "../../../text.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type { PerformanceOverlaySettings, SoftwareRecord } from "../../../types.ts";
import { ContentState } from "../../../ui/patterns/ContentState.tsx";

interface PerformanceOverlaySettingsSectionProps {
  text: SettingsTextBundle;
  /** Overlay editing requires writable persistence. */
  overlayEditingAvailable: boolean;
  /** Refresh after the software-details dialog closes. */
  softwareDetailOpen: boolean;
  onOpenOverlay: (software: SoftwareRecord) => void;
}

interface OverlayRow {
  readonly settings: PerformanceOverlaySettings;
  /** If software disappears, show its ID and disable details. */
  readonly software: SoftwareRecord | null;
}

/* List enabled saved overlay settings and module registrations; these are configuration facts, not live injection state. Edit through software details. */
export function PerformanceOverlaySettingsSection(props: PerformanceOverlaySettingsSectionProps) {
  const copy = () => props.text.performanceOverlay;
  const runtime = useFrontendRuntime();
  // Visible demand owns software and overlay subscriptions and cancels pending reads on exit.
  // Retain the last accepted source values across section reconstruction.
  const work = useFrontendWork();
  const workId = frontendWorkIds.settingsPerformanceOverlay;
  useFrontendVisibilityDemand(workId, [workId]);
  const needed = () => work.isNeeded(workId);
  const softwareSource = useSource(runtime.sources.managementSoftware, () => ({ active: needed() }));
  const overlaysSource = useSource(runtime.sources.performanceOverlays, () => ({ active: needed() }));
  // After details close, wait for any earlier read before refreshing saved changes.
  // The modal details dialog prevents navigation away while open.
  createEffect(on(() => props.softwareDetailOpen, (open) => {
    if (!open && needed()) void overlaysSource.refresh({ afterCurrent: true });
  }, { defer: true }));
  const overlaysSnapshot = () => overlaysSource.snapshot();
  const overlaysFailed = () => overlaysSnapshot().data === null
    && (overlaysSnapshot().status === "error" || overlaysSnapshot().status === "unavailable");

  const softwareSnapshot = () => softwareSource.snapshot();
  const softwareFailed = () => softwareSnapshot().data === null
    && (softwareSnapshot().status === "error" || softwareSnapshot().status === "unavailable");
  const rows = createMemo<readonly OverlayRow[] | null>(() => {
    const current = overlaysSnapshot().data;
    const software = softwareSnapshot().data;
    if (!current || !software) return null;
    return current.map((settings) => ({
      settings,
      software: software.find((record) => record.id.toLowerCase() === settings.softwareId.toLowerCase()) ?? null
    }));
  });
  const failure = () => overlaysFailed()
    ? userFacingErrorMessage(overlaysSnapshot().error, copy().loadFailed)
    : softwareFailed() ? userFacingErrorMessage(softwareSnapshot().error, copy().loadFailed) : null;
  const retry = () => {
    if (overlaysFailed()) void overlaysSource.refresh();
    if (softwareFailed()) void softwareSource.refresh();
  };
  const summary = (settings: PerformanceOverlaySettings) => [
    settings.mode === "injected" ? uiText.performanceOverlay.injected : uiText.performanceOverlay.external,
    copy().itemCount(settings.items.length),
    copy().position(anchorLabel(settings.anchor))
  ].join(" · ");

  return (
    <div class="settings-section-panel performance-overlay-settings" {...frontendVisibilitySurface(workId, [workId])}>
      <p>{copy().intro}</p>
      <Show
        when={failure() === null}
        fallback={<ContentState kind="unavailable" title={copy().loadFailed}
          detail={failure() !== copy().loadFailed ? failure() ?? undefined : undefined}
          actions={<button type="button" class="secondary" onClick={retry}>{props.text.actions.retry}</button>} />}
      >
        <Show when={rows()} fallback={<ContentState kind="waiting" title={copy().loading} />}>
          {(current) => (
            <Show
              when={current().length > 0}
              fallback={<ContentState kind="empty" title={copy().emptyTitle} detail={copy().emptyDetail(uiText.performanceOverlay.tab)} />}
            >
              <For each={current()}>
                {(row) => {
                  const name = () => row.software ? softwareRecordName(row.software) : row.settings.softwareId;
                  const disabledReason = () => !row.software ? copy().softwareMissing
                    : !props.overlayEditingAvailable ? uiText.softwareActions.policyEditUnavailable : undefined;
                  return (
                    <div class="settings-row performance-overlay-row" data-software-id={row.settings.softwareId}>
                      <div class="settings-row-copy">
                        <strong data-full-content={name()}>{name()}</strong>
                        <span>{summary(row.settings)}</span>
                        {/* Explain unavailable details beside the disabled button. */}
                        <Show when={disabledReason()}>{(reason) => <span role="note">{reason()}</span>}</Show>
                      </div>
                      <button
                        type="button"
                        class="secondary"
                        aria-label={copy().openLabel(name())}
                        disabled={disabledReason() !== undefined}
                        onClick={() => row.software && props.onOpenOverlay(row.software)}
                      >
                        {copy().open}
                      </button>
                    </div>
                  );
                }}
              </For>
            </Show>
          )}
        </Show>
      </Show>
    </div>
  );
}

function anchorLabel(anchor: PerformanceOverlaySettings["anchor"]) {
  switch (anchor) {
    case "topLeft": return uiText.performanceOverlay.topLeft;
    case "topRight": return uiText.performanceOverlay.topRight;
    case "bottomLeft": return uiText.performanceOverlay.bottomLeft;
    case "bottomRight": return uiText.performanceOverlay.bottomRight;
  }
}
