import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { createContext, useContext, type JSX } from "solid-js";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import type { AccessRequestPendingSnapshot } from "./trustedAccessTypes.ts";

type AccessRequestSnapshot = SourceSnapshot<AccessRequestPendingSnapshot> | null;

const AccessRequestSnapshotContext = createContext<() => AccessRequestSnapshot>();

export function AccessRequestSnapshotProvider(props: { children: JSX.Element }) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const { snapshot } = usePushSource(runtime.sources.accessRequests,
    () => ({ active: frontendWork.frontendVisible() }));

  return (
    <AccessRequestSnapshotContext.Provider value={snapshot}>
      {props.children}
    </AccessRequestSnapshotContext.Provider>
  );
}

export function useAccessRequestSnapshot() {
  const snapshot = useContext(AccessRequestSnapshotContext);
  if (!snapshot) {
    throw new Error("AccessRequestSnapshotProvider is required.");
  }
  return snapshot;
}
