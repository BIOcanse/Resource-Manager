/** Restore the opener after closing, using its focus key after rerendering. Ignore missing or inert targets. */
export function restoreFocusTarget(
  opener: HTMLElement | null,
  focusKey: string | undefined
) {
  const target = opener?.isConnected && (!focusKey || opener.dataset.focusKey === focusKey)
    ? opener
    : focusKey
      ? document.querySelector<HTMLElement>(`[data-focus-key="${CSS.escape(focusKey)}"]`)
      : null;
  if (target && !target.inert && !target.closest("[inert]")) {
    target.focus({ preventScroll: true });
  }
}

/** After unmount, restore only if focus fell to body or a detached element; preserve focus moved by the user. */
export function scheduleFocusRestore(
  opener: HTMLElement | null,
  focusKey: string | undefined
) {
  queueMicrotask(() => {
    requestAnimationFrame(() => {
      const activeElement = document.activeElement;
      if (activeElement === document.body
        || !(activeElement instanceof HTMLElement)
        || !activeElement.isConnected) {
        restoreFocusTarget(opener, focusKey);
      }
    });
  });
}
