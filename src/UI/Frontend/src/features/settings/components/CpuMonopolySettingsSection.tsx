import { For, Show, type JSX } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { usePushSource } from "../../../frontendRuntime/push/usePushSource.ts";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import {
  cpuMonopolySettingRanges, type CpuMonopolyLiveStatus, type CpuMonopolyProtectionSettings
} from "../../../data/cpuMonopolyProtection/cpuMonopolyProtection.ts";
import { ContentState } from "../../../ui/patterns/ContentState.tsx";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { uiText } from "../../../text.ts";
import { cpuMonopolyExclusionReasonText, cpuMonopolyInactiveReasonText } from "../../cpuMonopoly/cpuMonopolyText.ts";
import { NumberField } from "./SettingsControls";

interface CpuMonopolySettingsSectionProps {
  /** Edit CPU protection draft fields and preserve other backend fields when saving. */
  settings: CpuMonopolyProtectionSettings | null | undefined;
  /** Compare against saved settings to detect draft changes. */
  committed: CpuMonopolyProtectionSettings | null | undefined;
  runtimeFailureCode: string | null;
  onChange: (settings: CpuMonopolyProtectionSettings) => void;
}

/* Defaults remain backend-owned, so omit reset. Runtime state is read-only from the shared subscription; edit the shared whitelist in memory protection. */
export function CpuMonopolySettingsSection(props: CpuMonopolySettingsSectionProps) {
  const copy = () => uiText.cpuMonopoly;
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  const workId = frontendWorkIds.settingsCpuMonopolyProtection;
  useFrontendVisibilityDemand(workId, [workId]);
  const protection = usePushSource(runtime.sources.cpuMonopolyProtection, () => ({ active: work.isNeeded(workId) }));
  const status = () => protection.snapshot().data?.status ?? null;
  const unavailable = () => protection.snapshot().status === "error" || protection.snapshot().status === "unavailable";
  const unsaved = () => JSON.stringify(props.settings ?? null) !== JSON.stringify(props.committed ?? null);
  // Global type rejections belong in save status; show other rejections only for this section's unsaved changes.
  const rejection = () => {
    const code = props.runtimeFailureCode;
    return code && code !== "invalid-settings-value-type" && unsaved() ? code : null;
  };
  const update = (patch: Partial<CpuMonopolyProtectionSettings>) => {
    if (props.settings) props.onChange({ ...props.settings, ...patch });
  };
  const seconds = cpuMonopolySettingRanges.seconds;

  return (
    <div class="settings-section-panel cpu-monopoly-settings" {...frontendVisibilitySurface(workId, [workId])}>
      <p>{copy().intro}</p>
      <p class="cpu-monopoly-note">{copy().whitelistNote}</p>
      <Show when={props.settings} fallback={<ContentState kind="unavailable" title={copy().notProvided} />}>
        {(settings) => (
          <>
            <Show when={unsaved()}><p role="status">{copy().unsavedNote}</p></Show>
            <Show when={rejection()}>{(code) => <p role="alert" data-full-content={code()}>{copy().rejected(code())}</p>}</Show>
            {/* Keep status geometry while loading; failed status reads do not block settings edits. */}
            <Show when={!status()}>
              <Show when={unavailable()} fallback={<p class="sr-only" role="status">{copy().statusLoading}</p>}>
                <ContentState kind="unavailable" title={copy().statusUnavailable} detail={copy().statusUnavailableDetail} />
              </Show>
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().enabledTitle}</strong>
                <span>{copy().enabledDescription}</span>
              </div>
              <SwitchControl ariaLabel={copy().enabledTitle} checked={settings().enabled} onChange={(enabled) => update({ enabled })} />
            </div>
            <Show when={status() || !unavailable()}>
              <CpuMonopolyState status={status()} />
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().thresholdTitle}</strong>
                <span>{copy().thresholdDescription}</span>
              </div>
              <NumberField label={copy().thresholdLabel} ariaLabel={copy().thresholdTitle} value={settings().thresholdPercent}
                min={cpuMonopolySettingRanges.thresholdPercent.minimum} max={cpuMonopolySettingRanges.thresholdPercent.maximum}
                step={0.1} onChange={(thresholdPercent) => update({ thresholdPercent })} />
            </div>
            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().durationTitle}</strong>
                <span>{copy().durationDescription}</span>
              </div>
              <NumberField label={copy().durationLabel} ariaLabel={copy().durationTitle} value={settings().sustainedSeconds}
                min={seconds.minimum} max={seconds.maximum} step={0.1} onChange={(sustainedSeconds) => update({ sustainedSeconds })} />
            </div>
            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().intervalTitle}</strong>
                <span>{copy().intervalDescription}</span>
              </div>
              <NumberField label={copy().intervalLabel} ariaLabel={copy().intervalTitle} value={settings().checkIntervalSeconds}
                min={seconds.minimum} max={seconds.maximum} step={0.1} onChange={(checkIntervalSeconds) => update({ checkIntervalSeconds })} />
            </div>
          </>
        )}
      </Show>
    </div>
  );
}

/** Preserve the state, active-action, and exclusion layout before status arrives. */
function CpuMonopolyState(props: { status: CpuMonopolyLiveStatus | null }) {
  const copy = () => uiText.cpuMonopoly;
  return (
    <div class="settings-row settings-row-block cpu-monopoly-state">
      <div class="settings-row-copy" role="status">
        <strong>{copy().statusTitle}</strong>
        <Show when={props.status} fallback={
          <>
            <span>--</span>
            <dl class="memory-protection-facts">
              <Fact label={copy().currentAction}>--</Fact>
              <Fact label={copy().exclusionsTitle} wide>--</Fact>
            </dl>
          </>
        }>
          {(current) => (
            <>
              <span class="cpu-monopoly-effective" data-active={String(current().active)}
                data-full-content={current().inactiveReason ?? undefined}>
                {current().active ? copy().stateActive
                  : copy().stateInactive(cpuMonopolyInactiveReasonText(current().inactiveReason ?? "disabled"))}
              </span>
              <dl class="memory-protection-facts">
                <Fact label={copy().currentAction}>
                  {current().currentActionId === null ? "--" : <code class="memory-protection-path">{current().currentActionId}</code>}
                </Fact>
                <Fact label={copy().exclusionsTitle} wide>
                  <Show when={current().excludedCandidates.length > 0} fallback={copy().exclusionsEmpty}>
                    <ul class="cpu-monopoly-exclusions">
                      <For each={current().excludedCandidates}>
                        {(entry) => (
                          <li data-reason={entry.reason}>
                            {copy().process(entry.name, entry.processId)} · {cpuMonopolyExclusionReasonText(entry.reason)}
                          </li>
                        )}
                      </For>
                    </ul>
                  </Show>
                </Fact>
              </dl>
            </>
          )}
        </Show>
      </div>
    </div>
  );
}

function Fact(props: { label: string; wide?: boolean; children: JSX.Element }) {
  return (
    <div class="memory-protection-fact" classList={{ wide: props.wide === true }}>
      <dt>{props.label}</dt>
      <dd>{props.children}</dd>
    </div>
  );
}
