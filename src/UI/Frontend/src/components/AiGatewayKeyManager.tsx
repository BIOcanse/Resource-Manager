import { Copy, KeyRound, Trash2 } from "lucide-solid";
import { createResource, createSignal, For, Show } from "solid-js";
import { createAiGatewayCredential, getAiGatewayCredentials, revokeAiGatewayCredential } from "../api";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import type { AiGatewayCompatibilityProfile, AiGatewayCredentialCreatedView } from "../types";
import { SegmentedControl } from "../ui/primitives/SegmentedControl.tsx";
import { uiText } from "../text.ts";

interface AiGatewayKeyManagerLabels {
  aiGatewayTitle: string;
  aiGatewayDescription: string;
  aiGatewayOpenAiProfile: string;
  aiGatewayAnthropicProfile: string;
  aiGatewayNamePlaceholder: string;
  aiGatewayGenerate: string;
  aiGatewayGenerating: string;
  aiGatewayOneTimeTitle: string;
  aiGatewayOneTimeDescription: string;
  aiGatewayApiKeyLabel: string;
  aiGatewayBaseUrlLabel: string;
  aiGatewayCopy: string;
  aiGatewayRevoke: string;
  aiGatewayEmpty: string;
  aiGatewayLoadFailed: string;
}

interface AiGatewayKeyManagerProps {
  enabled: boolean;
  labels: AiGatewayKeyManagerLabels;
}

export function AiGatewayKeyManager(props: AiGatewayKeyManagerProps) {
  const [profile, setProfile] = createSignal<AiGatewayCompatibilityProfile>("openai");
  const [displayName, setDisplayName] = createSignal("");
  const [created, setCreated] = createSignal<AiGatewayCredentialCreatedView | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);
  const [copiedField, setCopiedField] = createSignal<string | null>(null);
  const [credentials, { refetch }] = createResource(
    () => props.enabled,
    async () => getAiGatewayCredentials());

  const generate = async () => {
    if (!props.enabled || busy()) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const result = await createAiGatewayCredential(profile(), displayName());
      setCreated(result);
      setDisplayName("");
      await refetch();
    } catch (reason) {
      setError(userFacingErrorMessage(reason, uiText.misc.generateKeyFailed));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (credentialId: string) => {
    if (!props.enabled || busy()) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      await revokeAiGatewayCredential(credentialId);
      if (created()?.credential.id === credentialId) {
        setCreated(null);
      }
      await refetch();
    } catch (reason) {
      setError(userFacingErrorMessage(reason, uiText.misc.revokeKeyFailed));
    } finally {
      setBusy(false);
    }
  };

  const copy = async (field: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopiedField(field);
      window.setTimeout(() => setCopiedField((current) => current === field ? null : current), 1200);
    } catch (reason) {
      setError(userFacingErrorMessage(reason, uiText.misc.copyFailed));
    }
  };

  return (
    <div class="settings-row settings-row-block settings-debug-option ai-gateway-manager">
      <div class="settings-row-copy">
        <strong>{props.labels.aiGatewayTitle}</strong>
        <span>{props.labels.aiGatewayDescription}</span>
      </div>

      <div class="ai-gateway-create-row">
        <SegmentedControl
          value={profile()}
          options={[
            { id: "openai", label: props.labels.aiGatewayOpenAiProfile },
            { id: "anthropic", label: props.labels.aiGatewayAnthropicProfile }
          ] satisfies Array<{ id: AiGatewayCompatibilityProfile; label: string }>}
          ariaLabel={props.labels.aiGatewayTitle}
          class="settings-segmented-control"
          itemClass="settings-segment"
          disabled={!props.enabled}
          onChange={setProfile}
        />
        <input
          class="settings-text-field ai-gateway-name-field"
          type="text"
          maxlength={80}
          value={displayName()}
          placeholder={props.labels.aiGatewayNamePlaceholder}
          aria-label={props.labels.aiGatewayNamePlaceholder}
          disabled={!props.enabled || busy()}
          onInput={(event) => setDisplayName(event.currentTarget.value)}
        />
        <button type="button" disabled={!props.enabled || busy()} onClick={generate}>
          <KeyRound size={17} />
          {busy() ? props.labels.aiGatewayGenerating : props.labels.aiGatewayGenerate}
        </button>
      </div>

      <Show when={created()}>
        {(result) => (
          <div class="ai-gateway-created">
            <div class="settings-row-copy">
              <strong>{props.labels.aiGatewayOneTimeTitle}</strong>
              <span>{props.labels.aiGatewayOneTimeDescription}</span>
            </div>
            <GatewayValue
              id="key"
              label={props.labels.aiGatewayApiKeyLabel}
              value={result().apiKey}
              copyLabel={props.labels.aiGatewayCopy}
              copied={copiedField() === "key"}
              onCopy={copy}
            />
            <GatewayValue
              id="base-url"
              label={props.labels.aiGatewayBaseUrlLabel}
              value={result().credential.baseUrl}
              copyLabel={props.labels.aiGatewayCopy}
              copied={copiedField() === "base-url"}
              onCopy={copy}
            />
          </div>
        )}
      </Show>

      <Show when={error() || credentials.error}>
        <div class="ai-gateway-error">
          {error() ?? userFacingErrorMessage(credentials.error, props.labels.aiGatewayLoadFailed)}
        </div>
      </Show>

      <div class="ai-gateway-credential-list">
        <For each={credentials() ?? []}>
          {(credential) => (
            <div class="ai-gateway-credential-row">
              <div class="ai-gateway-credential-copy">
                <strong>{credential.displayName}</strong>
                <span>
                  {credential.compatibilityProfile === "openai"
                    ? props.labels.aiGatewayOpenAiProfile
                    : props.labels.aiGatewayAnthropicProfile}
                  {" · "}{new Date(credential.createdAt).toLocaleString()}
                </span>
                <code>{credential.baseUrl}</code>
              </div>
              <button
                class="settings-icon-button"
                type="button"
                aria-label={props.labels.aiGatewayRevoke}
                title={props.labels.aiGatewayRevoke}
                disabled={busy()}
                onClick={() => revoke(credential.id)}
              >
                <Trash2 size={17} />
              </button>
            </div>
          )}
        </For>
        <Show when={!credentials.loading && (credentials()?.length ?? 0) === 0}>
          <span class="ai-gateway-empty">{props.labels.aiGatewayEmpty}</span>
        </Show>
      </div>
    </div>
  );
}

interface GatewayValueProps {
  id: string;
  label: string;
  value: string;
  copyLabel: string;
  copied: boolean;
  onCopy: (id: string, value: string) => Promise<void>;
}

function GatewayValue(props: GatewayValueProps) {
  return (
    <div class="ai-gateway-value">
      <span>{props.label}</span>
      <code>{props.value}</code>
      <button
        class="secondary"
        type="button"
        aria-label={props.copyLabel}
        title={props.copyLabel}
        onClick={() => props.onCopy(props.id, props.value)}
      >
        <Copy size={16} />
        {props.copied ? "✓" : props.copyLabel}
      </button>
    </div>
  );
}
