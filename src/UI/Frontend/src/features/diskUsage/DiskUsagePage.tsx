import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { createSignal, For, Show } from "solid-js";
import type { JSX } from "solid-js";
import { formatBytes, formatBytePair } from "../../presentation/byteUnits.ts";
import { pickShellFolder } from "../../utils.ts";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl.tsx";
import { uiText } from "../../text.ts";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import type { FrontendVisibilitySurfaceAttributes } from "../../frontendWork/frontendVisibilitySurface.ts";
import type {
  DiskUsageScanMode,
  DiskUsageScanScope,
  DiskUsageVolume
} from "./diskUsageTypes.ts";

/** Scanning uses explicitly selected scope and mode; unavailable indexed scans must report the reason without silently traversing. */
export function DiskUsagePage(props: {
  volumes: DiskUsageVolume[];
  scanning: boolean;
  hasResult: boolean;
  resultReadState: "loading" | "ready" | "error";
  volumesReadState: "loading" | "ready" | "error";
  onRetryVolumes: () => void;
  onScan: (scope: DiskUsageScanScope, mode: DiskUsageScanMode, target: string) => void;
  /** Report action failures immediately with action-specific fallback text. */
  onActionError: (error: unknown, fallback: string) => void;
  /** Render an empty state until a scan result exists. */
  children?: JSX.Element;
  /** Page visibility governs volume reads, results, and layout work. */
  visibilitySurface: FrontendVisibilitySurfaceAttributes;
}) {
  const [scope, setScope] = createSignal<DiskUsageScanScope>("allVolumes");
  const [mode, setMode] = createSignal<DiskUsageScanMode>("fast");
  const [volumeId, setVolumeId] = createSignal("");
  const [folder, setFolder] = createSignal("");
  // Collapse scan settings after results arrive; allow reopening for another scan.
  const [setupOpen, setSetupOpen] = createSignal(true);
  const collapsed = () => props.hasResult && !setupOpen();

  const scannableVolumes = () => props.volumes.filter((volume) => volume.isReady);
  const selectedVolume = () =>
    scannableVolumes().find((volume) => volume.volumeId === volumeId());

  // Without an index, disable fast scan with a reason and use full scan for this volume.
  // Preserve the selected mode when switching back to indexed volumes.
  const fastUnreachable = () => scope() === "volume"
    && selectedVolume() !== undefined
    && !selectedVolume()!.supportsMasterFileTable;
  const scanMode = (): DiskUsageScanMode => fastUnreachable() ? "full" : mode();

  const target = () => scope() === "volume"
    ? volumeId()
    : scope() === "folder"
      ? folder()
      : "";

  const canScan = () => !props.scanning
    && (scope() === "allVolumes"
      ? scannableVolumes().length > 0
      : target().length > 0);

  async function chooseFolder() {
    let chosen: string | null;
    try {
      chosen = await pickShellFolder(uiText.diskUsage.chooseFolder, folder() || undefined);
    } catch (error) {
      props.onActionError(error, uiText.diskUsage.chooseFolderFailed);
      return;
    }
    if (chosen) {
      setFolder(chosen);
      setScope("folder");
    }
  }

  return (
    <div class="disk-usage-page" {...props.visibilitySurface}>
      <Show when={!collapsed()}>
        <div class="panel disk-usage-setup">
          <div class="settings-row">
            <div class="settings-row-copy">
              <strong>{uiText.diskUsage.scopeLabel}</strong>
            </div>
            <SegmentedControl<DiskUsageScanScope>
              class="settings-segmented-control"
              itemClass="settings-segment"
              value={scope()}
              options={[
                { id: "allVolumes", label: uiText.diskUsage.scopeAllVolumes },
                { id: "volume", label: uiText.diskUsage.scopeVolume },
                { id: "folder", label: uiText.diskUsage.scopeFolder }
              ]}
              ariaLabel={uiText.diskUsage.scopeLabel}
              onChange={(value) => {
                setScope(value);
                if (value === "folder" && !folder()) {
                  void chooseFolder();
                }
              }}
            />
          </div>

          <div class="settings-row">
            <div class="settings-row-copy">
              <strong>{uiText.diskUsage.modeLabel}</strong>
            </div>
            <SegmentedControl<DiskUsageScanMode>
              class="settings-segmented-control"
              itemClass="settings-segment"
              value={scanMode()}
              options={[
                {
                  id: "fast",
                  label: uiText.diskUsage.modeFast,
                  description: fastUnreachable() ? uiText.diskUsage.fastUnsupported : uiText.diskUsage.modeFastHint,
                  disabled: fastUnreachable()
                },
                {
                  id: "full",
                  label: uiText.diskUsage.modeFull,
                  description: uiText.diskUsage.modeFullHint
                }
              ]}
              ariaLabel={uiText.diskUsage.modeLabel}
              onChange={setMode}
            />
          </div>

          <Show when={scope() === "folder"}>
            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{uiText.diskUsage.scopeFolder}</strong>
                <span>{folder() || uiText.diskUsage.folderNotChosen}</span>
              </div>
              <button class="secondary" type="button" onClick={() => void chooseFolder()}>
                {uiText.diskUsage.chooseFolder}
              </button>
            </div>
          </Show>

        </div>
      </Show>

      <Show when={scope() !== "folder" && !collapsed()}>
        <div class="panel disk-usage-volumes">
          <div class="panel-header">
            <div class="optimization-heading">
              <h2>{uiText.diskUsage.volumeColumnLabel}</h2>
            </div>
          </div>
          {/* Reserve a volume-card row while loading so scan controls and results keep their positions. */}
          <Show when={props.volumesReadState === "loading" && props.volumes.length === 0}>
            <p class="sr-only" role="status">{uiText.observationNotice.loading(uiText.diskUsage.volumeColumnLabel)}</p>
            <ul class="disk-usage-volume-list" aria-hidden="true">
              <li>
                <div class="disk-usage-volume pending">
                  <span class="disk-usage-volume-name">--</span>
                  <span class="disk-usage-volume-meta">--</span>
                  <span class="disk-usage-volume-size">--</span>
                  <span class="disk-usage-volume-bar" />
                </div>
              </li>
            </ul>
          </Show>
          <Show when={props.volumesReadState === "error"}>
            <div role="alert">
              <p>{uiText.diskUsage.volumesReadFailed}</p>
              <Show when={props.volumes.length > 0}><p>{uiText.diskUsage.retainedVolumesStale}</p></Show>
              <button type="button" onClick={props.onRetryVolumes}>{uiText.diskUsage.retryRead}</button>
            </div>
          </Show>
          <Show when={props.volumes.length > 0 || props.volumesReadState === "ready"}>
            <Show
              when={props.volumes.length > 0}
              fallback={<div class="optimization-empty">{uiText.diskUsage.noVolumes}</div>}
            >
              <ul class="disk-usage-volume-list">
                <For each={props.volumes}>
                  {(volume) => (
                    <li>
                      <button
                        type="button"
                        class="disk-usage-volume"
                        classList={{
                          selected: scope() === "volume" && volumeId() === volume.volumeId,
                          unavailable: !volume.isReady
                        }}
                        disabled={!volume.isReady}
                        aria-pressed={scope() === "volume" && volumeId() === volume.volumeId}
                        onClick={() => {
                          setVolumeId(volume.volumeId);
                          setScope("volume");
                        }}
                      >
                        <span class="disk-usage-volume-name">
                          {volume.volumeId}
                          <Show when={volume.label}>{` ${volume.label}`}</Show>
                        </span>
                        <span class="disk-usage-volume-meta">
                          {uiText.diskUsage.volumeKind[volume.volumeKind]}
                          <Show when={volume.fileSystem}>{` · ${volume.fileSystem}`}</Show>
                        </span>
                        <span class="disk-usage-volume-size">
                          <Show
                            when={volume.isReady}
                            fallback={uiText.diskUsage.notReady}
                          >
                            {uiText.diskUsage.freeOfTotal(
                              formatBytes(volume.freeBytes, "storage"),
                              formatBytes(volume.totalBytes, "storage"))}
                          </Show>
                        </span>
                        <span
                          class="disk-usage-volume-bar"
                          aria-hidden="true"
                          style={{
                            "--disk-usage-used": usedFraction(volume).toFixed(4)
                          }}
                        />
                      </button>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          </Show>
        </div>
      </Show>

      {/* Place scan after its scope, mode, and volume inputs. */}
      <div class="disk-usage-setup-actions">
        <Show when={props.hasResult}>
          <button
            class="secondary"
            type="button"
            aria-expanded={setupOpen()}
            onClick={() => setSetupOpen((open) => !open)}
          >
            <StableLabel
              labels={{ expand: uiText.diskUsage.expandSetup, collapse: uiText.diskUsage.collapseSetup }}
              current={setupOpen() ? "collapse" : "expand"}
            />
          </button>
        </Show>
        <button
          type="button"
          disabled={!canScan()}
          onClick={() => props.onScan(scope(), scanMode(), target())}
        >
          <StableLabel
            labels={{ scan: uiText.diskUsage.scan, rescan: uiText.diskUsage.rescan, scanning: uiText.diskUsage.scanning }}
            current={props.scanning ? "scanning" : props.hasResult ? "rescan" : "scan"}
          />
        </button>
      </div>

      <Show when={!props.hasResult && props.resultReadState !== "error"}>
        <div class="panel disk-usage-result">
          <ContentState
            kind={props.resultReadState === "loading" ? "waiting" : "empty"}
            title={props.resultReadState === "loading"
              ? uiText.observationNotice.loading(uiText.diskUsage.title) : uiText.diskUsage.emptyTitle}
            detail={props.resultReadState === "loading" ? undefined : uiText.diskUsage.emptyDetail}
          />
        </div>
      </Show>
      {props.children}
    </div>
  );
}

// The usage ratio only paints the background; missing capacity contributes no fill.
function usedFraction(volume: DiskUsageVolume) {
  if (!volume.isReady || volume.totalBytes <= 0) {
    return 0;
  }
  const used = volume.totalBytes - volume.freeBytes;
  return Math.max(0, Math.min(1, used / volume.totalBytes));
}

// Display used and total capacity together.
export function volumeUsageText(volume: DiskUsageVolume) {
  return formatBytePair(
    Math.max(0, volume.totalBytes - volume.freeBytes),
    volume.totalBytes,
    "storage");
}
