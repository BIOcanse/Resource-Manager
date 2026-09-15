import { createMemo, createSignal, For, Show } from "solid-js";
import { Download, ExternalLink, Info, RefreshCw } from "lucide-solid";
import { UserDetailsDialog } from "../components/UserDetailsDialog";
import type { ManagedComponent } from "../types";
import type { BrowserRuntimeEntry, BrowserRuntimeSnapshot } from "./browserRuntimeTypes";
import { uiText } from "../text.ts";

interface BrowserRuntimePageProps {
  snapshot: BrowserRuntimeSnapshot | null;
  installComponent: ManagedComponent | undefined;
  actionLabel: string | undefined;
  refreshInProgress: boolean;
  onRefresh: () => void;
  onInstall: (component: ManagedComponent) => void;
}

export function BrowserRuntimePage(props: BrowserRuntimePageProps) {
  const [selectedRuntime, setSelectedRuntime] = createSignal<BrowserRuntimeEntry | null>(null);
  const browsers = createMemo(() =>
    (props.snapshot?.candidates ?? []).filter((candidate) => candidate.kind !== "WebView2Runtime"));
  const installAvailable = () => {
    const component = props.installComponent;
    return component && (component.canInstall || component.canDownload || component.installerAvailable);
  };

  return (
    <>
      <header class="browser-runtime-header">
        <div>
          <h2>运行时管理</h2>
          <p>查看 Web 界面运行环境和已安装的浏览器。</p>
        </div>
        <div class="browser-runtime-actions">
          <Show when={!props.snapshot?.sharedRuntime && installAvailable()}>
            <button
              class="primary icon-text-button"
              type="button"
              disabled={Boolean(props.actionLabel)}
              onClick={() => props.installComponent && props.onInstall(props.installComponent)}
            >
              <Download size={16} aria-hidden="true" />
              <span>{props.actionLabel || uiText.browserRuntime.downloadShared}</span>
            </button>
          </Show>
          <button
            class="secondary icon-text-button"
            type="button"
            disabled={props.refreshInProgress}
            onClick={props.onRefresh}
          >
            <RefreshCw size={16} aria-hidden="true" />
            <span>{props.refreshInProgress ? uiText.browserRuntime.refreshing : uiText.browserRuntime.refresh}</span>
          </button>
        </div>
      </header>

      <section class="browser-runtime-current" aria-labelledby="sharedRuntimeHeading">
        <div class="browser-runtime-section-heading">
          <div>
            <h3 id="sharedRuntimeHeading">当前共享运行时</h3>
            <p>资源管理器与其它 WebView2 软件共用同一份系统运行时。</p>
          </div>
          <Show when={props.installComponent?.definition?.sourcePageUrl}>
            {(url) => (
              <button
                class="secondary icon-button"
                type="button"
                title={uiText.browserRuntime.openOfficialSource}
                aria-label={uiText.browserRuntime.openOfficialSource}
                onClick={() => window.open(String(url()), "_blank", "noopener")}
              >
                <ExternalLink size={16} aria-hidden="true" />
              </button>
            )}
          </Show>
        </div>
        <Show
          when={props.snapshot?.sharedRuntime}
          fallback={(
            <div class="browser-runtime-empty">
              <strong>未检测到共享运行时</strong>
              <span>可以安装官方共享运行时；现有浏览器仍会列入备用目录。</span>
            </div>
          )}
        >
          {(runtime) => (
            <RuntimeIdentity
              runtime={runtime()}
              label={uiText.browserRuntime.inUse}
              onShowDetails={() => setSelectedRuntime(runtime())}
            />
          )}
        </Show>
      </section>

      <section class="browser-runtime-browsers" aria-labelledby="browserCandidatesHeading">
        <div class="browser-runtime-section-heading">
          <div>
            <h3 id="browserCandidatesHeading">已安装的浏览器</h3>
            <p>支持外部浏览器的软件可以使用这些浏览器。</p>
          </div>
          <span class="browser-runtime-count">{browsers().length} 项</span>
        </div>
        <div class="browser-runtime-list">
          <Show
            when={browsers().length > 0}
            fallback={<div class="browser-runtime-empty">未发现可复用的浏览器。</div>}
          >
            <For each={browsers()}>
              {(browser) => (
                <RuntimeIdentity
                  runtime={browser}
                  label={props.snapshot?.browserFallback?.id === browser.id ? uiText.browserRuntime.currentFallback : undefined}
                  onShowDetails={() => setSelectedRuntime(browser)}
                />
              )}
            </For>
          </Show>
        </div>
      </section>
      <UserDetailsDialog
        open={selectedRuntime() !== null}
        title={uiText.browserRuntime.detailTitle(selectedRuntime()?.name ?? uiText.browserRuntime.fallbackRuntimeName)}
        summary={runtimeSummary(selectedRuntime())}
        sections={runtimeDetailSections(selectedRuntime())}
        onClose={() => setSelectedRuntime(null)}
      />
    </>
  );
}

function RuntimeIdentity(props: {
  runtime: BrowserRuntimeEntry;
  label?: string;
  onShowDetails: () => void;
}) {
  return (
    <article class="browser-runtime-row">
      <div class="browser-runtime-row-main">
        <strong>{props.runtime.name}</strong>
        <span>版本 {props.runtime.version || uiText.browserRuntime.unknown}</span>
      </div>
      <Show when={props.label}>
        <span class="browser-runtime-selection">{props.label}</span>
      </Show>
      <button
        class="secondary icon-button browser-runtime-details-button"
        type="button"
        title={uiText.browserRuntime.details}
        aria-label={uiText.browserRuntime.detailsOf(props.runtime.name)}
        onClick={props.onShowDetails}
      >
        <Info size={16} aria-hidden="true" />
      </button>
    </article>
  );
}

function runtimeSummary(runtime: BrowserRuntimeEntry | null) {
  if (!runtime) {
    return undefined;
  }

  if (runtime.kind === "WebView2Runtime") {
    return uiText.browserRuntime.sharedRuntimePurpose;
  }

  return runtime.kind === "GeckoBrowser"
    ? uiText.browserRuntime.externalBrowserPurpose
    : uiText.browserRuntime.chromiumReusePurpose;
}

function runtimeDetailSections(runtime: BrowserRuntimeEntry | null) {
  if (!runtime) {
    return [];
  }

  return [{
    title: uiText.browserRuntime.runtimeInfoTitle,
    items: [
      { label: uiText.browserRuntime.field.kind, value: runtimeKindLabel(runtime.kind) },
      { label: uiText.browserRuntime.field.version, value: runtime.version || uiText.browserRuntime.unknown },
      { label: uiText.browserRuntime.field.state, value: runtime.selected ? uiText.browserRuntime.inUse : uiText.browserRuntime.available },
      { label: uiText.browserRuntime.field.source, value: runtimeSourceLabel(runtime.source) },
      { label: uiText.browserRuntime.field.runtimeDirectory, value: runtime.runtimeDirectory },
      { label: uiText.browserRuntime.field.executable, value: runtime.executablePath }
    ]
  }];
}

function runtimeKindLabel(kind: BrowserRuntimeEntry["kind"]) {
  return ({
    WebView2Runtime: uiText.browserRuntime.kind.webView2Runtime,
    ChromiumBrowser: uiText.browserRuntime.kind.chromiumBrowser,
    GeckoBrowser: uiText.browserRuntime.kind.geckoBrowser
  } as const)[kind];
}

function runtimeSourceLabel(source: string) {
  return ({
    SystemMachine: uiText.browserRuntime.source.systemMachine,
    SystemUser: uiText.browserRuntime.source.systemUser,
    Managed: uiText.browserRuntime.source.managed,
    BrowserRegistration: uiText.browserRuntime.source.browserRegistration,
    KnownInstall: uiText.browserRuntime.source.knownInstall
  } as Record<string, string>)[source] ?? uiText.browserRuntime.source.local;
}
