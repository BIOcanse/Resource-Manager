import { createSignal, onCleanup, onMount } from "solid-js";
import { DiskUsagePage } from "../diskUsage/DiskUsagePage";
import { getDiskUsageVolumes } from "../diskUsage/diskUsageApi.ts";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import type {
  DiskUsageScanMode,
  DiskUsageScanScope,
  DiskUsageVolume
} from "../diskUsage/diskUsageTypes.ts";

/**
 * 磁盘占用页的外壳：只负责取卷清单并把扫描请求交出去。
 * 卷会插拔，所以每次进页面都重新读一次，不留缓存。
 */
export function DiskUsageWorkspace() {
  const runtime = useFrontendRuntime();
  const [volumes, setVolumes] = createSignal<DiskUsageVolume[]>([]);
  const [scanning] = createSignal(false);

  onMount(() => {
    const controller = new AbortController();
    void getDiskUsageVolumes(runtime.requestClient, controller.signal)
      .then(setVolumes)
      .catch(() => setVolumes([]));
    onCleanup(() => controller.abort());
  });

  function startScan(
    scope: DiskUsageScanScope,
    mode: DiskUsageScanMode,
    target: string
  ) {
    // 扫描本身在下一个切片接上操作协调器；这里先把请求形状定下来。
    void scope;
    void mode;
    void target;
  }

  return (
    <DiskUsagePage
      volumes={volumes()}
      scanning={scanning()}
      onScan={startScan}
    />
  );
}
