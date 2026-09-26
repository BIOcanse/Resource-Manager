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
import { StandardSelect } from "../../../components/StandardSelect";

interface ManualSoftwareModalProps {
  open: boolean;
  kind: ManualSoftwareKind;
  software: SoftwareRecord[];
  actionInProgress: boolean;
  onClose: () => void;
  onSubmit: (request: ManualSoftwareRequest) => void | Promise<void>;
}

export function ManualSoftwareModal(props: ManualSoftwareModalProps) {
  let closeButton: HTMLButtonElement | undefined;
  const [selectedSoftwareId, setSelectedSoftwareId] = createSignal("");
  const [manualName, setManualName] = createSignal("");
  const [manualRootPaths, setManualRootPaths] = createSignal("");

  const candidates = createMemo(() => props.software.filter((item) => {
    const kind = normalizeSoftwareKind(item);
    return kind !== props.kind
      && Boolean(textOrEmpty(item.name));
  }));
  const selectedSoftware = createMemo(() => candidates().find((item) => item.id === selectedSoftwareId()));
  const rootPathLines = createMemo(() => manualRootPaths()
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean));
  const usesManualRootEntry = createMemo(() => props.kind === "Other" || props.kind === "Adapted");
  const manualSubmitName = createMemo(() => {
    if (props.kind === "Adapted") {
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
        kind: props.kind,
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
      kind: props.kind,
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
          title={manualSoftwareKindLabel(props.kind)}
          titleId="manualSoftwareModalTitle"
          closeButtonRef={(element) => { closeButton = element; }}
          onDismiss={props.onClose}
        />
        <DialogBody class="manual-software-body">
            <Show
              when={usesManualRootEntry()}
              fallback={
                <Show
                  when={candidates().length > 0}
                  fallback={<div class="management-empty">{uiText.manualSoftwareModal.noCandidates}</div>}
                >
                  <label class="manual-software-field">
                    <span>{uiText.manualSoftwareModal.software}</span>
                    <StandardSelect
                      value={selectedSoftwareId()}
                      ariaLabel={uiText.manualSoftwareModal.software}
                      searchable
                      searchPlaceholder={uiText.manualSoftwareModal.searchSoftware}
                      options={candidates().map((item) => ({
                        value: item.id,
                        label: `${item.name} · ${softwareDisplayKindLabel(item.kind, item.displayKind)}`
                      }))}
                      onChange={setSelectedSoftwareId}
                    />
                  </label>
                  <Show when={selectedSoftware()}>
                    {(item) => <div class="manual-software-candidate-meta">{candidateMeta(item())}</div>}
                  </Show>
                </Show>
              }
            >
              <Show when={props.kind === "Other"}>
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
            {props.actionInProgress ? uiText.manualSoftwareModal.saving : uiText.metricPicker.confirm}
          </button>
        </DialogActions>
      </form>
    </DialogRoot>
  );
}

export function manualSoftwareKindLabel(kind: ManualSoftwareKind) {
  return kind === "Adapted"
    ? uiText.manualSoftware.addAdapted
    : kind === "Game"
    ? uiText.manualSoftware.addGame
    : kind === "HighPerformance"
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
    softwareDisplayKindLabel(item.kind, item.displayKind),
    roots.length > 0 ? roots[0] : null,
    roots.length > 1 ? uiText.manualSoftwareModal.additionalRoots(roots.length - 1) : null
  ].filter(Boolean);
  return parts.join(" · ");
}
