import { StableLabel } from "../../../ui/primitives/StableLabel.tsx";
import { createEffect, createMemo, createSignal, Show } from "solid-js";
import type { ManualSoftwareKind, ManualSoftwareRequest, SoftwareRecord } from "../../../types";
import { softwareDisplayKindLabel, uiText } from "../../../text.ts";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../../../ui/primitives/Dialog.tsx";
import { normalizeSoftwareKind, textOrEmpty } from "../../../utils";
import { isUnclassifiedSoftware } from "../../../presentation/softwareClassification.ts";
import { softwareRecordName } from "../../../presentation/softwareFacts.ts";
import { StandardSelect } from "../../../components/StandardSelect";

interface ManualSoftwareModalProps {
  open: boolean;
  kind: ManualSoftwareKind;
  software: SoftwareRecord[];
  sourceSoftware?: SoftwareRecord | null;
  actionInProgress: boolean;
  onClose: () => void;
  onSubmit: (request: ManualSoftwareRequest) => void | Promise<void>;
}

export function ManualSoftwareModal(props: ManualSoftwareModalProps) {
  let closeButton: HTMLButtonElement | undefined;
  const [selectedSoftwareId, setSelectedSoftwareId] = createSignal("");
  const [manualName, setManualName] = createSignal("");
  const [manualRootPaths, setManualRootPaths] = createSignal("");

  const [classificationKind, setClassificationKind] = createSignal<ManualSoftwareKind>("other");
  const effectiveKind = () => props.sourceSoftware ? classificationKind() : props.kind;
  createEffect(() => { if (props.open) setClassificationKind(props.kind); });

  const candidates = createMemo(() => props.software.filter((item) => {
    const kind = normalizeSoftwareKind(item);
    return kind !== effectiveKind()
      && Boolean(textOrEmpty(item.name));
  }));
  const selectedSoftware = createMemo(() => props.sourceSoftware ?? candidates().find((item) => item.id === selectedSoftwareId()));
  const rootPathLines = createMemo(() => manualRootPaths()
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean));
  const usesManualRootEntry = createMemo(() => !props.sourceSoftware && (effectiveKind() === "other" || effectiveKind() === "adapted"));
  const manualSubmitName = createMemo(() => {
    if (effectiveKind() === "adapted") {
      return inferSoftwareNameFromRootPath(rootPathLines()[0]) ?? uiText.manualSoftwareModal.defaultName;
    }

    return textOrEmpty(manualName());
  });
  const canSubmit = createMemo(() => {
    if (props.actionInProgress) {
      return false;
    }

    if (usesManualRootEntry()) {
      return Boolean(manualSubmitName()) && rootPathLines().length > 0;
    }

    return Boolean(selectedSoftware());
  });

  createEffect(() => {
    if (!props.open) {
      return;
    }

    if (usesManualRootEntry()) {
      setManualName("");
      setManualRootPaths("");
      return;
    }

    if (!candidates().some((item) => item.id === selectedSoftwareId())) {
      setSelectedSoftwareId(candidates()[0]?.id ?? "");
    }
  });

  function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!canSubmit()) {
      return;
    }

    if (usesManualRootEntry()) {
      void props.onSubmit({
        name: manualSubmitName(),
        kind: effectiveKind(),
        rootPaths: rootPathLines(),
        sourceSoftwareId: null
      });
      return;
    }

    const source = selectedSoftware();
    if (!source) {
      return;
    }

    void props.onSubmit({
      name: source.name,
      kind: effectiveKind(),
      rootPaths: source.rootPaths ?? [],
      sourceSoftwareId: source.id
    });
  }

  return (
    <DialogRoot
      open={props.open}
      labelledBy="manualSoftwareModalTitle"
      class="manual-software-modal"
      dismissOnBackdrop={false}
      initialFocus={() => closeButton}
      onDismiss={props.onClose}
    >
      <form class="dialog-form" onSubmit={submit}>
        <DialogHeader
          title={manualSoftwareKindLabel(effectiveKind())}
          titleId="manualSoftwareModalTitle"
          closeButtonRef={(element) => { closeButton = element; }}
          onDismiss={props.onClose}
        />
        <DialogBody class="manual-software-body">
            <Show when={props.sourceSoftware}>
              {(source) => <>
                <strong>{source().name}</strong>
                <label class="manual-software-field">
                  <span>{uiText.management.categoryNav}</span>
                  <StandardSelect
                    value={classificationKind()}
                    ariaLabel={uiText.management.categoryNav}
                    options={(["other", "game", "high_performance"] as const).map((kind) => ({
                      value: kind, label: softwareDisplayKindLabel(kind)
                    }))}
                    onChange={(kind) => setClassificationKind(kind as ManualSoftwareKind)}
                  />
                </label>
              </>}
            </Show>
            <Show
              when={usesManualRootEntry()}
              fallback={
                <Show
                  when={props.sourceSoftware || candidates().length > 0}
                  fallback={<div class="management-empty">{uiText.manualSoftwareModal.noCandidates}</div>}
                >
                  <Show when={!props.sourceSoftware}>
                    <label class="manual-software-field">
                      <span>{uiText.manualSoftwareModal.software}</span>
                      <StandardSelect
                        value={selectedSoftwareId()}
                        ariaLabel={uiText.manualSoftwareModal.software}
                        searchable
                        searchPlaceholder={uiText.manualSoftwareModal.searchSoftware}
                        options={candidates().map((item) => ({
                          value: item.id,
                          label: `${softwareRecordName(item)} · ${candidateKindLabel(item)}`
                        }))}
                        onChange={setSelectedSoftwareId}
                      />
                    </label>
                  </Show>
                  <Show when={selectedSoftware()}>
                    {(item) => <div class="manual-software-candidate-meta">{candidateMeta(item())}</div>}
                  </Show>
                </Show>
              }
            >
              <Show when={effectiveKind() === "other"}>
                <label class="manual-software-field">
                  <span>{uiText.manualSoftwareModal.name}</span>
                  <input value={manualName()} onInput={(event) => setManualName(event.currentTarget.value)} />
                </label>
              </Show>
              <label class="manual-software-field">
                <span>{uiText.manualSoftwareModal.rootDirectory}</span>
                <textarea
                  rows={5}
                  value={manualRootPaths()}
                  placeholder={uiText.manualSoftwareModal.rootPlaceholder}
                  onInput={(event) => setManualRootPaths(event.currentTarget.value)}
                />
              </label>
            </Show>
        </DialogBody>
        <DialogActions>
          <button class="secondary" type="button" disabled={props.actionInProgress} onClick={props.onClose}>
            {uiText.manualSoftwareModal.cancel}
          </button>
          <button type="submit" disabled={!canSubmit()}>
            <StableLabel labels={{ idle: uiText.metricPicker.confirm, busy: uiText.manualSoftwareModal.saving }} current={props.actionInProgress ? "busy" : "idle"} />
          </button>
        </DialogActions>
      </form>
    </DialogRoot>
  );
}

export function manualSoftwareKindLabel(kind: ManualSoftwareKind) {
  return kind === "adapted"
    ? uiText.manualSoftware.addAdapted
    : kind === "game"
    ? uiText.manualSoftware.addGame
    : kind === "high_performance"
      ? uiText.manualSoftware.addHighPerformance
      : uiText.manualSoftware.addGeneral;
}

function inferSoftwareNameFromRootPath(path: string | undefined) {
  const normalized = textOrEmpty(path)?.replace(/[\\/]+$/, "");
  if (!normalized) {
    return null;
  }

  return normalized.split(/[\\/]/).filter(Boolean).at(-1) ?? normalized;
}

function candidateMeta(item: SoftwareRecord) {
  const roots = item.rootPaths ?? [];
  const parts = [
    candidateKindLabel(item),
    roots.length > 0 ? roots[0] : null,
    roots.length > 1 ? uiText.manualSoftwareModal.additionalRoots(roots.length - 1) : null
  ].filter(Boolean);
  return parts.join(" · ");
}

function candidateKindLabel(item: SoftwareRecord) {
  return isUnclassifiedSoftware(item)
    ? uiText.softwareKind.Unclassified
    : softwareDisplayKindLabel(item.kind);
}
