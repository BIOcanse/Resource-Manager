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
  /** 软件详细信息里能不能编辑浮层（浮层分页只在可写持久化时出现）。 */
  overlayEditingAvailable: boolean;
  /** 软件详细信息对话框是否开着：关上时重新读一次，那里保存的改动随即出现在这里。 */
  softwareDetailOpen: boolean;
  onOpenOverlay: (software: SoftwareRecord) => void;
}

interface OverlayRow {
  readonly settings: PerformanceOverlaySettings;
  /** 软件列表里对应的软件；列表里已经没有时为 null，名称写软件 ID，不能打开详细信息。 */
  readonly software: SoftwareRecord | null;
}

/*
 * 设置 → 性能浮层：哪些软件启用了浮层。
 * 数据只用前端已有的：浮层前端偏好，加上浮层分页本来就读的启动模块登记（getPerformanceOverlaySettings，同一读法），
 * 只列启用的。没有运行状态（是否找到窗口、是否已注入），这里写明显示的是保存的设置。
 * 修改在软件详细信息的浮层分页里做：每一行的按钮直接打开那个分页。
 */
export function PerformanceOverlaySettingsSection(props: PerformanceOverlaySettingsSectionProps) {
  const copy = () => props.text.performanceOverlay;
  const runtime = useFrontendRuntime();
  // 软件列表和浮层设置都是数据来源，随这个分区的需求订阅：进入时读，退出时退订并取消没读完的那次。
  // 退出后来源保留最后接受的值，分区重建时第一帧就有。
  const work = useFrontendWork();
  const workId = frontendWorkIds.settingsPerformanceOverlay;
  useFrontendVisibilityDemand(workId, [workId]);
  const needed = () => work.isNeeded(workId);
  const softwareSource = useSource(runtime.sources.managementSoftware, () => ({ active: needed() }));
  const overlaysSource = useSource(runtime.sources.performanceOverlays, () => ({ active: needed() }));
  // 软件详细信息关上时再读一次（同一读法），那里保存的改动随即出现在这里；等此前的读取结束再读，读到的是保存后的值。
  // 详细信息是盖住整页的对话框，开着时不能离开这一页。
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
    copy().metricCount(settings.metrics.length),
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
                        {/* 打不开的原因写在这一行的说明里（禁用的按钮收不到指针）。 */}
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
