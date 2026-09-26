import { onCleanup, onMount, Show } from "solid-js";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { uiText } from "../../text.ts";
import type { DiskUsageNode } from "./diskUsageLayoutTypes.ts";

/**
 * 方格图的右键菜单。
 *
 * 上半部分是**事实条目**：文件名、类型、大小、完整路径。它们不可点击，
 * 但文本可以选中复制 —— 用户要看这些信息时不用再点一次「属性」。
 * 下半部分才是动作。
 */
export function DiskUsageContextMenu(props: {
  node: DiskUsageNode;
  clientX: number;
  clientY: number;
  onClose: () => void;
  onOpenLocation: () => void;
  onProperties: () => void;
  onCopyPath: () => void;
  onDrillDown: () => void;
}) {
  let menu: HTMLDivElement | undefined;

  onMount(() => {
    // 点到别处或按 Esc 就关。指针按下时关，免得菜单挡住下一次点击。
    const dismiss = (event: MouseEvent) => {
      if (menu && event.target instanceof Node && menu.contains(event.target)) {
        return;
      }
      props.onClose();
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        props.onClose();
      }
    };
    document.addEventListener("pointerdown", dismiss, true);
    document.addEventListener("keydown", escape);
    menu?.focus({ preventScroll: true });
    onCleanup(() => {
      document.removeEventListener("pointerdown", dismiss, true);
      document.removeEventListener("keydown", escape);
    });
  });

  // 贴着窗口边时往回收，免得菜单跑到屏幕外。
  const position = () => {
    const width = 320;
    const height = 260;
    return {
      left: `${Math.min(props.clientX, Math.max(8, window.innerWidth - width))}px`,
      top: `${Math.min(props.clientY, Math.max(8, window.innerHeight - height))}px`
    };
  };

  return (
    <div
      ref={(element) => { menu = element; }}
      class="disk-usage-context-menu"
      role="menu"
      tabIndex={-1}
      style={position()}
    >
      <div class="disk-usage-context-facts">
        <strong class="disk-usage-context-name">{props.node.name || props.node.fullPath}</strong>
        <dl>
          <dt>{props.node.isDirectory
            ? uiText.diskUsage.menuKindDirectory
            : uiText.diskUsage.menuKindFile}</dt>
          <dd>{formatBytes(props.node.sizeBytes, "storage")}</dd>
          <Show when={props.node.isDirectory}>
            <dt>{uiText.diskUsage.menuSize}</dt>
            <dd>{uiText.diskUsage.fileCount(props.node.fileCount)}</dd>
          </Show>
          <dt>{uiText.diskUsage.menuPath}</dt>
          <dd class="disk-usage-context-path">{props.node.fullPath}</dd>
        </dl>
      </div>

      <div class="disk-usage-context-actions">
        <Show when={props.node.isDirectory}>
          <button type="button" role="menuitem" onClick={props.onDrillDown}>
            {uiText.diskUsage.menuDrillDown}
          </button>
        </Show>
        <button type="button" role="menuitem" onClick={props.onOpenLocation}>
          {uiText.diskUsage.menuOpenLocation}
        </button>
        <button type="button" role="menuitem" onClick={props.onProperties}>
          {uiText.diskUsage.menuProperties}
        </button>
        <button type="button" role="menuitem" onClick={props.onCopyPath}>
          {uiText.diskUsage.menuCopyPath}
        </button>
      </div>
    </div>
  );
}
