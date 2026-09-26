import { createSignal, For, Show } from "solid-js";
import type { JSX } from "solid-js";
import { formatBytes, formatBytePair } from "../presentation/byteUnits.ts";
import { pickShellFolder } from "../utils.ts";
import { SegmentedControl } from "../ui/primitives/SegmentedControl.tsx";
import { uiText } from "../text.ts";
import type {
  DiskUsageScanMode,
  DiskUsageScanScope,
  DiskUsageVolume
} from "./diskUsageTypes.ts";

/**
 * 磁盘占用页。
 *
 * 这一版是骨架：选范围、选方式、选目标，然后开扫。扫描本身和方格图在后面的切片里接上。
 * 范围与方式都是用户显式选的 —— 快速扫描碰到没有文件系统索引的盘会明说扫不到，
 * 不会偷偷改用遍历。
 */
export function DiskUsagePage(props: {
  volumes: DiskUsageVolume[];
  scanning: boolean;
  onScan: (scope: DiskUsageScanScope, mode: DiskUsageScanMode, target: string) => void;
  /** 扫描结果面板。还没有结果时由这里出空态。 */
  children?: JSX.Element;
}) {
  const [scope, setScope] = createSignal<DiskUsageScanScope>("allVolumes");
  const [mode, setMode] = createSignal<DiskUsageScanMode>("fast");
  const [volumeId, setVolumeId] = createSignal("");
  const [folder, setFolder] = createSignal("");
  // 有结果之后默认收起设置区；用户想重扫再展开。
  const [setupOpen, setSetupOpen] = createSignal(true);
  const collapsed = () => Boolean(props.children) && !setupOpen();

  const scannableVolumes = () => props.volumes.filter((volume) => volume.isReady);
  const selectedVolume = () =>
    scannableVolumes().find((volume) => volume.volumeId === volumeId());

  // 快速扫描要文件系统索引。选中的盘没有索引时，把话说在前面。
  const fastUnreachable = () => mode() === "fast"
    && scope() === "volume"
    && selectedVolume() !== undefined
    && !selectedVolume()!.supportsMasterFileTable;

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
    const chosen = await pickShellFolder(uiText.diskUsage.chooseFolder, folder() || undefined);
    if (chosen) {
      setFolder(chosen);
      setScope("folder");
    }
  }

  return (
    <div class="disk-usage-page">
      <div class="panel disk-usage-setup" classList={{ collapsed: collapsed() }}>
        <div class="panel-header">
          <div class="disk-usage-setup-actions">
            <Show when={props.children}>
              <button
                class="secondary"
                type="button"
                aria-expanded={setupOpen()}
                onClick={() => setSetupOpen((open) => !open)}
              >
                {setupOpen()
                  ? uiText.diskUsage.collapseSetup
                  : uiText.diskUsage.expandSetup}
              </button>
            </Show>
            <button
              type="button"
              disabled={!canScan()}
              onClick={() => props.onScan(scope(), mode(), target())}
            >
              {props.scanning
                ? uiText.diskUsage.scanning
                : props.children
                  ? uiText.diskUsage.rescan
                  : uiText.diskUsage.scan}
            </button>
          </div>
        </div>

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
            value={mode()}
            options={[
              {
                id: "fast",
                label: uiText.diskUsage.modeFast,
                description: uiText.diskUsage.modeFastHint
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

        <Show when={fastUnreachable()}>
          <p class="disk-usage-warning" role="alert">{uiText.diskUsage.fastUnsupported}</p>
        </Show>
      </div>

      <Show when={scope() !== "folder" && !collapsed()}>
        <div class="panel disk-usage-volumes">
          <div class="panel-header">
            <div class="optimization-heading">
              <h2>{uiText.diskUsage.volumeColumnLabel}</h2>
            </div>
          </div>
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
        </div>
      </Show>

      <Show
        when={props.children}
        fallback={(
          <div class="panel disk-usage-result">
            <div class="optimization-empty disk-usage-empty">
              <strong>{uiText.diskUsage.emptyTitle}</strong>
              <span>{uiText.diskUsage.emptyDetail}</span>
            </div>
          </div>
        )}
      >
        {props.children}
      </Show>
    </div>
  );
}

// 已用比例只用于画那条细底纹，读不到容量时当作 0，不编造。
function usedFraction(volume: DiskUsageVolume) {
  if (!volume.isReady || volume.totalBytes <= 0) {
    return 0;
  }
  const used = volume.totalBytes - volume.freeBytes;
  return Math.max(0, Math.min(1, used / volume.totalBytes));
}

// 供后续切片使用：已用 / 总量的成对显示。
export function volumeUsageText(volume: DiskUsageVolume) {
  return formatBytePair(
    Math.max(0, volume.totalBytes - volume.freeBytes),
    volume.totalBytes,
    "storage");
}
