import { Copy, KeyRound, RefreshCw, ShieldCheck, Trash2 } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, onCleanup, onMount, Show } from "solid-js";
import { StandardSelect } from "../../../components/StandardSelect.tsx";
import { userFacingDateTime, userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { uiText } from "../../../text.ts";
import { ConfirmDialog, type ConfirmDialogRecord, type ConfirmDialogRequest } from "../../../ui/patterns/ConfirmDialog.tsx";
import { useAccessRequestSnapshot } from "../../trustedAccess/AccessRequestSnapshotContext.tsx";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { onPageLeave } from "../../../app/navigation/pageHost.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import { acceptSourceCommand } from "../../../frontendRuntime/source/acceptSourceCommand.ts";
import { refreshAccessKeysAfterCommand } from "../../../data/settingsReadSources.ts";
import {
  approveAccessRequest,
  createAccessKey,
  denyAccessRequest,
  revokeAccessKey,
  rotateAccessKey,
  setDefaultAccessKey,
  TrustedAccessRequestError
} from "../../trustedAccess/trustedAccessApi.ts";
import {
  accessRequestListView,
  accessRequestNoticeText,
  accessScopeLabel
} from "../../trustedAccess/trustedAccessPresentation.ts";
import {
  isKnownAccessScope,
  type AccessKeyCreated,
  type AccessKeySnapshot,
  type AccessKeySummary,
  type AccessRequestDecisionResult,
  type AccessRequestDenialReason,
  type AccessRequestPending,
  type TrustedAccessFailureReason
} from "../../trustedAccess/trustedAccessTypes.ts";
import type { TrustedAccessCopy } from "../../../i18n/settingsTypes.ts";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

interface AccessRequestOutcome {
  readonly requestId: string;
  readonly name: string;
  readonly status: AccessRequestDecisionResult["status"];
  readonly denialReason?: AccessRequestDecisionResult["denialReason"];
}

/** Show key data only when ready; failures show a reason and retry without stale data. */
type KeyListState =
  | { readonly status: "loading" }
  | { readonly status: "ready"; readonly snapshot: AccessKeySnapshot }
  | { readonly status: "failed"; readonly reason: TrustedAccessFailureReason; readonly error: unknown };

/** Serialize operations using create, default, key:<id>, or request:<id>. */
type ActionTarget = "create" | "default" | `key:${string}` | `request:${string}`;

export function TrustedAccessManager(props: {
  copy: TrustedAccessCopy;
  /** Focus target after navigating to access requests. */
  onFocusTargetChange?: (target: HTMLElement | undefined) => void;
}) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const pendingSnapshot = useAccessRequestSnapshot();
  const requestList = createMemo(() => accessRequestListView(pendingSnapshot()));
  const requestById = createMemo(() => new Map(requestList().requests.map((request) => [request.requestId, request])));
  const requestIds = createMemo(() => requestList().requests.map((request) => request.requestId),
    [], { equals: (previous, next) => previous.length === next.length && previous.every((id, index) => id === next[index]) });
  // Visibility owns key-list reads; release while hidden and reread on return.
  const workId = frontendWorkIds.settingsTrustedAccess;
  useFrontendVisibilityDemand(workId, [workId]);
  const keySource = useSource(runtime.sources.settingsReads.accessKeys,
    () => ({ active: frontendWork.isNeeded(workId), refreshIntervalMs: null }));
  const keyList = (): KeyListState => {
    const current = keySource.snapshot();
    if (sourceCanRender(current) && current.data) return { status: "ready", snapshot: current.data };
    if (current.status === "idle" || current.status === "loading") return { status: "loading" };
    return { status: "failed", reason: current.error instanceof TrustedAccessRequestError ? current.error.reason : "failed",
      error: current.error };
  };
  const [created, setCreated] = createSignal<AccessKeyCreated | null>(null);
  const [createdCopied, setCreatedCopied] = createSignal(false);
  const [name, setName] = createSignal("");
  const [selectedCreateScopes, setSelectedCreateScopes] = createSignal<string[] | null>(null);
  const [selectedRequestScopes, setSelectedRequestScopes] = createSignal<Record<string, string[]>>({});
  const [denyReasons, setDenyReasons] = createSignal<Record<string, AccessRequestDenialReason>>({});
  const [outcomes, setOutcomes] = createSignal<AccessRequestOutcome[]>([]);
  const [error, setError] = createSignal<string | null>(null);
  const [pending, setPending] = createSignal<ActionTarget | null>(null);
  const [confirmRequest, setConfirmRequest] = createSignal<ConfirmDialogRecord | null>(null);
  let resolveConfirm: ((confirmed: boolean) => void) | null = null;
  let managerRegion: HTMLElement | undefined;
  let copyButton: HTMLButtonElement | undefined;
  let enableDefaultButton: HTMLButtonElement | undefined;
  let defaultHeading: HTMLElement | undefined;
  let keysHeading: HTMLElement | undefined;
  let requestsHeading: HTMLElement | undefined;
  let keyListSection: HTMLElement | undefined;

  const failureText = (reason: unknown, fallback: string) => {
    const kind = reason instanceof TrustedAccessRequestError ? reason.reason : "failed";
    return kind === "failed"
      ? userFacingErrorMessage(reason instanceof TrustedAccessRequestError ? reason.cause : reason, fallback)
      : props.copy.failureReasons[kind];
  };

  const load = () => keySource.refresh({ afterCurrent: true });
  onMount(() => {
    props.onFocusTargetChange?.(managerRegion);
  });
  onCleanup(() => props.onFocusTargetChange?.(undefined));

  const ready = () => {
    const state = keyList();
    return state.status === "ready" ? state.snapshot : null;
  };
  const failed = () => {
    const state = keyList();
    return state.status === "failed" ? state : null;
  };
  const acceptKeyCommand = <T,>(command: () => Promise<T>, update: (current: AccessKeySnapshot, result: T) => AccessKeySnapshot) =>
    acceptSourceCommand(runtime.sources.settingsReads.accessKeys, command,
      (result, current) => sourceCanRender(current) && current.data ? update(current.data, result) : undefined);

  let previousRequests = new Map<string, AccessRequestPending>();
  createEffect(() => {
    const current = pendingSnapshot();
    if (current?.status !== "ready" || current.data === null) {
      return;
    }
    const nextRequests = new Map(current.data.requests.map((request) => [request.requestId, request]));
    const expired = [...previousRequests.values()]
      .filter((request) => !nextRequests.has(request.requestId) && Date.parse(request.expiresAt) <= Date.now());
    if (expired.length > 0) {
      const existing = new Set(outcomes().map((outcome) => outcome.requestId));
      const next = expired
        .filter((request) => !existing.has(request.requestId))
        .map((request) => ({ requestId: request.requestId, name: request.name, status: "expired" as const }));
      if (next.length > 0) setOutcomes((previous) => [...next, ...previous].slice(0, 5));
    }
    previousRequests = nextRequests;
  });

  const confirm = (request: ConfirmDialogRequest) => new Promise<boolean>((resolve) => {
    resolveConfirm?.(false);
    resolveConfirm = resolve;
    setConfirmRequest({ ...request, id: crypto.randomUUID(), dedupeKey: request.title });
  });
  const finishConfirm = (confirmed: boolean) => {
    const resolve = resolveConfirm;
    resolveConfirm = null;
    setConfirmRequest(null);
    resolve?.(confirmed);
  };
  // Cancel pending confirmation and discard one-time keys when the page hides or unmounts.
  onPageLeave(() => {
    finishConfirm(false);
    setCreated(null);
  });

  const availableScopes = () => ready()?.availableScopes ?? [];
  /** Allow only recognized scopes the backend currently permits granting. */
  const grantable = (scope: string) => isKnownAccessScope(scope) && availableScopes().includes(scope);
  const createOptions = () => availableScopes().filter(isKnownAccessScope);
  // Mark scopes requiring an elevated administrator holder.
  const administratorHolder = (scope: string) => ready()?.administratorHolderScopes.includes(scope) ?? false;
  // New keys select read access by default; request approvals select all requested scopes.
  const createScopes = () => selectedCreateScopes() ?? (createOptions().includes("read") ? ["read"] : []);
  const setCreateScope = (scope: string, checked: boolean) => {
    const current = new Set(createScopes());
    if (checked) current.add(scope);
    else current.delete(scope);
    setSelectedCreateScopes([...current]);
  };
  const requestScopes = (request: AccessRequestPending) => {
    const selected = selectedRequestScopes()[request.requestId] ?? [...request.requestedScopes];
    return selected.filter((scope) => grantable(scope) && request.requestedScopes.includes(scope));
  };
  const setRequestScope = (request: AccessRequestPending, scope: string, checked: boolean) => {
    const selected = new Set(requestScopes(request));
    if (checked) selected.add(scope);
    else selected.delete(scope);
    setSelectedRequestScopes((previous) => ({ ...previous, [request.requestId]: [...selected] }));
  };
  const rememberOutcome = (request: AccessRequestPending, result: AccessRequestDecisionResult) => {
    setOutcomes((previous) => [
      { requestId: request.requestId, name: request.name, status: result.status, denialReason: result.denialReason },
      ...previous.filter((outcome) => outcome.requestId !== request.requestId)
    ].slice(0, 5));
  };
  /** Return success as a boolean; restore buttons before moving focus. */
  const runAction = async (target: ActionTarget, fallback: string, action: () => Promise<void>) => {
    if (pending() !== null) return false;
    setPending(target);
    setError(null);
    try {
      await action();
      return true;
    } catch (reason) {
      setError(failureText(reason, fallback));
      return false;
    } finally {
      setPending(null);
    }
  };
  const busy = () => pending() !== null;
  const independentKeys = () => (ready()?.keys ?? []).filter((key) => !key.isDefault);
  const defaultKey = () => ready()?.keys.find((key) => key.isDefault) ?? null;

  const showCreated = (result: AccessKeyCreated) => {
    setCreated(result);
    setCreatedCopied(false);
    copyButton?.focus();
  };
  const generate = () => runAction("create", props.copy.actionFailed, async () => {
    const displayName = name().trim();
    if (!displayName || createScopes().length === 0) return;
    const result = await acceptKeyCommand(() => createAccessKey(displayName, createScopes(), props.copy.actionFailed),
      (current, result) => ({ ...current, keys: [...current.keys, result.key] }));
    setName("");
    setSelectedCreateScopes(null);
    showCreated(result);
  });
  const rotate = async (key: AccessKeySummary) => {
    if (busy() || !await confirm({
      title: props.copy.confirmRotateTitle(key.name),
      message: props.copy.confirmRotateMessage,
      confirmLabel: props.copy.rotate,
      tone: "warning"
    })) return;
    await runAction(`key:${key.id}`, props.copy.actionFailed, async () => {
      const result = await acceptKeyCommand(() => rotateAccessKey(key.id, props.copy.actionFailed), (current, result) => ({
        ...current,
        keys: [...current.keys.filter((candidate) => candidate.id !== key.id), result.key]
      }));
      showCreated(result);
    });
  };
  const revoke = async (key: AccessKeySummary) => {
    if (busy() || !await confirm({
      title: props.copy.confirmRevokeTitle(key.name),
      message: props.copy.confirmRevokeMessage,
      confirmLabel: props.copy.revoke,
      tone: "error"
    })) return;
    const index = independentKeys().findIndex((candidate) => candidate.id === key.id);
    if (!await runAction(`key:${key.id}`, props.copy.actionFailed, async () => {
      await acceptKeyCommand(() => revokeAccessKey(key.id, props.copy.actionFailed),
        (current) => ({ ...current, keys: current.keys.filter((candidate) => candidate.id !== key.id) }));
      if (created()?.key.id === key.id) setCreated(null);
    })) return;
    // After revocation, focus the next row at the same position or the list heading.
    const rowButtons = [...keyListSection?.querySelectorAll<HTMLElement>('[data-key-action="rotate"]') ?? []];
    (rowButtons[Math.min(Math.max(index, 0), rowButtons.length - 1)] ?? keysHeading)?.focus();
  };
  const disableDefault = async () => {
    if (busy() || !await confirm({
      title: props.copy.confirmDisableTitle,
      message: props.copy.confirmDisableMessage,
      confirmLabel: props.copy.disableDefault,
      tone: "error"
    })) return;
    if (!await runAction("default", props.copy.actionFailed, async () => {
      // Use the returned list after default-key revocation without another read.
      await acceptKeyCommand(() => setDefaultAccessKey("disable", props.copy.actionFailed), (current) => ({
        ...current,
        defaultEnabled: false,
        defaultKeyId: null,
        keys: current.keys.filter((key) => !key.isDefault)
      }));
      setCreated(null);
    })) return;
    // Move focus to re-enable after the disable button disappears.
    enableDefaultButton?.focus();
  };
  const regenerateDefault = async () => {
    if (busy() || !await confirm({
      title: props.copy.confirmRegenerateTitle,
      message: props.copy.confirmRegenerateMessage,
      confirmLabel: props.copy.regenerateDefault,
      tone: "warning"
    })) return;
    await runAction("default", props.copy.actionFailed, async () => {
      await refreshAccessKeysAfterCommand(runtime.sources.settingsReads.accessKeys, async () => {
        await setDefaultAccessKey("rotate", props.copy.actionFailed);
        setCreated(null);
        // Reconnect default-key streams after rotation because the old streams were closed.
        runtime.reconnectSubscriptions();
      });
    });
  };
  const enableDefault = async () => {
    if (!await runAction("default", props.copy.actionFailed, async () => {
      await refreshAccessKeysAfterCommand(runtime.sources.settingsReads.accessKeys, async () => {
        await setDefaultAccessKey("enable", props.copy.actionFailed);
        // Reconnect after re-enabling even if scopes match; streams may have terminated with 401.
        runtime.reconnectSubscriptions();
      });
    })) return;
    // After re-enabling, focus the section heading or the whole region if reading failed.
    (defaultHeading?.isConnected ? defaultHeading : managerRegion)?.focus();
  };
  const decide = (request: AccessRequestPending, action: "approve" | "deny") =>
    runAction(`request:${request.requestId}`, props.copy.requestActionFailed, async () => {
      const result = await acceptKeyCommand(() => action === "approve"
        ? approveAccessRequest(request.requestId, requestScopes(request), props.copy.requestActionFailed)
        : denyAccessRequest(
          request.requestId,
          denyReasons()[request.requestId] ?? "not-approved",
          props.copy.requestActionFailed), (current, result) => result.key ? ({
          ...current,
          keys: [...current.keys.filter((key) => key.id !== result.key!.id), result.key!]
        }) : current);
      rememberOutcome(request, result);
      // Move focus to the request heading before subscription removal; announce the result in status.
      requestsHeading?.focus();
    });
  const copyCreatedSecret = async () => {
    const result = created();
    if (!result) return;
    try {
      await navigator.clipboard.writeText(result.apiKey);
      setCreatedCopied(true);
    } catch (reason) {
      setError(userFacingErrorMessage(reason, props.copy.copyFailed));
    }
  };
  const scopeLabel = (scope: string) => accessScopeLabel(props.copy, scope);
  const formatDate = (value: string) => userFacingDateTime(value);
  const outcomeText = (outcome: AccessRequestOutcome) => props.copy.requestOutcome(outcome.name,
    outcome.status === "approved" ? props.copy.requestApproved
      : outcome.status === "expired" ? props.copy.requestExpired
        : outcome.denialReason ? props.copy.requestDeniedWithReason(props.copy.denialReasons[outcome.denialReason])
          : props.copy.requestDenied);
  const denialOptions = createMemo(() => (Object.entries(props.copy.denialReasons) as Array<[AccessRequestDenialReason, string]>)
    .map(([value, label]) => ({ value, label })));
  const enableDefaultAction = () => (
    <button type="button" ref={(element) => { enableDefaultButton = element; }} disabled={busy()}
      onClick={() => void enableDefault()}>
      <ShieldCheck size={16} /> {pending() === "default" ? props.copy.working : props.copy.enableDefault}
    </button>
  );

  return (
    // Keep the accessible region name without duplicating its visible heading.
    <section ref={managerRegion} id="trustedAccessManager" class="settings-row settings-row-block trusted-access-manager"
      aria-label={props.copy.title} tabindex={-1} {...frontendVisibilitySurface(workId, [workId])}>
      <div class="settings-row-copy">
        <span>{props.copy.description}</span>
      </div>

      <Show when={keyList().status === "loading"}>
        <p role="status" class="trusted-access-state">{props.copy.loading}</p>
      </Show>
      <Show when={failed()}>
        {(failure) => (
          <div class="trusted-access-error trusted-access-load-failure" role="alert">
            <span>{failureText(failure().error, props.copy.loadFailed)}</span>
            <div class="trusted-access-actions">
              <button type="button" class="secondary" disabled={busy()} onClick={() => {
                // Explicit retry also reconnects streams because authorization may have changed elsewhere.
                runtime.reconnectSubscriptions();
                void load();
              }}>
                {props.copy.retry}
              </button>
            </div>
          </div>
        )}
      </Show>

      <Show when={ready()}>
        {(value) => (
          <>
            <section class="trusted-access-default" aria-labelledby="trustedAccessDefaultTitle">
              <div class="trusted-access-section-heading">
                <div class="settings-row-copy">
                  <div class="settings-row-title">
                    <strong id="trustedAccessDefaultTitle" tabindex={-1}
                      ref={(element) => { defaultHeading = element; }}>{props.copy.defaultTitle}</strong>
                    <HelpHint label={props.copy.defaultTitle}>{props.copy.defaultDescription}</HelpHint>
                  </div>
                </div>
                <span class="trusted-access-status" role="status">
                  {value().defaultEnabled ? props.copy.defaultEnabled : props.copy.defaultDisabled}
                </span>
              </div>
              <div class="trusted-access-default-path">
                <span>{props.copy.defaultFilePath}</span>
                <code>{props.copy.defaultFileLocation}</code>
              </div>
              <Show when={defaultKey()}>
                {(key) => (
                  <div class="trusted-access-key-summary">
                    <strong>{props.copy.defaultKeyName}</strong>
                    <span>{props.copy.createdAt} {formatDate(key().createdAt)}</span>
                    <div class="trusted-access-scope-list">
                      <For each={key().scopes}>{(scope) =>
                        <span class="trusted-access-scope">{scopeLabel(scope)}</span>
                      }</For>
                    </div>
                  </div>
                )}
              </Show>
              <Show when={value().defaultEnabled} fallback={
                <>
                  <p class="trusted-access-warning" role="note">{props.copy.defaultDisabledRecovery}</p>
                  <div class="trusted-access-actions">{enableDefaultAction()}</div>
                </>
              }>
                <div class="trusted-access-actions">
                  <button type="button" class="secondary" disabled={busy()} onClick={() => void disableDefault()}>
                    <Trash2 size={16} /> {props.copy.disableDefault}
                  </button>
                  <button type="button" class="secondary" disabled={busy()} onClick={() => void regenerateDefault()}>
                    <RefreshCw size={16} /> {pending() === "default" ? props.copy.working : props.copy.regenerateDefault}
                  </button>
                </div>
                <p class="trusted-access-warning" role="note">{props.copy.defaultAccessLossWarning}</p>
              </Show>
              <p class="trusted-access-native-secret">{props.copy.defaultSecretLocalOnly}</p>
            </section>

            <section class="trusted-access-create" aria-label={props.copy.createTitle}>
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{props.copy.createTitle}</strong>
                  <HelpHint label={props.copy.createTitle}>{props.copy.createDescription}</HelpHint>
                </div>
              </div>
              <label class="trusted-access-name-field">
                <span>{props.copy.nameLabel}</span>
                <input class="settings-text-field" type="text" maxlength={80}
                  value={name()} placeholder={props.copy.namePlaceholder}
                  onInput={(event) => setName(event.currentTarget.value)} />
              </label>
              <fieldset class="trusted-access-scopes">
                <legend>{props.copy.createScopesLabel}</legend>
                <For each={createOptions()}>{(scope) => (
                  <label class="trusted-access-scope-option">
                    <input type="checkbox" checked={createScopes().includes(scope)} disabled={busy()}
                      onChange={(event) => setCreateScope(scope, event.currentTarget.checked)} />
                    <span>{scopeLabel(scope)}<Show when={administratorHolder(scope)}>
                      <small class="trusted-access-scope-holder">{props.copy.administratorHolderScope}</small>
                    </Show></span>
                  </label>
                )}</For>
                <Show when={createOptions().length === 0}>
                  <span class="trusted-access-muted">{props.copy.noScopesAvailable}</span>
                </Show>
              </fieldset>
              <div class="trusted-access-actions">
                <button type="button" disabled={busy() || !name().trim() || createScopes().length === 0}
                  onClick={() => void generate()}>
                  <KeyRound size={16} /> {pending() === "create" ? props.copy.working : props.copy.create}
                </button>
              </div>
            </section>

            <Show when={created()}>
              {(result) => (
                <div class="trusted-access-one-time">
                  <div class="settings-row-copy">
                    <strong>{props.copy.oneTimeTitle}</strong>
                    <span>{props.copy.oneTimeDescription}</span>
                  </div>
                  <div class="trusted-access-secret-row">
                    <span>{props.copy.apiKeyLabel}</span>
                    <code>{result().apiKey}</code>
                    <button type="button" class="secondary" ref={(element) => { copyButton = element; }}
                      onClick={() => void copyCreatedSecret()}>
                      <Copy size={15} /> {createdCopied() ? props.copy.copied : props.copy.copy}
                    </button>
                  </div>
                  <button type="button" class="secondary trusted-access-dismiss"
                    onClick={() => setCreated(null)}>{props.copy.dismissSecret}</button>
                </div>
              )}
            </Show>

            <section class="trusted-access-key-list" aria-labelledby="trustedAccessKeysTitle"
              ref={(element) => { keyListSection = element; }}>
              <strong id="trustedAccessKeysTitle" tabindex={-1}
                ref={(element) => { keysHeading = element; }}>{props.copy.keysTitle}</strong>
              <For each={independentKeys()}>
                {(key) => (
                  <div class="trusted-access-key-row">
                    <div class="trusted-access-key-summary">
                      <strong>{key.name}</strong>
                      <span>{props.copy.createdAt} {formatDate(key.createdAt)}</span>
                      <div class="trusted-access-scope-list">
                        <For each={key.scopes}>{(scope) =>
                          <span class="trusted-access-scope">{scopeLabel(scope)}</span>
                        }</For>
                      </div>
                    </div>
                    <div class="trusted-access-actions">
                      <button type="button" class="secondary" disabled={busy()} data-key-action="rotate"
                        aria-label={props.copy.rotateKeyLabel(key.name)}
                        onClick={() => void rotate(key)}>
                        <RefreshCw size={16} /> {pending() === `key:${key.id}` ? props.copy.working : props.copy.rotate}
                      </button>
                      <button type="button" class="settings-icon-button" disabled={busy()}
                        aria-label={props.copy.revokeKeyLabel(key.name)} data-name-tooltip=""
                        onClick={() => void revoke(key)}>
                        <Trash2 size={17} />
                      </button>
                    </div>
                  </div>
                )}
              </For>
              <Show when={independentKeys().length === 0}>
                <span class="trusted-access-muted">{props.copy.keysEmpty}</span>
              </Show>
            </section>
          </>
        )}
      </Show>
      {/* Announce key creation without reading the one-time key aloud. */}
      <p class="sr-only" role="status">{created() ? props.copy.oneTimeAnnouncement : ""}</p>

      <section class="trusted-access-requests" aria-labelledby="trustedAccessRequestsTitle">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong id="trustedAccessRequestsTitle" tabindex={-1}
              ref={(element) => { requestsHeading = element; }}>{props.copy.requestsTitle}</strong>
            <HelpHint label={props.copy.requestsTitle}>{props.copy.requestsDescription}</HelpHint>
          </div>
        </div>
        <Show when={requestList().notice}>
          {(notice) => (
            <p class={notice() === "error" || notice() === "unavailable" ? "trusted-access-error" : "trusted-access-state"}
              role={notice() === "error" || notice() === "unavailable" ? "alert" : "status"}>
              {accessRequestNoticeText(props.copy, notice())}
            </p>
          )}
        </Show>
        {/* Key request cards by ID so publications preserve open menus and focus. */}
        <For each={requestIds()}>{(requestId) => {
          const request = () => requestById().get(requestId)!;
          return <Show when={requestById().has(requestId)}>
            <article class="trusted-access-request" aria-label={props.copy.requestNamed(request().name)}>
              <div class="trusted-access-request-heading">
                <strong>{request().name}</strong>
                <span>{props.copy.expiresAt} {formatDate(request().expiresAt)}</span>
              </div>
              <div class="trusted-access-request-facts">
                <span>{props.copy.observedExecutable}</span>
                <code>{request().observedExecutablePath}</code>
                <span>{props.copy.observedProcess}</span>
                <code>{request().observedProcessId}</code>
                <span>{props.copy.submittedAt} {formatDate(request().submittedAt)}</span>
              </div>
              <p class="trusted-access-muted">{props.copy.observedCallerIsInformational}</p>
              <fieldset class="trusted-access-scopes">
                <legend>{props.copy.requestScopesLabel}</legend>
                <Show when={request().requestedScopes.some((scope) => !grantable(scope))}>
                  <span class="trusted-access-muted">{props.copy.requestUnavailableScopeHelp}</span>
                </Show>
                <For each={request().requestedScopes}>{(scope) => (
                  <label class="trusted-access-scope-option">
                    <input type="checkbox" checked={requestScopes(request()).includes(scope)}
                      disabled={busy() || !grantable(scope)}
                      onChange={(event) => setRequestScope(request(), scope, event.currentTarget.checked)} />
                    <span>{scopeLabel(scope)}<Show when={administratorHolder(scope)}>
                      <small class="trusted-access-scope-holder">{props.copy.administratorHolderScope}</small>
                    </Show></span>
                  </label>
                )}</For>
              </fieldset>
              <div class="trusted-access-request-actions">
                <button type="button" disabled={busy() || requestScopes(request()).length === 0}
                  onClick={() => void decide(request(), "approve")}>
                  {pending() === `request:${request().requestId}` ? props.copy.working : props.copy.approve}
                </button>
                <label class="settings-select trusted-access-deny-reason">
                  <span>{props.copy.denyReasonLabel}</span>
                  <StandardSelect<AccessRequestDenialReason>
                    value={denyReasons()[request().requestId] ?? "not-approved"}
                    ariaLabel={props.copy.denyReasonLabel}
                    disabled={busy()}
                    options={denialOptions()}
                    onChange={(reason) => setDenyReasons((previous) => ({ ...previous, [request().requestId]: reason }))}
                  />
                </label>
                <button type="button" class="secondary" disabled={busy()}
                  onClick={() => void decide(request(), "deny")}>{props.copy.deny}</button>
              </div>
            </article>
          </Show>;
        }}</For>
        <Show when={requestList().empty}>
          <span class="trusted-access-muted">{props.copy.requestsEmpty}</span>
        </Show>
        <For each={outcomes()}>{(outcome) => (
          <p class={`trusted-access-outcome ${outcome.status}`} role="status">{outcomeText(outcome)}</p>
        )}</For>
      </section>

      <Show when={error()}>
        <p class="trusted-access-error" role="alert">{error()}</p>
      </Show>
      <ConfirmDialog
        request={confirmRequest()}
        confirmLabel={uiText.feedback.confirm}
        cancelLabel={uiText.feedback.cancel}
        closeLabel={uiText.feedback.close}
        onResolve={finishConfirm}
      />
    </section>
  );
}
