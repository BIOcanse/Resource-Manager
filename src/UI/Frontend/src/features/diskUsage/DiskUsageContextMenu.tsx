import { onCleanup, onMount, Show } from "solid-js";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { scheduleFocusRestore } from "../../interactions/focusReturn.ts";
import { moveRovingFocus } from "../../ui/primitives/RovingFocus.ts";
import { uiText } from "../../text.ts";
import type { DiskUsageNode } from "./diskUsageLayoutTypes.ts";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";

/** Show selectable file facts above actions so inspection does not require opening properties. */
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
    // Restore focus to the element that opened the menu when appropriate.
    // Preserve outside-click focus; restore the opener only if closing leaves focus on body.
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const openerFocusKey = opener?.dataset.focusKey;
    let closedByPointer = false;
    // Close on outside pointer down or Escape so the menu cannot block the next click.
    const dismiss = (event: MouseEvent) => {
      if (menu && event.target instanceof Node && menu.contains(event.target)) {
        return;
      }
      closedByPointer = true;
      props.onClose();
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        props.onClose();
      }
    };
    document.addEventListener("pointerdown", dismiss, true);
    document.addEventListener("keydown", escape);
    // Start keyboard navigation at the first action.
    (menu?.querySelector<HTMLElement>("[role='menuitem']") ?? menu)?.focus({ preventScroll: true });
    onCleanup(() => {
      document.removeEventListener("pointerdown", dismiss, true);
      document.removeEventListener("keydown", escape);
      if (!closedByPointer) {
        scheduleFocusRestore(opener, openerFocusKey);
      }
    });
  });

  // Keep the menu inside the viewport.
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
      ref={(element) => { menu = element; exitOnUnmount(element); }}
      class="disk-usage-context-menu"
      role="menu"
      tabIndex={-1}
      style={position()}
      onKeyDown={(event) => {
        // Tab closes the menu and follows the shared focus-restoration rule.
        if (event.key === "Tab") {
          event.preventDefault();
          props.onClose();
          return;
        }
        moveRovingFocus(event, {
          selector: "[role='menuitem']",
          previousKeys: ["ArrowUp"],
          nextKeys: ["ArrowDown"]
        });
      }}
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
          <button type="button" role="menuitem" tabIndex={-1} onClick={props.onDrillDown}>
            {uiText.diskUsage.menuDrillDown}
          </button>
        </Show>
        <button type="button" role="menuitem" tabIndex={-1} onClick={props.onOpenLocation}>
          {uiText.diskUsage.menuOpenLocation}
        </button>
        <button type="button" role="menuitem" tabIndex={-1} onClick={props.onProperties}>
          {uiText.diskUsage.menuProperties}
        </button>
        <button type="button" role="menuitem" tabIndex={-1} onClick={props.onCopyPath}>
          {uiText.diskUsage.menuCopyPath}
        </button>
      </div>
    </div>
  );
}
