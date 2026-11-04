import { createEffect, createMemo, createSignal, For, on, Show } from "solid-js";
import { createStore, produce } from "solid-js/store";
import { Portal } from "solid-js/web";
import type { ConfirmDialogRequest } from "../../../components/AppFeedback";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { uiText } from "../../../text.ts";
import { DialogActions, DialogBody, DialogHeader, DialogRoot } from "../../../ui/primitives/Dialog.tsx";
import { ItemPicker } from "../../itemSelection/ItemPicker.tsx";
import { itemKinds, type CatalogEntry } from "../../itemSelection/itemSelectionModel.ts";
import {
  createProfile, deleteProfile, getItemCatalog, maximumProfileItems, maximumProfiles, patchProfile,
  recordingErrorText, recordingFailure, type RecordingProfile
} from "../targetedRecordingApi.ts";
import {
  draftChanged, draftFromProfile, draftIssues, hasIssues, parsedDefaultInterval, profilePatch, type ProfileDraft
} from "./profileDrafts.ts";

const newDraftPrefix = "new:";

/* Keep edits local until save; new profiles need an item. Require clean drafts before copying and confirmation before deletion or discarding changes. Portal above the parent details dialog. */
export function TargetedRecordingProfilesDialog(props: {
  open: boolean;
  /** Initially selected profile. */
  initialProfileId: string | null;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  onClose: () => void;
}) {
  const runtime = useFrontendRuntime();
  const copy = () => uiText.targetedReport.profiles;
  const profilesSource = useSource(runtime.sources.targetedRecordingProfiles, () => ({ active: props.open }));
  const profiles = createMemo(() => profilesSource.snapshot().data ?? []);
  const [drafts, setDrafts] = createStore<Record<string, ProfileDraft>>({});
  const [selectedKey, setSelectedKey] = createSignal<string | null>(null);
  const [newSequence, setNewSequence] = createSignal(0);
  const [pickerIssues, setPickerIssues] = createSignal(false);
  const [busy, setBusy] = createSignal(false);
  const [failure, setFailure] = createSignal<string | null>(null);
  const [catalog, setCatalog] = createSignal<CatalogEntry[]>([]);
  const [catalogState, setCatalogState] = createSignal<"loading" | "ready" | "failed">("loading");
  // Remount the picker after switching or discarding to clear invalid interval text.
  const [pickerRevision, setPickerRevision] = createSignal(0);

  const loadCatalog = async () => {
    setCatalogState("loading");
    try {
      setCatalog(await getItemCatalog(runtime.requestClient));
      setCatalogState("ready");
    } catch {
      setCatalogState("failed");
    }
  };

  createEffect(on(() => props.open, (open) => {
    if (!open) return;
    setDrafts(produce((all) => { for (const key of Object.keys(all)) delete all[key]; }));
    setSelectedKey(props.initialProfileId);
    setFailure(null);
    void loadCatalog();
    void profilesSource.refresh().catch(() => undefined);
  }));

  // Visibly select the first profile when the requested one is absent.
  createEffect(() => {
    const key = selectedKey();
    if (key !== null && (key.startsWith(newDraftPrefix) ? drafts[key] : profiles().some((item) => item.id === key))) return;
    setSelectedKey(profiles()[0]?.id ?? null);
  });

  const savedProfile = (key: string | null) => key === null ? undefined : profiles().find((item) => item.id === key);
  const draftOf = (key: string | null): ProfileDraft | null => {
    if (key === null) return null;
    if (drafts[key]) return drafts[key];
    const saved = savedProfile(key);
    return saved ? draftFromProfile(saved) : null;
  };
  const current = createMemo(() => draftOf(selectedKey()));
  const isUnsaved = (key: string) => {
    const draft = drafts[key];
    return draft !== undefined && draftChanged(draft, savedProfile(key));
  };
  const unsavedKeys = createMemo(() => Object.keys(drafts).filter(isUnsaved));
  const issues = createMemo(() => { const draft = current(); return draft ? draftIssues(draft) : null; });
  const totalProfiles = () => profiles().length + Object.keys(drafts).filter((key) => key.startsWith(newDraftPrefix)).length;
  const atProfileLimit = () => totalProfiles() >= maximumProfiles;
  const selectedUnsaved = () => { const key = selectedKey(); return key !== null && isUnsaved(key); };

  const edit = (change: Partial<ProfileDraft>) => {
    const key = selectedKey();
    const draft = current();
    if (key === null || draft === null) return;
    setDrafts(key, { ...draft, ...change });
  };
  const select = (key: string) => {
    setSelectedKey(key);
    setFailure(null);
    setPickerRevision((value) => value + 1);
  };
  const forget = (key: string) => setDrafts(produce((all) => { delete all[key]; }));
  const fail = (error: unknown) => setFailure(recordingErrorText(recordingFailure(error), "profile"));
  const acceptProfiles = (next: RecordingProfile[]) => profilesSource.acceptAuthoritative(next);

  const createEmpty = () => {
    if (atProfileLimit()) return;
    const sequence = newSequence() + 1;
    setNewSequence(sequence);
    const key = `${newDraftPrefix}${sequence}`;
    setDrafts(key, { savedId: null, name: copy().newProfileName(totalProfiles() + 1), defaultIntervalText: "1000", items: [] });
    select(key);
  };

  const copySelected = async () => {
    const saved = savedProfile(selectedKey());
    if (!saved || selectedUnsaved() || atProfileLimit() || busy()) return;
    setBusy(true);
    setFailure(null);
    try {
      const created = await createProfile(runtime.requestClient, { name: copy().copyName(saved.name), copyFromId: saved.id });
      acceptProfiles([...profiles(), created]);
      select(created.id);
      await profilesSource.refresh();
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    const key = selectedKey();
    const draft = current();
    if (key === null || draft === null || busy() || pickerIssues() || hasIssues(draftIssues(draft))) return;
    const defaultIntervalMs = parsedDefaultInterval(draft)!;
    setBusy(true);
    setFailure(null);
    try {
      if (draft.savedId === null) {
        const created = await createProfile(runtime.requestClient,
          { name: draft.name.trim(), defaultIntervalMs, items: draft.items });
        acceptProfiles([...profiles(), created]);
        forget(key);
        select(created.id);
      } else {
        const saved = savedProfile(key);
        if (!saved) return;
        const updated = await patchProfile(runtime.requestClient, saved.id, profilePatch(draft, saved));
        acceptProfiles(profiles().map((item) => item.id === updated.id ? updated : item));
        forget(key);
        setPickerRevision((value) => value + 1);
      }
      await profilesSource.refresh();
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const discard = () => {
    const key = selectedKey();
    if (key === null) return;
    forget(key);
    setFailure(null);
    if (key.startsWith(newDraftPrefix)) setSelectedKey(profiles()[0]?.id ?? null);
    setPickerRevision((value) => value + 1);
  };

  const remove = async () => {
    const saved = savedProfile(selectedKey());
    if (!saved || profiles().length <= 1 || busy()) return;
    const confirmed = await props.confirmDialog({
      title: copy().confirmDelete,
      message: saved.name,
      details: [copy().deleteDetail],
      tone: "warning",
      confirmLabel: copy().delete
    });
    if (!confirmed) return;
    setBusy(true);
    setFailure(null);
    try {
      await deleteProfile(runtime.requestClient, saved.id);
      forget(saved.id);
      acceptProfiles(profiles().filter((item) => item.id !== saved.id));
      // The selection effect handles removed profiles.
      setSelectedKey(null);
      setPickerRevision((value) => value + 1);
      await profilesSource.refresh();
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const close = async () => {
    if (busy()) return;
    if (unsavedKeys().length > 0) {
      const confirmed = await props.confirmDialog({
        title: copy().confirmDiscard,
        details: [copy().discardDetail],
        tone: "warning",
        confirmLabel: copy().discardConfirm
      });
      if (!confirmed) return;
    }
    props.onClose();
  };

  const listLabel = (key: string) => draftOf(key)?.name.trim() || key;
  const listKeys = createMemo(() => [
    ...profiles().map((item) => item.id),
    ...Object.keys(drafts).filter((key) => key.startsWith(newDraftPrefix))
  ]);
  const deleteBlockedReason = () => {
    const key = selectedKey();
    if (key === null || key.startsWith(newDraftPrefix)) return null;
    return profiles().length <= 1 ? uiText.targetedReport.errors.lastProfile : null;
  };
  const copyBlockedReason = () => {
    if (atProfileLimit()) return uiText.targetedReport.errors.profilesTooMany;
    const key = selectedKey();
    if (key !== null && (key.startsWith(newDraftPrefix) || selectedUnsaved())) return copy().copyNeedsSave;
    return null;
  };
  const saveBlocked = () => {
    const found = issues();
    return busy() || pickerIssues() || found === null || hasIssues(found) || !selectedUnsaved();
  };

  return (
    <Portal>
      <DialogRoot open={props.open} labelledBy="targetedRecordingProfilesTitle" class="targeted-profiles-modal"
        dismissOnBackdrop={false} onDismiss={() => void close()}>
        <DialogHeader title={copy().title} titleId="targetedRecordingProfilesTitle" closeLabel={copy().close}
          onDismiss={() => void close()} />
        <DialogBody class="targeted-profiles-body">
          <aside class="targeted-profiles-list" aria-label={copy().list}>
            <Show when={profilesSource.snapshot().data !== null} fallback={
              <p class="targeted-profiles-note">{uiText.targetedReport.profilesLoading}</p>
            }>
              <ul>
                <For each={listKeys()}>
                  {(key) => (
                    <li>
                      <button type="button" class="targeted-profiles-entry" classList={{ active: selectedKey() === key }}
                        aria-pressed={selectedKey() === key} onClick={() => select(key)}>
                        <strong>{listLabel(key)}</strong>
                        <Show when={isUnsaved(key)}>
                          <span class="targeted-profiles-unsaved">
                            {key.startsWith(newDraftPrefix) ? copy().unsavedNew : copy().unsaved}
                          </span>
                        </Show>
                      </button>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
            <div class="targeted-profiles-list-actions">
              <button class="secondary" type="button" disabled={busy() || atProfileLimit()} onClick={createEmpty}>
                {copy().newProfile}
              </button>
              <button class="secondary" type="button" disabled={busy() || copyBlockedReason() !== null || !savedProfile(selectedKey())}
                onClick={() => void copySelected()}>
                {copy().copyProfile}
              </button>
              <Show when={copyBlockedReason()}>
                {(reason) => <p class="targeted-profiles-note">{reason()}</p>}
              </Show>
            </div>
          </aside>
          <section class="targeted-profiles-editor">
            <Show when={current()}>
              {(draft) => (
                <>
                  <div class="targeted-profiles-fields">
                    <label class="targeted-profiles-field">
                      <span>{copy().name}</span>
                      <input type="text" value={draft().name} maxLength={128} aria-invalid={issues()?.name}
                        onInput={(event) => edit({ name: event.currentTarget.value })} />
                      <Show when={issues()?.name}>
                        <small class="targeted-profiles-issue" role="alert">{uiText.targetedReport.errors.profileInvalidName}</small>
                      </Show>
                    </label>
                    <label class="targeted-profiles-field">
                      <span>{copy().defaultInterval}</span>
                      <input type="text" inputMode="numeric" value={draft().defaultIntervalText}
                        aria-invalid={issues()?.defaultInterval}
                        onInput={(event) => edit({ defaultIntervalText: event.currentTarget.value })} />
                      <Show when={issues()?.defaultInterval} fallback={<small>{copy().defaultIntervalHint}</small>}>
                        <small class="targeted-profiles-issue" role="alert">{copy().intervalInvalid}</small>
                      </Show>
                    </label>
                  </div>
                  <h4>{copy().items}</h4>
                  <Show when={catalogState() !== "failed"} fallback={
                    <div class="targeted-recording-alert observation-state error" role="alert">
                      <strong>{copy().catalogFailed}</strong>
                      <button class="secondary" type="button" onClick={() => void loadCatalog()}>
                        {uiText.observationNotice.retry}
                      </button>
                    </div>
                  }>
                    <Show when={catalogState() === "ready"} fallback={<p class="targeted-profiles-note">{copy().catalogLoading}</p>}>
                      <Show when={`${selectedKey()}#${pickerRevision()}`} keyed>
                        <ItemPicker catalog={catalog()} use="recording" items={draft().items} maximumItems={maximumProfileItems}
                          allowedKinds={itemKinds}
                          defaultIntervalMs={parsedDefaultInterval(draft()) ?? savedProfile(selectedKey())?.defaultIntervalMs ?? 1000}
                          onChange={(items) => edit({ items })} onIssuesChange={setPickerIssues} />
                      </Show>
                    </Show>
                  </Show>
                  <Show when={issues()?.itemsEmpty}>
                    <p class="targeted-profiles-issue" role="status">{uiText.targetedReport.errors.profileItemsEmpty}</p>
                  </Show>
                </>
              )}
            </Show>
          </section>
        </DialogBody>
        <Show when={failure()}>
          {(text) => <p class="targeted-profiles-failure observation-state error" role="alert">{text()}</p>}
        </Show>
        <DialogActions class="targeted-profiles-actions">
          <div class="targeted-profiles-delete">
            <button class="secondary" type="button"
              disabled={busy() || deleteBlockedReason() !== null || !savedProfile(selectedKey())}
              onClick={() => void remove()}>
              {copy().delete}
            </button>
            <Show when={deleteBlockedReason()}>{(reason) => <small>{reason()}</small>}</Show>
          </div>
          <Show when={selectedUnsaved()}>
            <span class="targeted-profiles-status" role="status">
              {selectedKey()?.startsWith(newDraftPrefix) ? copy().unsavedNew : copy().unsaved}
            </span>
          </Show>
          <Show when={selectedUnsaved() && (pickerIssues() || hasIssues(issues() ?? { name: false, defaultInterval: false, itemsEmpty: false }))}>
            <small class="targeted-profiles-issue">{copy().fixIssues}</small>
          </Show>
          <button class="secondary" type="button" disabled={busy() || !selectedUnsaved()} onClick={discard}>
            {copy().discard}
          </button>
          <button type="button" disabled={saveBlocked()} onClick={() => void save()}>
            {busy() ? copy().saving : copy().save}
          </button>
        </DialogActions>
      </DialogRoot>
    </Portal>
  );
}
