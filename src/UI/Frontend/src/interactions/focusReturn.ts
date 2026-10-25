/**
 * 弹层（对话框、右键菜单）关闭后把焦点还给打开它的元素。
 *
 * 打开时记下当时的焦点元素和它的 data-focus-key。关闭时：那个元素还在页面上（且 focus key 没变）就还给它；
 * 它已经被重新渲染掉时，按 focus key 找到新的同一个元素；都找不到，或者目标处于 inert 之下，就不动焦点。
 */
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

/**
 * 弹层卸载之后再还焦点：只在焦点因为弹层消失而掉到 body（或已不在页面上的元素）时才还，
 * 用户这期间自己把焦点放到别处时不抢。
 */
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
