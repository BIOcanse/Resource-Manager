import { Copy, KeyRound, RefreshCw, ShieldCheck, Trash2 } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, onCleanup, onMount, Show } from "solid-js";
import { StandardSelect } from "../../../components/StandardSelect.tsx";
import { userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { uiText, currentLanguage } from "../../../text.ts";
import { ConfirmDialog, type ConfirmDialogRecord, type ConfirmDialogRequest } from "../../../ui/patterns/ConfirmDialog.tsx";
import { useAccessRequestSnapshot } from "../../trustedAccess/AccessRequestSnapshotContext.tsx";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import {
  approveAccessRequest,
  createAccessKey,
  denyAccessRequest,
  enableDefaultAccessKeyElevated,
  getAccessKeySnapshot,
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

interface AccessRequestOutcome {
  readonly requestId: string;
  readonly name: string;
  readonly status: AccessRequestDecisionResult["status"];
  readonly denialReason?: AccessRequestDecisionResult["denialReason"];
}

/** 密钥列表的读取状态：只有 ready 时才显示密钥数据；失败时显示原因和重试，不沿用旧数据。 */
type KeyListState =
  | { readonly status: "loading" }
  | { readonly status: "ready"; readonly snapshot: AccessKeySnapshot }
  | { readonly status: "failed"; readonly reason: TrustedAccessFailureReason; readonly error: unknown };

/** 正在进行的操作对象：create、default、key:<id>、request:<id>。一次只进行一个操作。 */
type ActionTarget = "create" | "default" | `key:${string}` | `request:${string}`;

export function TrustedAccessManager(props: {
  copy: TrustedAccessCopy;
  onHeadingChange?: (heading: HTMLElement | undefined) => void;
}) {
  const runtime = useFrontendRuntime();
  const pendingSnapshot = useAccessRequestSnapshot();
  const requestList = createMemo(() => accessRequestListView(pendingSnapshot()));
  const requestById = createMemo(() => new Map(requestList().requests.map((request) => [request.requestId, request])));
  const requestIds = createMemo(() => requestList().requests.map((request) => request.requestId),
    [], { equals: (previous, next) => previous.length === next.length && previous.every((id, index) => id === next[index]) });
  const [keyList, setKeyList] = createSignal<KeyListState>({ status: "loading" });
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
  let managerHeading: HTMLElement | undefined;
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

  /** 读取密钥列表。showLoading 为 false 时，读取期间保留当前列表，读取失败仍切到失败状态。 */
  const load = async (showLoading = true) => {
    if (showLoading) setKeyList({ status: "loading" });
    try {
      setKeyList({ status: "ready", snapshot: await getAccessKeySnapshot(props.copy.loadFailed) });
    } catch (reason) {
      setKeyList({
        status: "failed",
        reason: reason instanceof TrustedAccessRequestError ? reason.reason : "failed",
        error: reason
      });
    }
  };
  onMount(() => {
    props.onHeadingChange?.(managerHeading);
    void load();
  });
  onCleanup(() => props.onHeadingChange?.(undefined));

  const ready = () => {
    const state = keyList();
    return state.status === "ready" ? state.snapshot : null;
  };
  const failed = () => {
    const state = keyList();
    return state.status === "failed" ? state : null;
  };
  const updateSnapshot = (update: (current: AccessKeySnapshot) => AccessKeySnapshot) => {
    const state = keyList();
    if (state.status === "ready") setKeyList({ status: "ready", snapshot: update(state.snapshot) });
  };

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
  onCleanup(() => finishConfirm(false));

  const availableScopes = () => ready()?.availableScopes ?? [];
  /** 本版本认识、且后端当前允许授予的范围才能勾选。 */
  const grantable = (scope: string) => isKnownAccessScope(scope) && availableScopes().includes(scope);
  const createOptions = () => availableScopes().filter(isKnownAccessScope);
  // 用户裁定（2026-10-25）：新建密钥默认只勾选“读取接口”；批准请求默认勾选请求申请的全部范围。
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
  /** 进行一个操作；成功返回 true。操作期间所有操作按钮不可用，焦点移动放在操作结束、按钮恢复之后。 */
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
    const result = await createAccessKey(displayName, createScopes(), props.copy.actionFailed);
    updateSnapshot((current) => ({ ...current, keys: [...current.keys, result.key] }));
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
      const result = await rotateAccessKey(key.id, props.copy.actionFailed);
      updateSnapshot((current) => ({
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
      await revokeAccessKey(key.id, props.copy.actionFailed);
      updateSnapshot((current) => ({ ...current, keys: current.keys.filter((candidate) => candidate.id !== key.id) }));
      if (created()?.key.id === key.id) setCreated(null);
    })) return;
    // 被撤销的行已移除：焦点移到同一位置的下一行，没有行时移到列表标题。
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
      await setDefaultAccessKey("disable", props.copy.actionFailed);
      // 关闭后界面已没有可用的密钥，不再读取列表；按返回结果更新。
      updateSnapshot((current) => ({
        ...current,
        defaultEnabled: false,
        defaultKeyId: null,
        keys: current.keys.filter((key) => !key.isDefault)
      }));
      setCreated(null);
    })) return;
    // 关闭按钮已经消失：焦点交给恢复入口。
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
      await setDefaultAccessKey("rotate", props.copy.actionFailed);
      setCreated(null);
      // 界面用的就是默认密钥：旧密钥上的推送流已被后端结束，按新密钥重连。
      runtime.reconnectSubscriptions();
      await load(false);
    });
  };
  const enableDefault = async () => {
    if (!await runAction("default", props.copy.actionFailed, async () => {
      await enableDefaultAccessKeyElevated(props.copy.actionFailed);
      // 关闭期间推送流收到 401 已终止，集合没变也要重连。
      runtime.reconnectSubscriptions();
      await load(keyList().status !== "ready");
    })) return;
    // 恢复入口已经消失：焦点交给默认密钥区的标题（读取失败时交给整个区的标题）。
    (defaultHeading?.isConnected ? defaultHeading : document.getElementById("trustedAccessManagerTitle"))?.focus();
  };
  const decide = (request: AccessRequestPending, action: "approve" | "deny") =>
    runAction(`request:${request.requestId}`, props.copy.requestActionFailed, async () => {
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
      // 这张请求卡片会随订阅更新移除：焦点先移到请求区标题，结果由状态行播报。
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
  const formatDate = (value: string) => new Date(value).toLocaleString(currentLanguage());
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
      <ShieldCheck size={16} /> {pending() === "default" ? props.copy.working : props.copy.enableDefaultElevated}
    </button>
  );

  return (
    <section id="trustedAccessManager" class="settings-row settings-row-block trusted-access-manager"
      aria-labelledby="trustedAccessManagerTitle">
      <div class="settings-row-copy">
        <strong ref={managerHeading} id="trustedAccessManagerTitle" tabindex={-1}>{props.copy.title}</strong>
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
                // 显式重试：授权可能已在别处恢复（界面外的管理员启用），推送流也一起重连。
                runtime.reconnectSubscriptions();
                void load();
              }}>
                {props.copy.retry}
              </button>
              <Show when={failure().reason === "unauthorized"}>{enableDefaultAction()}</Show>
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
                  <strong id="trustedAccessDefaultTitle" tabindex={-1}
                    ref={(element) => { defaultHeading = element; }}>{props.copy.defaultTitle}</strong>
                  <span>{props.copy.defaultDescription}</span>
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
                <For each={createOptions()}>{(scope) => (
                  <label class="trusted-access-scope-option">
                    <input type="checkbox" checked={createScopes().includes(scope)} disabled={busy()}
                      onChange={(event) => setCreateScope(scope, event.currentTarget.checked)} />
                    <span>{scopeLabel(scope)}</span>
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
                        aria-label={props.copy.revokeKeyLabel(key.name)} title={props.copy.revoke}
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
      {/* 一次性密钥只在页面上显示；读屏只播报“已生成，请复制”，不朗读密钥本身。 */}
      <p class="sr-only" role="status">{created() ? props.copy.oneTimeAnnouncement : ""}</p>

      <section class="trusted-access-requests" aria-labelledby="trustedAccessRequestsTitle">
        <div class="settings-row-copy">
          <strong id="trustedAccessRequestsTitle" tabindex={-1}
            ref={(element) => { requestsHeading = element; }}>{props.copy.requestsTitle}</strong>
          <span>{props.copy.requestsDescription}</span>
        </div>
        <Show when={requestList().notice}>
          {(notice) => (
            <p class={notice() === "error" || notice() === "unavailable" ? "trusted-access-error" : "trusted-access-state"}
              role={notice() === "error" || notice() === "unavailable" ? "alert" : "status"}>
              {accessRequestNoticeText(props.copy, notice())}
            </p>
          )}
        </Show>
        {/* 订阅每次发布都是新对象：按请求 ID 保留卡片，打开的下拉框和焦点不会因重新发布而被替换。 */}
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
                    <span>{scopeLabel(scope)}</span>
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
