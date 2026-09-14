export interface RovingFocusOptions {
  readonly selector: string;
  readonly previousKeys: readonly string[];
  readonly nextKeys: readonly string[];
  readonly activate?: (element: HTMLElement) => void;
  readonly ensureVisible?: (element: HTMLElement, container: HTMLElement) => void;
}

export function moveRovingFocus(
  event: KeyboardEvent & { currentTarget: HTMLElement },
  options: RovingFocusOptions
) {
  const move = options.previousKeys.includes(event.key)
    ? -1
    : options.nextKeys.includes(event.key)
      ? 1
      : event.key === "Home"
        ? "first"
        : event.key === "End"
          ? "last"
          : null;
  if (move === null) {
    return false;
  }

  const items = Array.from(
    event.currentTarget.querySelectorAll<HTMLElement>(options.selector));
  if (items.length === 0) {
    return false;
  }

  const currentIndex = items.indexOf(document.activeElement as HTMLElement);
  const nextIndex = move === "first"
    ? 0
    : move === "last"
      ? items.length - 1
      : currentIndex < 0
        ? (move > 0 ? 0 : items.length - 1)
        : (currentIndex + move + items.length) % items.length;
  const next = items[nextIndex];
  event.preventDefault();
  next.focus({ preventScroll: true });
  options.ensureVisible?.(next, event.currentTarget);
  options.activate?.(next);
  return true;
}
