import { Copy, KeyRound, RefreshCw, Trash2 } from "lucide-solid";
import { createEffect, createResource, createSignal, For, Show } from "solid-js";
import { userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { useAccessRequestSnapshot } from "../../trustedAccess/AccessRequestSnapshotContext.tsx";
import {
  approveAccessRequest,
  createAccessKey,
  denyAccessRequest,
  getAccessKeySnapshot,
  revokeAccessKey,
  rotateAccessKey,
  setDefaultAccessKey
} from "../../trustedAccess/trustedAccessApi.ts";
import type {
  AccessKeyCreated,
  AccessKeySnapshot,
  AccessRequestDecisionResult,
  AccessRequestPending,
  DefaultAccessKeyState
} from "../../trustedAccess/trustedAccessTypes.ts";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";

type TrustedAccessCopy = SettingsTextBundle["systemIntegration"]["trustedAccess"];

interface AccessRequestOutcome {
  readonly requestId: string;
  readonly name: string;
  readonly status: AccessRequestDecisionResult["status"];
  readonly denialReason?: AccessRequestDecisionResult["denialReason"];
}

export function TrustedAccessManager(props: { copy: TrustedAccessCopy }) {
  const pendingSnapshot = useAccessRequestSnapshot();
  const [snapshot, { refetch, mutate }] = createResource<AccessKeySnapshot>(
    async () => getAccessKeySnapshot(props.copy.loadFailed));
  const [created, setCreated] = createSignal<AccessKeyCreated | null>(null);
  const [createdCopied, setCreatedCopied] = createSignal(false);
  const [name, setName] = createSignal("");
  const [selectedCreateScopes, setSelectedCreateScopes] = createSignal<string[] | null>(null);
  const [selectedRequestScopes, setSelectedRequestScopes] = createSignal<Record<string, string[]>>({});
  const [denyReasons, setDenyReasons] = createSignal<Record<string, NonNullable<AccessRequestDecisionResult["denialReason"]>>>({});
  const [outcomes, setOutcomes] = createSignal<AccessRequestOutcome[]>([]);
  const [error, setError] = createSignal<string | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [defaultStateOverride, setDefaultStateOverride] = createSignal<DefaultAccessKeyState | null>(null);

  createEffect(() => {
    const loaded = snapshot();
    const override = defaultStateOverride();
    if (loaded && override
      && loaded.defaultEnabled === override.defaultEnabled
      && loaded.defaultKeyId === override.defaultKeyId) {
      setDefaultStateOverride(null);
    }
  });

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

  const createScopes = () => selectedCreateScopes() ?? (snapshot()?.availableScopes.includes("read") ? ["read"] : []);
  const setCreateScope = (scope: string, checked: boolean) => {
    const current = new Set(createScopes());
    if (checked) current.add(scope);
    else current.delete(scope);
    setSelectedCreateScopes([...current]);
  };
  const requestScopes = (request: AccessRequestPending) => {
    const allowed = new Set(snapshot()?.availableScopes ?? []);
    const selected = selectedRequestScopes()[request.requestId] ?? [...request.requestedScopes];
    return selected.filter((scope) => allowed.has(scope) && request.requestedScopes.includes(scope));
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
  const updateSnapshot = (update: (current: AccessKeySnapshot) => AccessKeySnapshot) => {
    mutate((current) => current ? update(current) : current);
  };
  const runAction = async (action: () => Promise<void>) => {
    if (busy()) return;
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (reason) {
      setError(userFacingErrorMessage(reason, props.copy.actionFailed));
    } finally {
      setBusy(false);
    }
  };
  const generate = () => runAction(async () => {
    const displayName = name().trim();
    if (!displayName || createScopes().length === 0) return;
    const result = await createAccessKey(displayName, createScopes(), props.copy.actionFailed);
    updateSnapshot((current) => ({ ...current, keys: [...current.keys, result.key] }));
    setCreated(result);
    setCreatedCopied(false);
    setName("");
    setSelectedCreateScopes(null);
  });
  const rotate = (keyId: string) => runAction(async () => {
    const result = await rotateAccessKey(keyId, props.copy.actionFailed);
    updateSnapshot((current) => ({
      ...current,
      keys: [...current.keys.filter((key) => key.id !== keyId), result.key]
    }));
    setCreated(result);
    setCreatedCopied(false);
  });
  const revoke = (keyId: string) => runAction(async () => {
    await revokeAccessKey(keyId, props.copy.actionFailed);
    updateSnapshot((current) => ({ ...current, keys: current.keys.filter((key) => key.id !== keyId) }));
    if (created()?.key.id === keyId) setCreated(null);
  });
  const updateDefault = (enabled: boolean, rotateExisting = false) => runAction(async () => {
    // This route returns metadata only. The default secret remains owned by the native shell.
    const result = await setDefaultAccessKey(enabled, rotateExisting, props.copy.actionFailed);
    setDefaultStateOverride(result);
    setCreated(null);
  });
  const decide = (request: AccessRequestPending, action: "approve" | "deny") => runAction(async () => {
    const result = action === "approve"
      ? await approveAccessRequest(request.requestId, requestScopes(request), props.copy.requestActionFailed)
      : await denyAccessRequest(
        request.requestId,
        denyReasons()[request.requestId] ?? "not-approved",
        props.copy.requestActionFailed);
    if (result.key) {
      updateSnapshot((current) => ({
        ...current,
        keys: [...current.keys.filter((key) => key.id !== result.key!.id), result.key!]
      }));
    }
    rememberOutcome(request, result);
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
  const scopeLabel = (scope: string) => props.copy.scopeLabels[scope] ?? scope;
  const availableScopes = () => snapshot()?.availableScopes ?? [];
  const formatDate = (value: string) => new Date(value).toLocaleString();
  const pendingRequests = () => pendingSnapshot()?.data?.requests ?? [];
  const defaultEnabled = () => defaultStateOverride()?.defaultEnabled ?? snapshot()?.defaultEnabled ?? false;
  const defaultKeyId = () => {
    const override = defaultStateOverride();
    return override ? override.defaultKeyId : snapshot()?.defaultKeyId ?? null;
  };

  return (
    <section id="trustedAccessManager" class="settings-row settings-row-block settings-debug-option trusted-access-manager"
      aria-label={props.copy.title}>
      <div class="settings-row-copy">
        <strong>{props.copy.title}</strong>
        <span>{props.copy.description}</span>
      </div>

      <Show when={snapshot.loading}>
        <p role="status" class="trusted-access-state">{props.copy.loading}</p>
      </Show>
      <Show when={snapshot.error}>
        <div class="trusted-access-error" role="alert">
          {userFacingErrorMessage(snapshot.error, props.copy.loadFailed)}
          <button type="button" class="secondary-button" disabled={busy()} onClick={() => void refetch()}>
            {props.copy.retry}
          </button>
        </div>
      </Show>

      <Show when={snapshot()}>
        {(value) => (
          <>
            <section class="trusted-access-default" aria-label={props.copy.defaultTitle}>
              <div class="trusted-access-section-heading">
                <div class="settings-row-copy">
                  <strong>{props.copy.defaultTitle}</strong>
                  <span>{props.copy.defaultDescription}</span>
                </div>
                <span class="trusted-access-status" role="status">
                  {defaultEnabled() ? props.copy.defaultEnabled : props.copy.defaultDisabled}
                </span>
              </div>
              <div class="trusted-access-default-path">
                <span>{props.copy.defaultFilePath}</span>
                <code>UserData/Security/default-key</code>
              </div>
              <Show when={defaultKeyId()}>
                {(keyId) => {
                  const defaultKey = () => value().keys.find((key) => key.id === keyId());
                  return (
                    <div class="trusted-access-key-summary">
                      <strong>{props.copy.defaultKeyName}</strong>
                      <span>{props.copy.createdAt} {defaultKey() ? formatDate(defaultKey()!.createdAt) : "—"}</span>
                      <div class="trusted-access-scope-list">
                        <For each={defaultKey()?.scopes ?? []}>{(scope) =>
                          <span class="trusted-access-scope">{scopeLabel(scope)}</span>
                        }</For>
                      </div>
                    </div>
                  );
                }}
              </Show>
              <Show when={defaultKeyId() && !value().keys.some((key) => key.id === defaultKeyId())}>
                <button type="button" class="secondary-button" disabled={busy()}
                  onClick={() => void refetch()}>{props.copy.reloadMetadata}</button>
              </Show>
              <div class="trusted-access-actions">
                <Show when={!defaultEnabled()}>
                  <button type="button" disabled={busy()} onClick={() => void updateDefault(true)}>
                    <KeyRound size={16} /> {props.copy.enableDefault}
                  </button>
                </Show>
                <Show when={defaultEnabled()}>
                  <button type="button" class="secondary-button" disabled={busy()}
                    onClick={() => void updateDefault(false)}>
                    <Trash2 size={16} /> {props.copy.disableDefault}
                  </button>
                  <button type="button" class="secondary-button" disabled={busy()}
                    onClick={() => void updateDefault(true, true)}>
                    <RefreshCw size={16} /> {props.copy.regenerateDefault}
                  </button>
                </Show>
              </div>
              <p class="trusted-access-native-secret">{props.copy.defaultSecretLocalOnly}</p>
              <p class="trusted-access-warning" role="note">{props.copy.defaultAccessLossWarning}</p>
            </section>

            <section class="trusted-access-create" aria-label={props.copy.createTitle}>
              <div class="settings-row-copy">
                <strong>{props.copy.createTitle}</strong>
                <span>{props.copy.createDescription}</span>
              </div>
              <label class="trusted-access-name-field">
                <span>{props.copy.nameLabel}</span>
                <input class="settings-text-field" type="text" maxlength={80}
                  value={name()} placeholder={props.copy.namePlaceholder}
                  onInput={(event) => setName(event.currentTarget.value)} />
              </label>
              <fieldset class="trusted-access-scopes">
                <legend>{props.copy.createScopesLabel}</legend>
                <For each={value().availableScopes}>{(scope) => (
                  <label class="trusted-access-scope-option">
                    <input type="checkbox" checked={createScopes().includes(scope)}
                      onChange={(event) => setCreateScope(scope, event.currentTarget.checked)} />
                    <span>{scopeLabel(scope)}</span>
                  </label>
                )}</For>
                <Show when={value().availableScopes.length === 0}>
                  <span class="trusted-access-muted">{props.copy.noScopesAvailable}</span>
                </Show>
              </fieldset>
              <div class="trusted-access-actions">
                <button type="button" disabled={busy() || !name().trim() || createScopes().length === 0}
                  onClick={() => void generate()}>
                  <KeyRound size={16} /> {busy() ? props.copy.working : props.copy.create}
                </button>
              </div>
            </section>

            <Show when={created()}>
              {(result) => (
                <div class="trusted-access-one-time" role="status">
                  <div class="settings-row-copy">
                    <strong>{props.copy.oneTimeTitle}</strong>
                    <span>{props.copy.oneTimeDescription}</span>
                  </div>
                  <div class="trusted-access-secret-row">
                    <span>{props.copy.apiKeyLabel}</span>
                    <code>{result().apiKey}</code>
                    <button type="button" class="secondary-button" onClick={() => void copyCreatedSecret()}>
                      <Copy size={15} /> {createdCopied() ? props.copy.copied : props.copy.copy}
                    </button>
                  </div>
                  <button type="button" class="secondary-button trusted-access-dismiss"
                    onClick={() => setCreated(null)}>{props.copy.dismissSecret}</button>
                </div>
              )}
            </Show>

            <section class="trusted-access-key-list" aria-label={props.copy.keysTitle}>
              <strong>{props.copy.keysTitle}</strong>
              <For each={value().keys.filter((key) => key.purpose === "control" && !key.isDefault)}>
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
                      <button type="button" class="secondary-button" disabled={busy()}
                        aria-label={`${props.copy.rotate} · ${key.name}`}
                        onClick={() => void rotate(key.id)}>
                        <RefreshCw size={16} /> {props.copy.rotate}
                      </button>
                      <button type="button" class="settings-icon-button" disabled={busy()}
                        aria-label={`${props.copy.revoke} · ${key.name}`} title={props.copy.revoke}
                        onClick={() => void revoke(key.id)}>
                        <Trash2 size={17} />
                      </button>
                    </div>
                  </div>
                )}
              </For>
              <Show when={value().keys.filter((key) => key.purpose === "control" && !key.isDefault).length === 0}>
                <span class="trusted-access-muted">{props.copy.keysEmpty}</span>
              </Show>
            </section>
          </>
        )}
      </Show>

      <section class="trusted-access-requests" aria-label={props.copy.requestsTitle}>
        <div class="settings-row-copy">
          <strong>{props.copy.requestsTitle}</strong>
          <span>{props.copy.requestsDescription}</span>
        </div>
        <Show when={pendingSnapshot()?.status === "loading" || pendingSnapshot()?.status === "unavailable"}>
          <p class="trusted-access-state" role="status">{props.copy.requestsLoading}</p>
        </Show>
        <Show when={pendingSnapshot()?.status === "error"}>
          <p class="trusted-access-error" role="alert">{props.copy.requestsLoadFailed}</p>
        </Show>
        <For each={pendingRequests()}>{(request) => (
          <article class="trusted-access-request" aria-label={`${props.copy.requestName}: ${request.name}`}>
            <div class="trusted-access-request-heading">
              <strong>{request.name}</strong>
              <span>{props.copy.expiresAt} {formatDate(request.expiresAt)}</span>
            </div>
            <div class="trusted-access-request-facts">
              <span>{props.copy.observedExecutable}</span>
              <code>{request.observedExecutablePath}</code>
              <span>{props.copy.observedProcess}</span>
              <code>{request.observedProcessId}</code>
              <span>{props.copy.submittedAt} {formatDate(request.submittedAt)}</span>
            </div>
            <p class="trusted-access-muted">{props.copy.observedCallerIsInformational}</p>
            <fieldset class="trusted-access-scopes">
              <legend>{props.copy.requestScopesLabel}</legend>
              <Show when={request.requestedScopes.some((scope) => !availableScopes().includes(scope))}>
                <span class="trusted-access-muted">{props.copy.requestUnavailableScopeHelp}</span>
              </Show>
              <For each={request.requestedScopes}>{(scope) => (
                <label class="trusted-access-scope-option">
                  <input type="checkbox" checked={requestScopes(request).includes(scope)}
                    disabled={busy() || !availableScopes().includes(scope)}
                    onChange={(event) => setRequestScope(request, scope, event.currentTarget.checked)} />
                  <span>{scopeLabel(scope)}</span>
                </label>
              )}</For>
            </fieldset>
            <div class="trusted-access-request-actions">
              <button type="button" disabled={busy() || requestScopes(request).length === 0}
                onClick={() => void decide(request, "approve")}>
                {busy() ? props.copy.working : props.copy.approve}
              </button>
              <label class="trusted-access-deny-reason">
                <span>{props.copy.denyReasonLabel}</span>
                <select
                  value={denyReasons()[request.requestId] ?? "not-approved"}
                  disabled={busy()}
                  onChange={(event) => setDenyReasons((previous) => ({
                    ...previous,
                    [request.requestId]: event.currentTarget.value as NonNullable<AccessRequestDecisionResult["denialReason"]>
                  }))}>
                  <For each={Object.entries(props.copy.denialReasons) as Array<[NonNullable<AccessRequestDecisionResult["denialReason"]>, string]>}>
                    {([reason, label]) => <option value={reason}>{label}</option>}
                  </For>
                </select>
              </label>
              <button type="button" class="secondary-button" disabled={busy()}
                onClick={() => void decide(request, "deny")}>{props.copy.deny}</button>
            </div>
          </article>
        )}</For>
        <Show when={pendingSnapshot()?.status === "ready" && pendingRequests().length === 0}>
          <span class="trusted-access-muted">{props.copy.requestsEmpty}</span>
        </Show>
        <For each={outcomes()}>{(outcome) => (
          <p class={`trusted-access-outcome ${outcome.status}`} role="status">
            <strong>{outcome.name}</strong>{" · "}
            {outcome.status === "approved" ? props.copy.requestApproved
              : outcome.status === "expired" ? props.copy.requestExpired
                : props.copy.requestDenied}
            <Show when={outcome.status === "denied" && outcome.denialReason}>
              {" · "}{props.copy.denialReasons[outcome.denialReason!]}
            </Show>
          </p>
        )}</For>
      </section>

      <Show when={error()}>
        <p class="trusted-access-error" role="alert">{error()}</p>
      </Show>
    </section>
  );
}
