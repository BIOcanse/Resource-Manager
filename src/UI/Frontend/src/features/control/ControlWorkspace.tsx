import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { HelpHint, OptionsHelpHint, type HelpHintEntry } from "../../ui/primitives/HelpHint.tsx";
import { controlReasonText, controlRequestErrorText } from "../../presentation/controlReasonText.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { batch, createMemo, createSignal, For, Match, Show, Switch } from "solid-js";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { useWhileNeeded } from "../../frontendWork/useWhileNeeded.ts";
import { Portal } from "solid-js/web";
import { CircleDashed, Download, Lock, X } from "lucide-solid";
import {
  acknowledgeControlNotice,
  setControlObjectSettings,
  deleteControlPreset,
  forgetControlInstance,
  retryControlObject,
  redetectControlPlatform,
  setControlOverclockConsent,
  refreshControlInstances,
  saveControlPreset
} from "./controlApi.ts";
import {
  ControlCapabilityEditor,
  actualRepeatsNumberEditor,
  formatActualNumber,
  initialSettingFor
} from "./ControlCapabilityEditor";
import { ControlFirstRunNotice } from "./ControlFirstRunNotice";
import { SettingsPanel } from "../settings/SettingsPanel.tsx";
import { controlItemTone } from "../../presentation/statusTone.ts";
import { controlCapabilityLabel, controlChannelLabel, controlDisplayText, controlFanGpuName, controlOutcomeReason, controlObjectName } from "./controlPresentation.ts";
import { GpuNameText } from "../monitor/GpuNameNote.tsx";
import {
  controlItemStatusOf,
  canRetryControlObject,
  hasPendingChanges
} from "./controlStateMachine.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { acceptSourceCommand } from "../../frontendRuntime/source/acceptSourceCommand.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { RadioGroupRoot, RadioGroupItem } from "../../ui/primitives/RadioGroup";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { uiText } from "../../text.ts";
import { currentLanguage } from "../../i18n/appTextStore.ts";
import { controlAccessLevelAllows } from "./controlTypes.ts";
import type {
  ControlAccessLevel,
  ControlActualState,
  ControlActualValue,
  ControlCapability,
  ControlPreset,
  ControlObject,
  ControlObjectDesiredState,
  ControlStateView,
  ControlInstanceCatalog,
  ControlSetting
} from "./controlTypes.ts";

/** The hardware section shares the settings draft; discard unsaved edits on leaving this page. */
export function ControlWorkspace(props: { onNotice: (message: string) => void; discardSettingsDraft: () => void }) {
  onPageLeave(() => props.discardSettingsDraft());
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const frontendWork = useFrontendWork();
  useFrontendVisibilityDemand("control.workspace", [frontendWorkIds.controlActual]);
  const visible = () => frontendWork.isNeeded(frontendWorkIds.controlActual);
  const demand = () => ({ active: visible(), refreshIntervalMs: null });
  const objectsSource = useSource(runtime.sources.control.objects, demand);
  const stateSource = useSource(runtime.sources.control.state, demand);
  const instancesSource = useSource(runtime.sources.control.instances, demand);
  const presetsSource = useSource(runtime.sources.control.presets, demand);
  const consentSource = useSource(runtime.sources.control.overclockConsent, demand);
  const accessSource = useSource(runtime.sources.control.accessLevel, demand);
  const noticeSource = useSource(runtime.sources.control.notice, demand);
  const sources = [objectsSource, stateSource, instancesSource, presetsSource, consentSource, accessSource, noticeSource];
  const reloadState = () => { for (const source of sources) void source.refresh(); };
  // Listen for foreground refresh only while visible; resuming demand refreshes the source.
  useWhileNeeded(frontendWorkIds.controlActual, () => {
    window.addEventListener("focus", reloadState);
    return () => window.removeEventListener("focus", reloadState);
  });
  const catalog = () => objectsSource.snapshot().data;
  const state = () => stateSource.snapshot().data;
  const instances = () => instancesSource.snapshot().data;
  const presets = () => presetsSource.snapshot().data;
  const failed = () => ["error", "unavailable"].includes(objectsSource.snapshot().status);
  const stateLoad = () => sourceCanRender(stateSource.snapshot()) ? "ready"
    : ["error", "unavailable"].includes(stateSource.snapshot().status) ? "error" : "loading";
  const overclockAccepted = () => consentSource.snapshot().data;
  const accessLevel = () => accessSource.snapshot().data;
  const noticeSeen = () => noticeSource.snapshot().data;
  const noticeReadFailed = () => ["error", "unavailable"].includes(noticeSource.snapshot().status);
  const [managingInstances, setManagingInstances] = createSignal(false);
  const [noticeAckFailed, setNoticeAckFailed] = createSignal(false);
  const [acknowledging, setAcknowledging] = createSignal(false);
  const [consentBusy, setConsentBusy] = createSignal(false);
  const reportFailure = (problem: unknown, fallback: string) => props.onNotice(
    problem instanceof RequestProblem ? controlRequestErrorText(problem.payload) ?? fallback : fallback);

  /** Disable rediscovery while running to prevent duplicate subprocesses. */
  const [detecting, setDetecting] = createSignal(false);

  /** A page-owned draft survives catalog refreshes and stays local until apply; apply and undo operate per object. */
  const [draft, setDraft] = createSignal<readonly ControlObjectDesiredState[] | null>(null);
  // An explicit curve selection can reapply a revised library entry with the same ID.
  // This is pending user intent, not a locally invented hardware state.
  const [curveSelections, setCurveSelections] = createSignal<ReadonlyMap<string, ReadonlySet<string>>>(new Map());
  const clearCurveSelections = (objectId: string) => setCurveSelections(current => {
    const next = new Map(current); next.delete(objectId); return next;
  });
  // Unsaved ownership choice belongs to the page draft, not a refreshed device card.
  const [fanModes, setFanModes] = createSignal<ReadonlyMap<string, string>>(new Map());
  const chooseFanMode = (id: string, mode: string | null) => setFanModes(current => {
    const next = new Map(current); if (mode === null) next.delete(id); else next.set(id, mode); return next;
  });
  const [takingOver, setTakingOver] = createSignal<ReadonlySet<string>>(new Set());
  const chooseOwnership = (objectId: string, enabled: boolean) => setTakingOver(current => {
    const next = new Set(current);
    if (enabled) next.add(objectId);
    else next.delete(objectId);
    return next;
  });
  // Discard unapplied drafts on leave and reload persisted state on return.
  onPageLeave(() => batch(() => {
    setDraft(null);
    setFanModes(new Map<string, string>());
    setTakingOver(new Set<string>());
    setCurveSelections(new Map());
  }));
  const savedObjects = () => state()?.desired.objects ?? [];
  const edited = () => draft() ?? savedObjects();

  const settingsOf = (objectId: string) =>
    edited().find((entry) => entry.objectId === objectId)?.settings ?? [];

  const editCapability = (
    objectId: string,
    capabilityId: string,
    next: ControlSetting | null
  ) => {
    setCurveSelections(current => {
      const selections = new Set(current.get(objectId));
      if (next?.gpuVfCurvePresetId) selections.add(capabilityId); else selections.delete(capabilityId);
      const updated = new Map(current);
      if (selections.size) updated.set(objectId, selections); else updated.delete(objectId);
      return updated;
    });
    const rest = settingsOf(objectId)
      .filter((entry) => entry.capabilityId !== capabilityId);
    const settings = next ? [...rest, next] : rest;
    const others = edited().filter((entry) => entry.objectId !== objectId);
    // Omit empty objects to express firmware ownership.
    setDraft(settings.length > 0
      ? [...others, { objectId, settings }]
      : others);
  };

  const [saving, setSaving] = createSignal(false);
  const discard = (objectId: string) => {
    const saved = savedObjects().find(entry => entry.objectId === objectId);
    setDraft([...edited().filter(entry => entry.objectId !== objectId), ...(saved ? [saved] : [])]);
    chooseOwnership(objectId, false);
    chooseFanMode(objectId, null);
    clearCurveSelections(objectId);
  };

  /** Observed hardware values remain separate from desired settings. */
  const actualSource = usePushSource(runtime.sources.controlActualState, () => ({
    active: visible(), intervalMs: readIntervals.current()?.monitor
  })).snapshot;
  const actual = () => actualSource().data;

  const apply = async (objectId: string) => {
    if (saving() || stateLoad() !== "ready") return;
    const submitted = settingsOf(objectId);
    const selectedCurves = curveSelections().get(objectId);
    const ownership = takingOver().has(objectId), fanMode = fanModes().get(objectId);
    setSaving(true);
    try {
      const changed = Boolean(selectedCurves?.size) || hasPendingChanges(submitted, savedObjects().find(entry => entry.objectId === objectId)?.settings ?? []);
      const next = await acceptSourceCommand(runtime.sources.control.state, () => changed
        ? setControlObjectSettings(runtime.requestClient, objectId, submitted)
        : retryControlObject(runtime.requestClient, objectId), value => value);
      // Preserve other drafts and edits made while this request was in flight.
      const current = edited();
      const unchanged = !hasPendingChanges(settingsOf(objectId), submitted);
      const saved = next.desired.objects.find(entry => entry.objectId === objectId);
      setDraft(unchanged
        ? [...current.filter(entry => entry.objectId !== objectId), ...(saved ? [saved] : [])]
        : current);
      if (curveSelections().get(objectId) === selectedCurves) clearCurveSelections(objectId);
      if (unchanged && takingOver().has(objectId) === ownership && fanModes().get(objectId) === fanMode) {
        chooseOwnership(objectId, false);
        chooseFanMode(objectId, null);
      }
    } catch (problem) {
      reportFailure(problem, uiText.control.saveFailed);
    } finally {
      setSaving(false);
    }
  };

  /** Only Intel integrated GPUs require this IGCL consent. */
  const hasIntelIntegratedGpu = () => (catalog()?.objects ?? []).some(
    (object) => object.kind === "gpu"
      && object.gpuAttachment === "integrated"
      && object.platform.vendor === "intel");

  // Key cards by domain identity to preserve drafts and focus during catalog refresh.
  // Refresh facts without replacing existing card DOM nodes.
  const objectById = createMemo(() => new Map((catalog()?.objects ?? []).map(object => [object.id, object])));
  const groups = () => ["gpu", "cpu", "fan"].filter(kind => (catalog()?.objects ?? []).some(object => object.kind === kind));
  const objectIds = (kind: string) => (catalog()?.objects ?? []).filter(object => object.kind === kind).map(object => object.id);
  const channels = () => [...new Set((catalog()?.objects ?? []).flatMap(object => object.capabilities
    .map(capability => capability.channel).filter((channel): channel is string => Boolean(channel))))]
    .filter(channel => uiText.controlPage.channels[channel]);
  return (
    <div class="page control-page" data-frontend-visibility-surface="control.workspace" data-frontend-visibility-demands="control.workspace">
      <Show when={noticeSeen() === false}>
        {/* Portal to body so page transforms cannot offset fixed-position dialogs. */}
        <Portal>
          <ControlFirstRunNotice busy={acknowledging()} failed={noticeAckFailed()} onAcknowledge={() => {
            if (acknowledging()) return;
            setAcknowledging(true);
            setNoticeAckFailed(false);
            void acceptSourceCommand(runtime.sources.control.notice,
              () => acknowledgeControlNotice(runtime.requestClient), () => true)
              .catch(() => setNoticeAckFailed(true))
              .finally(() => setAcknowledging(false));
          }} />
        </Portal>
      </Show>
      {/* Hardware control settings include persistence, reapply interval, and adjustment level. */}
      <SettingsPanel panel="hardwareControl" />
      <div class="panel">
        <div class="control-page-guide">
          <p>{uiText.controlPage.guide}</p>
          <Show when={channels().length}>
            <span class="with-help-hint control-channel-help">{uiText.controlPage.channelsTitle}
              <HelpHint label={uiText.controlPage.channelsTitle}>
                <p>{uiText.control.channelHint}</p>
                <For each={channels()}>{channel => <p class="help-hint-entry">
                  <strong>{controlChannelLabel(channel)}</strong>{uiText.controlPage.channels[channel]}
                </p>}</For>
              </HelpHint>
            </span>
          </Show>
        </div>
        <Show when={noticeReadFailed()}>
          <p role="alert">{uiText.control.recovery.noticeReadFailed}</p>
          <button type="button" class="secondary" onClick={reloadState}>{uiText.control.retryRead}</button>
        </Show>
        <Show when={stateLoad() !== "ready"}>
          <p role={stateLoad() === "error" ? "alert" : "status"}>{uiText.control.stateUnavailable}</p>
          <button type="button" class="secondary" onClick={reloadState}>{uiText.control.retryRead}</button>
        </Show>
        <Show when={accessLevel() === null}>
          <p role={accessSource.snapshot().status === "error" ? "alert" : "status"}>
            {accessSource.snapshot().status === "error" || accessSource.snapshot().status === "unavailable"
              ? uiText.control.recovery.accessReadFailed : uiText.control.recovery.accessLoading}
          </p>
          <button type="button" class="secondary" onClick={() => void accessSource.refresh()}>{uiText.control.retryRead}</button>
        </Show>
        <Show when={failed()}>
          <div class="disk-usage-warning" role="note">
            <strong>{uiText.control.loadFailed}</strong>
            <button type="button" class="secondary" onClick={reloadState}>{uiText.control.retryRead}</button>
          </div>
        </Show>

        <Show when={catalog() && groups().length === 0 && !failed()}>
          <p class="control-empty">{uiText.control.empty}</p>
        </Show>

        {/* Show Intel IGCL consent only for Intel integrated GPUs, including withdrawal after acceptance. */}
        <Show when={overclockAccepted() !== null && hasIntelIntegratedGpu()}>
          <section class="control-overclock-consent">
            <strong>{uiText.control.overclock.title}</strong>
            <p class="control-capability-reason">{uiText.control.overclock.body}</p>
            <div class="control-overclock-consent-actions">
              <Show when={overclockAccepted()}>
                <span class="control-capability-tag" data-tone="neutral">
                  {uiText.control.overclock.accepted}
                </span>
              </Show>
              <button
                class="secondary"
                type="button"
                disabled={consentBusy()}
                onClick={() => {
                  setConsentBusy(true);
                  void acceptSourceCommand(runtime.sources.control.overclockConsent, () => setControlOverclockConsent(
                  runtime.requestClient,
                  overclockAccepted() !== true), value => value)
                  .catch(problem => reportFailure(problem, uiText.control.saveFailed))
                  .finally(() => setConsentBusy(false));
                }}
              >
                <StableLabel
                  labels={{ accept: uiText.control.overclock.accept, revoke: uiText.control.overclock.revoke }}
                  current={overclockAccepted() ? "revoke" : "accept"}
                />
              </button>
            </div>
          </section>
        </Show>

        {/* Group infrequent page actions as compact secondary buttons. */}
        <div class="control-toolbar">
          {/* Explicit rediscovery refreshes cached hardware capabilities after component or device changes. */}
          <button
            class="panel-refresh-button secondary"
            type="button"
            disabled={detecting()}
            onClick={() => {
              setDetecting(true);
              void acceptSourceCommand(runtime.sources.control.objects,
                () => redetectControlPlatform(runtime.requestClient), value => value)
                .catch(() => props.onNotice(uiText.control.loadFailed))
                .finally(() => setDetecting(false));
            }}
          >
            <StableLabel labels={{ idle: uiText.control.detect, busy: uiText.control.detecting }} current={detecting() ? "busy" : "idle"} />
          </button>

          {/* Keep instance management collapsed because active devices already have cards. */}
          <button
            class="panel-refresh-button secondary"
            type="button"
            aria-expanded={managingInstances()}
            onClick={() => setManagingInstances((open) => !open)}
          >
            {uiText.control.instances.open}
          </button>
        </div>
        <Show when={managingInstances()}>
          <ControlInstancesPanel
            catalog={instances()}
            onRefresh={() => acceptSourceCommand(runtime.sources.control.instances,
              () => refreshControlInstances(runtime.requestClient), value => value)
              .then(() => undefined).catch(problem => reportFailure(problem, uiText.control.loadFailed))}
            onForget={(instanceId) => acceptSourceCommand(runtime.sources.control.instances, () => forgetControlInstance(
              runtime.requestClient,
              instanceId), value => value)
              // Report deletion failures as transient action feedback.
              .then(() => undefined).catch(problem => reportFailure(problem, uiText.control.forgetFailed))}
          />
        </Show>

        <Show when={stateLoad() === "ready"}>
        <For each={groups()}>
          {(kind) => (
            <section class="control-group">
              <h3>{uiText.control.kind[kind as keyof typeof uiText.control.kind] ?? kind}</h3>
              <div class="control-object-grid">
                <For each={objectIds(kind)}>
                  {(objectId) => (
                    <ControlObjectCard
                      object={objectById().get(objectId)!}
                      gpuName={controlFanGpuName(objectById().get(objectId)!, catalog()?.objects ?? [])}
                      fanModeChoice={fanModes().get(objectId)}
                      onFanModeChoice={(mode) => chooseFanMode(objectId, mode)}
                      takingOver={takingOver().has(objectId)}
                      onTakeOver={(enabled) => chooseOwnership(objectId, enabled)}
                      /* Provide other affected fans from this layer, which can see every object on the channel. */
                      takenOverWith={siblingFansTakenOverWith(
                        objectById().get(objectId)!,
                        catalog()?.objects ?? [])}
                      accessLevel={accessLevel()}
                      writeReady={noticeSeen() === true && accessLevel() !== null}
                      state={state()}
                      edited={settingsOf(objectId)}
                      selectedCurves={curveSelections().get(objectId)}
                      saving={saving()}
                      onApply={() => void apply(objectId)}
                      onDiscard={() => discard(objectId)}
                      actual={actual()}
                      presets={(presets()?.presets ?? []).filter(
                        (preset) => preset.objectId === objectId)}
                      onEdit={(capabilityId, next) =>
                        editCapability(objectId, capabilityId, next)}
                      onReplaceSettings={(settings) => {
                        clearCurveSelections(objectId);
                        setDraft([
                        ...edited().filter((entry) => entry.objectId !== objectId),
                        ...(settings.length > 0
                          ? [{ objectId: objectId, settings }]
                          : [])
                      ]); }}
                      onSavePreset={(name) => acceptSourceCommand(runtime.sources.control.presets, () => saveControlPreset(
                        runtime.requestClient,
                        objectId,
                        name,
                        settingsOf(objectId)), value => value)
                        .then(() => true).catch(problem => { reportFailure(problem, uiText.control.saveFailed); return false; })}
                      onDeletePreset={(presetId) => acceptSourceCommand(runtime.sources.control.presets, () => deleteControlPreset(
                        runtime.requestClient,
                        presetId), value => value)
                        .then(() => undefined).catch(problem => reportFailure(problem, uiText.control.saveFailed))}
                    />
                  )}
                </For>
              </div>
            </section>
          )}
        </For>
        </Show>
      </div>
    </div>
  );
}

/** List other fans only when the catalog declares channel-wide software ownership; unconfigured fans may hold their current speed. */
function siblingFansTakenOverWith(
  object: ControlObject,
  all: readonly ControlObject[]
): readonly string[] {
  if (!object.softwareTakeoverCoversAllFans) {
    return [];
  }
  return all
    .filter((other) =>
      other.id !== object.id
      && other.kind === "fan"
      && other.detail === object.detail)
    .map((other) => controlObjectName(other));
}

function ControlObjectCard(props: {
  object: ControlObject;
  /** Reported GPU name for the indexed fan controller, or null. */
  gpuName: string | null;
  fanModeChoice?: string;
  onFanModeChoice: (mode: string | null) => void;
  takingOver: boolean;
  onTakeOver: (enabled: boolean) => void;
  /** Other fans affected by software ownership; empty when ownership is independent. */
  takenOverWith: readonly string[];
  /** Current adjustment level gates capability selection. */
  accessLevel: ControlAccessLevel | null;
  writeReady: boolean;
  state: ControlStateView | null;
  edited: readonly ControlSetting[];
  selectedCurves?: ReadonlySet<string>;
  saving: boolean;
  onApply: () => void;
  onDiscard: () => void;
  actual: ControlActualState | null;
  presets: readonly ControlPreset[];
  onEdit: (capabilityId: string, next: ControlSetting | null) => void;
  /** Replace the object's entire desired state for preset loading or return to firmware. */
  onReplaceSettings: (settings: readonly ControlSetting[]) => void;
  onSavePreset: (name: string) => Promise<boolean>;
  onDeletePreset: (presetId: string) => Promise<void>;
}) {
  const [presetName, setPresetName] = createSignal("");
  const [presetBusy, setPresetBusy] = createSignal(false);
  onPageLeave(() => setPresetName(""));
  // Saved settings and latest execution receipt for this object.
  const saved = () => props.state?.desired.objects
    .find((entry) => entry.objectId === props.object.id)?.settings ?? [];

  /** Absent settings imply firmware ownership. Track only the pending software choice before any setting exists. */
  const isFan = () => props.object.kind === "fan";
  const modes = () => props.object.fanManagementModes ?? [];
  const storedMode = () => props.edited.length === 0 ? "firmware-management"
    : modes().find(mode => mode.items.length > 0 && props.edited.every(setting => mode.items.some(item =>
        item.capabilityId === setting.capabilityId && item.curveExecution === (setting.curveExecution ?? null))))?.id ?? null;
  const selectedMode = () => props.edited.length > 0 ? storedMode() : props.fanModeChoice ?? storedMode();
  const mode = () => modes().find(mode => mode.id === selectedMode());
  const mixed = () => isFan() && selectedMode() === null;
  const firmwareOwned = () => isFan() ? selectedMode() === "firmware-management"
    : props.edited.length === 0 && !props.takingOver;
  const editSetting = (capabilityId: string, setting: ControlSetting | null) => {
    const item = mode()?.items.find(item => item.capabilityId === capabilityId);
    props.onEdit(capabilityId, setting && isFan() ? { ...setting, curveExecution: item?.curveExecution ?? null } : setting);
  };
  const outcomes = () => props.state?.lastApply.outcomes
    .filter((entry) => entry.objectId === props.object.id) ?? [];

  const edited = () => props.edited;

  /** Observed capability value; omit when unavailable. */
  const actualOf = (capabilityId: string) => {
    const value = props.actual?.values.find((entry) =>
      entry.objectId === props.object.id && entry.capabilityId === capabilityId);
    return value && !value.unreadableReason
      && ((typeof value.number === "number" && Number.isFinite(value.number))
        || typeof value.toggle === "boolean") ? value : null;
  };

  const actualReadoutOf = (capability: ControlCapability) => {
    const value = actualOf(capability.id);
    if (!value || capability.valueKind !== "number" || typeof value.number !== "number") return value;
    const setting = edited().find((entry) => entry.capabilityId === capability.id)?.number;
    return actualRepeatsNumberEditor(setting, value.number) ? null : value;
  };

  const actualText = (value: ControlActualValue) => typeof value.toggle === "boolean"
    ? (value.toggle ? uiText.control.presentation.toggleOn : uiText.control.presentation.toggleOff)
    : `${formatActualNumber(value.number!)}${value.unit ? ` ${controlDisplayText(value.unit)}` : ""}`;

  /** Capabilities above the selected adjustment level cannot be selected. */
  const outOfReach = (capability: ControlCapability) =>
    props.accessLevel === null || !controlAccessLevelAllows(props.accessLevel, capability.requiredAccessLevel);

  /** Firmware ownership, insufficient level, or platform limits prevent editing. */
  const locked = (capability: ControlCapability) =>
    !props.writeReady || firmwareOwned() || mixed() || (isFan() && !mode()?.available) || outOfReach(capability)
    || (!capability.supported && capability.valueKind !== "gpu-vf-curve-reference");

  /** Firmware ownership is a local, removable restriction independent of backend availability. */
  const blockedKind = (capability: ControlCapability) => {
    if (firmwareOwned()) {
      return "firmware-owned" as const;
    }
    return capability.unavailableKind;
  };

  /** Expose disabled reasons on the lock to avoid repeating them in every row. */
  /** Risk guidance for elevated levels; channel guidance stays at page level. */
  const capabilityLegend = (capability: ControlCapability): HelpHintEntry[] => {
    const entries: HelpHintEntry[] = [];
    const level = capability.requiredAccessLevel;
    if (level !== "normal") {
      entries.push({ label: uiText.control.accessLevel.name[level], text: uiText.control.accessLevel.hint[level] });
    }
    return entries;
  };

  const lockReason = (capability: ControlCapability) => {
    /* Combine firmware ownership with remaining restrictions so all prerequisites are visible together. */
    if (!props.writeReady) return props.accessLevel === null
      ? uiText.control.recovery.accessReadFailed : uiText.control.recovery.noticeReadFailed;
    const own = firmwareOwned() ? uiText.control.ownership.firmwareNote : null;
    const access = outOfReach(capability)
      ? uiText.control.recovery.accessRequired(uiText.control.accessLevel.name[capability.requiredAccessLevel]) : null;
    const capabilityReason = capability.supported || !capability.unavailableReason
      ? null : controlReasonText(capability.unavailableReason);
    return [own, access, capabilityReason].filter(Boolean).join(" ") || controlCapabilityLabel(capability);
  };

  /** Show readable platform-unsupported values separately; access restrictions and missing writers remain editable candidates. */
  const readOnly = () => props.object.capabilities
    .filter((capability) => capability.valueKind !== "gpu-vf-curve-reference" && !capability.supported
      && capability.unavailableKind === "platform")
    .map((capability) => ({ capability, value: actualOf(capability.id) }))
    .filter((entry): entry is { capability: ControlCapability; value: ControlActualValue } =>
      entry.value !== null);

  /** Remaining capabilities belong in the editable section. */
  const adjustable = () => {
    const shown = new Set(readOnly().map((entry) => entry.capability.id));
    return props.object.capabilities.filter((capability) => !shown.has(capability.id)
      && (!isFan() || mode()?.items.some(item => item.capabilityId === capability.id)));
  };

  const itemView = (capabilityId: string) => controlItemStatusOf(
    capabilityId,
    edited(),
    saved(),
    outcomes(),
    false);
  const itemStatus = (capabilityId: string) => props.selectedCurves?.has(capabilityId) ? "edited" : itemView(capabilityId).status;
  const pending = () => Boolean(props.selectedCurves?.size) || hasPendingChanges(edited(), saved());
  const applyProblem = () => {
    if (mixed()) return controlReasonText({ code: "fan-management-modes-mixed" });
    for (const setting of edited()) {
      const capability = props.object.capabilities.find(item => item.id === setting.capabilityId);
      if (capability?.valueKind === "gpu-vf-curve-reference" && !capability.supported)
        return controlReasonText(capability.unavailableReason);
      if (capability?.valueKind === "number" && (typeof setting.number !== "number" || !Number.isFinite(setting.number))
        || capability?.valueKind === "gpu-vf-curve-reference" && !setting.gpuVfCurvePresetId
        || capability?.valueKind === "curve" && !setting.curve?.length) return uiText.controlPage.incompleteSetting;
    }
    return edited().length === 0 && saved().length === 0 && !firmwareOwned() ? uiText.controlPage.selectSetting : null;
  };

  return (
    <article
      class="control-object"
      classList={{ "control-object-inactive": !props.object.isControllable }}
    >
      <header>
        <strong>{controlObjectName(props.object)}<GpuNameText name={props.gpuName} /></strong>
        <span class="control-object-platform">
          {/* Availability belongs to each object and may differ between GPUs. */}
          {uiText.control.term[props.object.platform.vendor]}
          {/* Identify integrated GPUs because their control capabilities differ. */}
          <Show when={props.object.gpuAttachment}>
            {(attachment) => <>
              {" · "}
              {uiText.control.attachment[
                attachment() as keyof typeof uiText.control.attachment
              ]}
            </>}
          </Show>
          {/* Show classification tags separately from the object's name. */}
          <For each={[...new Set(props.object.terms)].filter(term => uiText.control.term[term]
            && term !== props.object.platform.vendor && term !== props.object.platform.operatingSystem)}>
            {(term) => <>{" · "}{uiText.control.term[term]}</>}
          </For>
          <Show when={props.object.detail && uiText.control.term[props.object.detail]}>{(detail) => <> · {detail()}</>}</Show>
        </span>
      </header>

      <Show when={readOnly().length > 0}>
        <div class="control-readings">
          <dl>
            <For each={readOnly()}>
              {(entry) => (
                <div class="control-reading">
                  <dt>
                    {controlCapabilityLabel(entry.capability)}
                    <Show when={entry.capability.channel}>
                      {(channel) => (
                        <span class="control-capability-channel">
                          {controlChannelLabel(channel())}
                        </span>
                      )}
                    </Show>
                  </dt>
                  <dd>
                    {actualText(entry.value)}
                    <Show when={entry.capability.unavailableReason}>
                      {(reason) => <HelpHint>{controlReasonText(reason())}</HelpHint>}
                    </Show>
                  </dd>
                </div>
              )}
            </For>
          </dl>
        </div>
      </Show>


      {/* Show a shared object-level restriction once. */}
      {/* Always offer returning the entire object to firmware control. */}
      {/* Explain each fan management mode and its lifetime, including other fans affected by software ownership. */}
      <Show when={isFan()}>
      <div class="control-ownership control-fan-ownership">
        <Show when={props.object.softwareTakeoverCoversAllFans}>
          <p class="control-curve-note" role="note">{props.takenOverWith.length > 0
            ? uiText.control.takeoverCoversOthers(
              new Intl.ListFormat(currentLanguage(), { type: "conjunction" }).format(props.takenOverWith))
            : uiText.control.term["takeover-all-fans"]}</p>
        </Show>
        <span class="with-help-hint">
        <RadioGroupRoot class="segmented settings-segmented-control control-ownership-choice control-fan-modes"
          ariaLabel={uiText.control.ownership.label} value={selectedMode() ?? ""} onChange={id => {
            if (selectedMode() === id) return;
            props.onReplaceSettings([]);
            props.onFanModeChoice(id);
          }}>
          <For each={modes()}>{entry =>
            <RadioGroupItem value={entry.id} class="segment settings-segment" disabled={!entry.available}>
              <Show when={!entry.available}>
                <Show when={entry.unavailableKind === "platform"} fallback={<Lock size={13} />}><X size={13} /></Show>
              </Show>
              {uiText.control.term[entry.id]}
            </RadioGroupItem>
          }</For>
        </RadioGroupRoot>
        {/* Explain unavailable modes beside their choices. */}
        <OptionsHelpHint class="control-fan-mode-help" label={uiText.control.ownership.label}
          entries={modes().map(entry => ({ label: uiText.control.term[entry.id],
            text: [uiText.control.fanModeHint[entry.id], entry.unavailableReason && controlReasonText(entry.unavailableReason)]
              .filter(Boolean).join(" ") }))} />
        </span>
        <Show when={mixed()}>
          <p class="control-curve-note" role="alert">{controlReasonText({code: "fan-management-modes-mixed"})}</p>
        </Show>
      </div>
      </Show>
      <Show when={!isFan()}>
      <div class="control-ownership">
        <SegmentedControl
          value={firmwareOwned() ? "firmware" : "app"}
          ariaLabel={uiText.control.ownership.label}
          // Set itemClass to keep segmented choices from inheriting primary-button styling.
          // Selection styling must distinguish the active segment.
          class="settings-segmented-control control-ownership-choice"
          itemClass="settings-segment"
          options={[
            { id: "firmware", label: uiText.control.ownership.firmware },
            { id: "app", label: uiText.control.ownership.app }
          ]}
          onChange={(next) => {
            if (next === "firmware") {
              props.onTakeOver(false);
              // Returning to firmware clears the object's complete desired state.
              props.onReplaceSettings([]);
              return;
            }
            props.onTakeOver(true);
          }}
        />

      </div>

      </Show>
      {/* Loading an instance preset only edits the draft; explicit apply remains required. */}
      {/* Hide empty preset controls when firmware owns the object. */}
      <Show when={props.presets.length > 0 || !firmwareOwned()}>
      <div class="control-presets">
        <For each={props.presets}>
          {(preset) => (
            <span class="control-preset">
              <button
                class="secondary"
                type="button"
                disabled={presetBusy()}
                onClick={() => {
                  props.onFanModeChoice(null);
                  props.onTakeOver(preset.settings.length > 0);
                  props.onReplaceSettings(preset.settings);
                }}
              >
                {preset.name}
              </button>
              <button
                class="control-preset-remove"
                type="button"
                aria-label={uiText.control.presets.remove}
                data-name-tooltip=""
                disabled={presetBusy()}
                onClick={() => {
                  setPresetBusy(true);
                  void props.onDeletePreset(preset.id).finally(() => setPresetBusy(false));
                }}
              >
                ×
              </button>
            </span>
          )}
        </For>
        <input
          class="control-preset-name"
          type="text"
          value={presetName()}
          placeholder={uiText.control.presets.namePlaceholder}
          aria-label={uiText.control.presets.namePlaceholder}
          disabled={presetBusy()}
          onInput={(event) => setPresetName(event.currentTarget.value)}
        />
        <button
          class="secondary"
          type="button"
          disabled={presetBusy() || presetName().trim().length === 0}
          onClick={async () => {
            setPresetBusy(true);
            try { if (await props.onSavePreset(presetName().trim())) setPresetName(""); }
            finally { setPresetBusy(false); }
          }}
        >
          {uiText.control.presets.save}
        </button>
      </div>
      </Show>

      {/* Separate readable platform limits from adjustable controls so values remain discoverable. */}

      <ul class="control-capabilities">
        <For each={adjustable().map(capability => capability.id)}>
          {(capabilityId) => {
            const capability = () => props.object.capabilities.find(item => item.id === capabilityId)!;
            return (
            <li
              class="control-capability"
              classList={{
                // Distinguish locked, unselected, and selected capability states.
                // Unselected capabilities return to firmware on the next apply.
                // Selected capabilities are applied on the next apply.
                "control-capability-locked": locked(capability()),
                "control-capability-on": props.edited.some(
                  (entry) => entry.capabilityId === capability().id),
                "control-capability-off": !capability().supported || firmwareOwned()
              }}
            >
              <span class="control-capability-label">
                {/* Derive selection directly from desired-state membership to avoid duplicate state. */}
                {/* Use locks for unavailable capabilities so they differ from unselected checkboxes. */}
                <Show
                  when={!locked(capability())}
                  fallback={
                    /* Backend availability categories distinguish removable restrictions from platform limits. */
                    /* The lock is an accessible help button with the same selectable explanation for pointer and keyboard users. */
                    <HelpHint
                      class="control-capability-lock-slot"
                      label={lockReason(capability())}
                      icon={
                    <Switch
                      fallback={
                        <Lock
                          class="control-capability-lock"
                          size={13}
                          aria-hidden="true"
                        />
                      }
                    >
                      {/* A cross marks an unsupported platform capability. */}
                      <Match when={blockedKind(capability()) === "platform"}>
                        <X
                          class="control-capability-x"
                          size={14}
                          aria-hidden="true"
                        />
                      </Match>
                      {/* A neutral hollow circle marks missing implementation without implying a setting or hardware fault. */}
                      <Match when={blockedKind(capability()) === "not-implemented"}>
                        <CircleDashed
                          class="control-capability-pending"
                          size={13}
                          aria-hidden="true"
                        />
                      </Match>
                      {/* Missing components link to their installation location. */}
                      <Match when={blockedKind(capability()) === "component"}>
                        <Download
                          class="control-capability-needs"
                          size={13}
                          aria-hidden="true"
                        />
                      </Match>
                    </Switch>
                    }
                    >
                      {lockReason(capability())}
                    </HelpHint>
                  }
                >
                  {/* Toggle rows reserve alignment space because firmware is already one of their three choices. */}
                  <Show
                    when={capability().valueKind !== "toggle" && capability().valueKind !== "gpu-vf-curve-reference"}
                    fallback={<span class="control-capability-check" aria-hidden="true" />}
                  >
                    <input
                      type="checkbox"
                      class="control-capability-check"
                      checked={props.edited.some(
                        (entry) => entry.capabilityId === capability().id)}
                      aria-label={controlCapabilityLabel(capability())}
                      onChange={(event) => editSetting(
                        capability().id,
                        event.currentTarget.checked ? initialSettingFor(capability(), actualOf(capability().id)?.number) : null)}
                    />
                  </Show>
                </Show>
                {controlCapabilityLabel(capability())}
                {/* Keep elevated-level markers visible even when unlocked; omit the default level. */}
                {/* Use a color marker for risk level and reveal details on demand. */}
                {/* Show each capability's writer channel because controls on the same device may use different interfaces. */}
                <Show when={capability().channel}>
                  {(channel) => (
                    <span class="control-capability-channel">
                      {controlChannelLabel(channel())}
                    </span>
                  )}
                </Show>
                <Show when={capability().requiredAccessLevel === "normal"
                  ? null
                  : capability().requiredAccessLevel}>
                  {(level) => (
                    <span
                      class="control-capability-level"
                      classList={{
                        "control-capability-level-root": level() === "root"
                      }}
                    >
                      <span class="sr-only">
                        {uiText.control.accessLevel.name[level()]}
                      </span>
                    </span>
                  )}
                </Show>
                {/* Show help only for adjustment levels that need explanation. */}
                <OptionsHelpHint entries={capabilityLegend(capability())} />
              </span>

              <span class="control-capability-meta">
                <Show when={props.actual?.values.find(entry => entry.objectId === props.object.id
                  && entry.capabilityId === capability().id)?.unreadableReason}>
                  {reason => <HelpHint label={uiText.control.actual}>{controlReasonText(reason())}</HelpHint>}
                </Show>
                {/* Only configured capabilities need execution status. */}
                <Show when={itemStatus(capability().id) !== "unset"}>
                  <span
                    class="control-capability-tag"
                    data-tone={controlItemTone(itemStatus(capability().id))}
                  >
                    {uiText.control.status[itemStatus(capability().id)]}
                  </span>
                  {/* Show backend failure reasons beside status, including failed firmware restoration. */}
                  <Show when={itemStatus(capability().id) === "failed" || itemStatus(capability().id) === "unsupported"
                    ? controlOutcomeReason(outcomes().find(outcome => outcome.capabilityId === capability().id)) : null}>
                    {(reason) => <HelpHint>{reason()}</HelpHint>}
                  </Show>
                </Show>
                {/* Show observed values only when a reading exists. */}
                {/* Observed values use a live marker and slider position, with equivalent accessible text. */}
                {/* Avoid repeating a reading already displayed by the numeric editor. */}
                <Show when={actualReadoutOf(capability())}>
                  {(value) => (
                    <span
                      class="control-capability-actual"
                      data-full-content={`${uiText.control.actual}${actualText(value())}`}
                    >
                      <span class="control-capability-live" aria-hidden="true" />
                      <span class="sr-only">{uiText.control.actual}</span>
                      {actualText(value())}
                    </span>
                  )}
                </Show>
              </span>
              <ControlCapabilityEditor
                capability={capability()}
                objectId={props.object.id}
                firmwareCurveKind={props.object.firmwareCurveKind ?? null}
                curveExecution={mode()?.items.find(item => item.capabilityId === capability().id)?.curveExecution ?? null}
                takenOverWith={props.takenOverWith}
                setting={edited().find((entry) => entry.capabilityId === capability().id)}
                actual={actualOf(capability().id)?.number ?? null}
                // Firmware-owned settings cannot be edited.
                held={locked(capability())}
                unavailableReason={lockReason(capability())}
                onChange={(next) => editSetting(capability().id, next)}
              />
            </li>
          ); }}
        </For>
      </ul>
      <div class="control-object-actions">
        <Show when={applyProblem()}>{reason => <span class="control-action-reason" role="note">{reason()}</span>}</Show>
        <button class="secondary" type="button"
          disabled={props.saving || (!pending() && !props.takingOver && !props.fanModeChoice)}
          onClick={props.onDiscard}>{uiText.control.discard}</button>
        <button type="button"
          disabled={props.saving || !props.writeReady || Boolean(applyProblem())
            || (!pending() && !canRetryControlObject(props.state, props.object.id))}
          onClick={props.onApply}>
          <StableLabel
            labels={{ apply: uiText.control.apply, retry: uiText.control.recovery.retry }}
            current={!pending() && canRetryControlObject(props.state, props.object.id) ? "retry" : "apply"}
          />
        </button>
      </div>
    </article>
  );
}

/** List present and absent registered devices so retained presets remain discoverable and obsolete instances can be deleted. */
function ControlInstancesPanel(props: {
  catalog: ControlInstanceCatalog | null;
  onRefresh: () => Promise<void>;
  onForget: (instanceId: string) => Promise<void>;
}) {
  const [busy, setBusy] = createSignal(false);
  const run = async (action: () => Promise<void>) => {
    if (busy()) return;
    setBusy(true);
    try { await action(); } finally { setBusy(false); }
  };
  return (
    <section class="control-group control-instances">
      <div class="control-instances-header">
        <h3>{uiText.control.instances.title}</h3>
        <button class="secondary" type="button" disabled={busy()} onClick={() => void run(props.onRefresh)}>
          {uiText.control.instances.refresh}
        </button>
      </div>

      <ul class="control-instance-list">
        <For each={props.catalog?.instances ?? []}>
          {(view) => (
            <li classList={{ "control-instance-absent": !view.isPresent }}>
              <span class="control-capability-label">
                {controlObjectName(view.instance)}
                <GpuNameText name={controlFanGpuName(view.instance, (props.catalog?.instances ?? []).map((other) => other.instance))} />
              </span>
              <span class="control-object-platform">
                {view.isPresent
                  ? uiText.control.instances.present
                  : uiText.control.instances.absent}
              </span>
              <span class="with-help-hint">
              <button
                class="secondary"
                type="button"
                disabled={view.isPresent || busy()}
                onClick={() => void run(() => props.onForget(view.instance.id))}
              >
                {uiText.control.instances.forget}
              </button>
              <Show when={view.isPresent}><HelpHint>{uiText.controlPage.presentDevice}</HelpHint></Show>
              </span>
            </li>
          )}
        </For>
      </ul>
    </section>
  );
}
