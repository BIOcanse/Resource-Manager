import { createMemo, createSignal, For, Show } from "solid-js";
import { Download, ExternalLink, Info, RefreshCw } from "lucide-solid";
import { UserDetailsDialog } from "../components/UserDetailsDialog";
import type { ManagedComponent } from "../types";
import type { BrowserRuntimeEntry, BrowserRuntimeSnapshot } from "./browserRuntimeTypes";

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
              <span>{props.actionLabel || "下载共享运行时"}</span>
            </button>
          </Show>
          <button
            class="secondary icon-text-button"
            type="button"
            disabled={props.refreshInProgress}
            onClick={props.onRefresh}
          >
            <RefreshCw size={16} aria-hidden="true" />
            <span>{props.refreshInProgress ? "正在刷新" : "刷新"}</span>
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
                title="打开官方来源"
                aria-label="打开官方来源"
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
              label="正在使用"
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
                  label={props.snapshot?.browserFallback?.id === browser.id ? "当前备用" : undefined}
                  onShowDetails={() => setSelectedRuntime(browser)}
                />
              )}
            </For>
          </Show>
        </div>
      </section>
      <UserDetailsDialog
        open={selectedRuntime() !== null}
        title={`${selectedRuntime()?.name ?? "运行时"}详细信息`}
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
        <span>版本 {props.runtime.version || "未知"}</span>
      </div>
      <Show when={props.label}>
        <span class="browser-runtime-selection">{props.label}</span>
      </Show>
      <button
        class="secondary icon-button browser-runtime-details-button"
        type="button"
        title="详细信息"
        aria-label={`${props.runtime.name}详细信息`}
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
    return "供本机 WebView2 软件共同使用的共享界面运行时。";
  }

  return runtime.kind === "GeckoBrowser"
    ? "供支持外部浏览器的软件打开 Web 界面；不能作为 WebView2 嵌入运行时。"
    : "供支持外部 Chromium 浏览器的软件复用。";
}

function runtimeDetailSections(runtime: BrowserRuntimeEntry | null) {
  if (!runtime) {
    return [];
  }

  return [{
    title: "运行时信息",
    items: [
      { label: "类型", value: runtimeKindLabel(runtime.kind) },
      { label: "版本", value: runtime.version || "未知" },
      { label: "状态", value: runtime.selected ? "正在使用" : "可用" },
      { label: "来源", value: runtimeSourceLabel(runtime.source) },
      { label: "运行目录", value: runtime.runtimeDirectory },
      { label: "可执行文件", value: runtime.executablePath }
    ]
  }];
}

function runtimeKindLabel(kind: BrowserRuntimeEntry["kind"]) {
  return ({
    WebView2Runtime: "共享 WebView2 运行时",
    ChromiumBrowser: "Chromium 浏览器",
    GeckoBrowser: "Gecko 浏览器"
  } as const)[kind];
}

function runtimeSourceLabel(source: string) {
  return ({
    SystemMachine: "系统（全机）",
    SystemUser: "系统（当前用户）",
    Managed: "资源管理器下载",
    BrowserRegistration: "系统浏览器注册",
    KnownInstall: "本机安装目录"
  } as Record<string, string>)[source] ?? "本机";
}
