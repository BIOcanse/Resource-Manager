import { render } from "solid-js/web";
import App from "./App";
import { createFrontendRuntime } from "./frontendRuntime/FrontendRuntime";
import { FrontendRuntimeProvider } from "./frontendRuntime/FrontendRuntimeContext";
import {
  disabledFrontendPerformanceMonitor,
  frontendPerformanceBootstrapRequested
} from "./frontendRuntime/performance/frontendPerformanceBootstrap";
import type {
  FrontendPerformanceMonitor
} from "./frontendRuntime/performance/FrontendPerformanceMonitor";
import "./styles.css";

const root = document.getElementById("root");

if (!root) {
  throw new Error("Missing root element.");
}

const bootstrapTarget = window as unknown as Record<string, unknown>;
if (frontendPerformanceBootstrapRequested(bootstrapTarget)) {
  void import("./frontendRuntime/performance/FrontendPerformanceMonitor.ts")
    .then(({ createFrontendPerformanceMonitor }) =>
      startApplication(createFrontendPerformanceMonitor()))
    .catch(reportBootstrapFailure);
} else {
  startApplication(disabledFrontendPerformanceMonitor);
}

function startApplication(performance: FrontendPerformanceMonitor) {
  const runtime = createFrontendRuntime(performance);
  const disposeApp = render(
    () => (
      <FrontendRuntimeProvider runtime={runtime}>
        <App />
      </FrontendRuntimeProvider>
    ),
    root!);
  runtime.performance.markLifecycle("app-render-returned");

  window.addEventListener("pagehide", () => {
    disposeApp();
    runtime.dispose();
  }, { once: true });
}

function reportBootstrapFailure(error: unknown) {
  queueMicrotask(() => {
    throw error;
  });
}
