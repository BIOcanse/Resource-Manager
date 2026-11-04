import { onMount, onCleanup } from "solid-js";
import { beginShellWindowResize, endShellWindowResize, subscribeShellHostMessages } from "../utils.ts";

export function useShellResizeBinding(): void {
  onMount(() => {
    const unsubscribe = subscribeShellHostMessages(message => {
      if (message === "host.window-resize:start") beginShellWindowResize();
      if (message === "host.window-resize:end") endShellWindowResize();
    });
    onCleanup(unsubscribe);
  });
}
