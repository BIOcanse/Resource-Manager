export type { Page, Locator, BrowserContext, Browser, Request, Response, ConsoleMessage, CDPSession, ElementHandle, JSHandle } from "playwright-core";
import type { Page } from "playwright-core";
import type { PerformanceOverlayEntry, PerformanceOverlayEditingConstraints } from "../../src/types.ts";

/** Wire response shared by the overlay journey fixtures and the mock backend. */
export interface OverlaySoftwareRead {
  softwareId: string;
  stored: boolean;
  revision: string;
  settings: PerformanceOverlayEntry;
  editingConstraints: PerformanceOverlayEditingConstraints;
}

export interface JourneyOptions { languages?: string[] }
export type Journey = (page: Page, baseUrl: string, options?: JourneyOptions) => Promise<unknown>;
export type JourneyEntry = [name: string, journey: Journey];
