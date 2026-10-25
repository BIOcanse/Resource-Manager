import { createContext, onCleanup, useContext, createSignal, type JSX } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import type { AccessRequestPendingSnapshot } from "./trustedAccessTypes.ts";

type AccessRequestSnapshot = SourceSnapshot<AccessRequestPendingSnapshot> | null;

const AccessRequestSnapshotContext = createContext<() => AccessRequestSnapshot>();

export function AccessRequestSnapshotProvider(props: { children: JSX.Element }) {
  const runtime = useFrontendRuntime();
  const [snapshot, setSnapshot] = createSignal<AccessRequestSnapshot>(null);
  const unsubscribe = runtime.sources.accessRequests.subscribeSnapshot(setSnapshot);
  onCleanup(unsubscribe);

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
