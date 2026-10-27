import { createDiskUsageOwner, type DiskUsageOwner } from "../features/diskUsage/diskUsageOwner.ts";
import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import { RequestClient } from "./request/RequestClient.ts";
import { BackendSessionOwner } from "./session/BackendSessionOwner.ts";
import { SourceRegistry } from "./source/SourceRegistry.ts";
import { TaskRegistry } from "./task/TaskRegistry.ts";
import { OperationRegistry } from "./operations/OperationRegistry.ts";
import { BackendSubscriptionChannel } from
  "./push/BackendSubscriptionChannel.ts";
import { TaskCenterProjection } from "./taskCenter/TaskCenterProjection.ts";
import type {
  FrontendPerformanceMonitor
} from "./performance/FrontendPerformanceMonitor.ts";
import {
  createFrontendSources,
  type FrontendSources
} from "./source/FrontendSources.ts";

export interface FrontendRuntime {
  readonly diskUsage: DiskUsageOwner;
  readonly backendSession: BackendSessionOwner;
  readonly requestClient: RequestClient;
  readonly sourceRegistry: SourceRegistry;
  readonly sources: FrontendSources;
  readonly taskRegistry: TaskRegistry;
  readonly operationRegistry: OperationRegistry;
  readonly taskCenter: TaskCenterProjection;
  readonly performance: FrontendPerformanceMonitor;
  /** 授权变化后按当前订阅重新连接推送流（见 BackendSubscriptionChannel.reconnect）。 */
  reconnectSubscriptions(): void;
  dispose(): void;
}

export function createFrontendRuntime(
  performance: FrontendPerformanceMonitor
): FrontendRuntime {
  const transport = getHostMessageTransport();
  const backendSession = new BackendSessionOwner({
    transport,
    allowStandalone: transport === null && isStandaloneDevelopmentSurface()
  });
  const requestClient = new RequestClient(backendSession, performance.enabled
    ? { onAttemptSettled: performance.requestObserver }
    : undefined);
  const sourceRegistry = new SourceRegistry(backendSession, performance.enabled
    ? { onActivity: performance.sourceObserver }
    : undefined);
  const subscriptionChannel = new BackendSubscriptionChannel({
    backendSession
  });
  const sources = createFrontendSources(
    requestClient,
    sourceRegistry,
    subscriptionChannel);
  const taskRegistry = new TaskRegistry();
  const unsubscribePerformanceTasks = performance.enabled
    ? taskRegistry.subscribe((change) => performance.recordTaskChange(change))
    : () => undefined;
  const operationRegistry = new OperationRegistry(
    requestClient,
    sources.operations,
    { active: false });
  const diskUsage = createDiskUsageOwner({ requestClient, sourceRegistry, operationRegistry, backendSession });
  const taskCenter = new TaskCenterProjection(taskRegistry, operationRegistry);
  let disposed = false;

  return {
    diskUsage,
    backendSession,
    requestClient,
    sourceRegistry,
    sources,
    taskRegistry,
    operationRegistry,
    taskCenter,
    performance,
    reconnectSubscriptions: () => subscriptionChannel.reconnect(),
    dispose: () => {
      if (disposed) {
        return;
      }
      disposed = true;
      diskUsage.dispose();
      taskCenter.dispose();
      taskRegistry.dispose();
      unsubscribePerformanceTasks();
      operationRegistry.dispose();
      subscriptionChannel.dispose();
      sourceRegistry.dispose();
      performance.dispose();
      backendSession.dispose();
    }
  };
}

function isStandaloneDevelopmentSurface(): boolean {
  if (typeof window === "undefined") {
    return false;
  }
  return import.meta.env.DEV
    || (window.location.hostname === "127.0.0.1"
      && window.location.port !== "9321");
}
