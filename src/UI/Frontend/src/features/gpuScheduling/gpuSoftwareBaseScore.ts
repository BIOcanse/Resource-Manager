export function defaultGpuSoftwareBaseScore(kind?: string | null) {
  switch (kind) {
    case "game":
      return 95;
    case "high_performance":
      return 85;
    case "windows_system":
    case "windows_component":
    case "windows_service":
      return 90;
    case "adapted":
      return 80;
    case "runtime_product":
    case "runtime_package":
    case "runtime_root":
      return 55;
    case "controlled":
      return 45;
    case "managed":
      return 42;
    case "unattributed":
      return 30;
    default:
      return 35;
  }
}
