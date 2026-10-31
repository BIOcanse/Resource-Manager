import type { Page } from "playwright-core";
import { waitForCondition } from "./journeyWaits.ts";

export async function publishMetricPhase(page: Page, baseUrl: string, kind: "catalog" | "ids" | "fields") {
  // The channel is registered asynchronously after the dashboard mounts. Retry
  // only zero-recipient attempts; stop after the first real publication.
  return waitForCondition(page, async () => {
    const response = await fetch(`${baseUrl}/__test/metrics/publish`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind })
    });
    if (!response.ok) throw new Error(`metric phase ${kind}: HTTP ${response.status}`);
    const publication: { publications: number; queries: string[] } = await response.json();
    return publication.publications > 0 ? { kind, ...publication } : false;
  }, { timeoutMs: 5000, description: `${kind} metric subscription is registered and publishes` });
}

export async function readMetricSlotStates(page: Page) {
  return page.locator(".dashboard .metric-card").evaluateAll(cards => cards.flatMap(card =>
    [...card.querySelectorAll<HTMLElement>(".metric-value")].map((value, index) => ({
      slot: `${(card as HTMLElement).dataset.cardId}:${index}`,
      value: value.classList.contains("pending") ? "<pending>" : value.textContent!.replace(/\s+/g, " ").trim()
    }))));
}

// Observe the actual hover target and its CSS transitions. Border expectations
// remain in the journey, so a wrong final colour still fails its original check.
export async function waitForMetricHover(page: Page, target: "slot" | "card") {
  await waitForCondition(page, () => page.evaluate((target) => {
    const card = document.querySelector<HTMLElement>('.metric-card[data-card-id="cpu"]')!;
    const slot = card?.querySelectorAll(".edit-slot.has-metric")[1];
    const actions = slot?.querySelector(".slot-actions");
    if (!card || !slot || !actions) return false;
    const onSlot = slot.matches(":hover");
    if (target === "slot" ? !onSlot : !card.matches(":hover") || card.querySelector<HTMLElement>(".edit-slot.has-metric:hover, .slot-add:hover")!) return false;
    if (getComputedStyle(actions).opacity !== (target === "slot" ? "1" : "0")) return false;
    return [card, slot, actions].every(element => !element.getAnimations().some(animation =>
      animation instanceof CSSTransition && (animation.pending || animation.playState === "running")));
  }, target), { timeoutMs: 5000, description: `${target} hover feedback transitions finish` });
}
