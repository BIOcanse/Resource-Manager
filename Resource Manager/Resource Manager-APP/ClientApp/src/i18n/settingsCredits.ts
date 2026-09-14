import type { CreditCatalogItem, CreditGroup, CreditItem, SettingsCopy } from "./settingsTypes";

const projectGitHubUrl = "https://github.com/BIOcanse/Resource-Manager";

const creditCatalog: CreditCatalogItem[] = [
  { group: "windows", name: "Khronos / Vulkan-Headers / Valve / LunarG", role: "hookLibrary", note: "upstreamContributors",
    links: [{ label: "github", href: "https://github.com/KhronosGroup/Vulkan-Headers" }] },
  { group: "windows", name: "Mesa / WGL contributors", role: "hookLibrary", note: "upstreamContributors",
    links: [{ label: "official", href: "https://www.mesa3d.org/" }] },
  { group: "data", name: "UL Solutions / 3DMark Steel Nomad", role: "externalReference", note: "upstreamContributors",
    links: [{ label: "official", href: "https://benchmarks.ul.com/compare/best-gpus" }] },
  { group: "runtime", name: "Free Software Foundation / libgcc / libstdc++ / Hewlett-Packard / Silicon Graphics / Lockless Inc.", role: "dotnetRuntime", note: "upstreamContributors",
    links: [{ label: "official", href: "https://gcc.gnu.org/" }, { label: "official", href: "https://www.mingw-w64.org/" }] },
  { group: "windows", name: "Microsoft Windows / Win32 / DirectX / WMI / ETW", role: "windowsManagement", note: "windowsFoundation",
    links: [{ label: "docs", href: "https://learn.microsoft.com/windows/" }] },
  { group: "build", name: ".NET SDK / MSBuild / NuGet / PowerShell", role: "buildToolchain", note: "microsoftTools",
    links: [{ label: "github", href: "https://github.com/dotnet" }] },
  { group: "windows", name: "Microsoft.Extensions.Hosting.WindowsServices / System.ServiceProcess.ServiceController / System.Reflection.TypeExtensions", role: "dotnetRuntime", note: "managedServices",
    links: [{ label: "github", href: "https://github.com/dotnet/runtime" }] },
  { group: "build", name: "GCC / MinGW-w64 / winpthreads / WinLibs", role: "nativeCompiler", note: "nativeToolchain",
    links: [{ label: "official", href: "https://gcc.gnu.org/" }, { label: "official", href: "https://www.mingw-w64.org/" }] },
  { group: "build", name: "npm / Babel / Rolldown / Oxc / Lightning CSS / PostCSS / Browserslist", role: "buildToolchain", note: "buildContributors",
    links: [{ label: "official", href: "https://www.npmjs.com/" }] },
  { group: "build", name: "Playwright / xUnit.net / Coverlet / Microsoft.NET.Test.Sdk", role: "buildToolchain", note: "testContributors",
    links: [{ label: "official", href: "https://playwright.dev/" }, { label: "official", href: "https://xunit.net/" }, { label: "github", href: "https://github.com/coverlet-coverage/coverlet" }] },
  { group: "hardware", name: "OpenHardwareMonitor", role: "hardwareBridge", note: "openHardwareMonitor",
    links: [{ label: "github", href: "https://github.com/openhardwaremonitor/openhardwaremonitor" }] },
  { group: "runtime", name: "ANTLR / .NET & WebView2 third-party contributors", role: "dotnetRuntime", note: "upstreamContributors",
    links: [{ label: "official", href: "https://www.antlr.org/" }, { label: "github", href: "https://github.com/antlr/antlr4" }] },
  {
    group: "project",
    name: "Resource Manager",
    role: "project",
    note: "resourceManager",
    links: [
      { label: "github", href: projectGitHubUrl },
      { label: "license", href: `${projectGitHubUrl}/blob/main/LICENSE` }
    ]
  },
  {
    group: "runtime",
    name: "SolidJS",
    role: "frontendFramework",
    note: "solid",
    links: [
      { label: "official", href: "https://www.solidjs.com/" },
      { label: "github", href: "https://github.com/solidjs/solid" },
      { label: "license", href: "https://github.com/solidjs/solid/blob/v1.9.13/LICENSE" }
    ]
  },
  {
    group: "runtime",
    name: "Lucide / Feather",
    role: "icons",
    note: "lucide",
    links: [
      { label: "official", href: "https://lucide.dev/" },
      { label: "license", href: "https://lucide.dev/license" }
    ]
  },
  {
    group: "runtime",
    name: "Seroval / seroval-plugins",
    role: "serialization",
    note: "seroval",
    links: [{ label: "github", href: "https://github.com/lxsmnsyc/seroval" }]
  },
  {
    group: "build",
    name: "Vite / vite-plugin-solid",
    role: "buildToolchain",
    note: "vite",
    links: [
      { label: "vite", href: "https://vite.dev/" },
      { label: "github", href: "https://github.com/vitejs/vite" },
      { label: "solidPlugin", href: "https://github.com/solidjs/vite-plugin-solid" }
    ]
  },
  {
    group: "build",
    name: "TypeScript",
    role: "typeSystem",
    note: "typescript",
    links: [
      { label: "official", href: "https://www.typescriptlang.org/" },
      { label: "github", href: "https://github.com/microsoft/TypeScript" }
    ]
  },
  {
    group: "build",
    name: "Node.js / DefinitelyTyped / CSSType",
    role: "buildRuntime",
    note: "node",
    links: [
      { label: "official", href: "https://nodejs.org/" },
      { label: "github", href: "https://github.com/DefinitelyTyped/DefinitelyTyped" },
      { label: "github", href: "https://github.com/frenic/csstype" }
    ]
  },
  {
    group: "runtime",
    name: "Microsoft WebView2",
    role: "webView",
    note: "webView2",
    links: [
      { label: "docs", href: "https://learn.microsoft.com/en-us/microsoft-edge/webview2/" },
      { label: "nuget", href: "https://www.nuget.org/packages/Microsoft.Web.WebView2" }
    ]
  },
  {
    group: "runtime",
    name: "Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite",
    role: "database",
    note: "sqlite",
    links: [
      { label: "github", href: "https://github.com/dotnet/efcore" },
      { label: "github", href: "https://github.com/ericsink/SQLitePCL.raw" },
      { label: "license", href: "https://www.sqlite.org/copyright.html" }
    ]
  },
  {
    group: "build",
    name: "Zig",
    role: "nativeCompiler",
    note: "zig",
    links: [
      { label: "official", href: "https://ziglang.org/" },
      { label: "license", href: "https://github.com/ziglang/zig/blob/master/LICENSE" }
    ]
  },
  {
    group: "runtime",
    name: ".NET / ASP.NET Core / Windows Forms",
    role: "dotnetRuntime",
    note: "dotnet",
    links: [
      { label: "official", href: "https://dotnet.microsoft.com/" },
      { label: "runtime", href: "https://github.com/dotnet/runtime" },
      { label: "aspnet", href: "https://github.com/dotnet/aspnetcore" }
    ]
  },
  {
    group: "windows",
    name: "MinHook / Hacker Disassembler Engine",
    role: "hookLibrary",
    note: "minHook",
    links: [
      { label: "github", href: "https://github.com/TsudaKageyu/minhook" },
      { label: "license", href: "https://github.com/TsudaKageyu/minhook/blob/v1.3.4/LICENSE.txt" }
    ]
  },
  {
    group: "windows",
    name: "Microsoft Detours",
    role: "startupInjectionLibrary",
    note: "detours",
    links: [
      { label: "github", href: "https://github.com/microsoft/Detours" },
      { label: "license", href: "https://github.com/microsoft/Detours/blob/v4.0.1/LICENSE.md" }
    ]
  },
  {
    group: "windows",
    name: "Microsoft TraceEvent / Diagnostics Client",
    role: "traceLibrary",
    note: "traceEvent",
    links: [
      { label: "github", href: "https://github.com/microsoft/perfview" },
      { label: "nuget", href: "https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent" }
    ]
  },
  {
    group: "windows",
    name: "System.Management / WMI / System.CodeDom",
    role: "windowsManagement",
    note: "systemManagement",
    links: [
      { label: "docs", href: "https://learn.microsoft.com/en-us/windows/win32/wmisdk/wmi-start-page" },
      { label: "nuget", href: "https://www.nuget.org/packages/System.Management" }
    ]
  },
  {
    group: "data",
    name: "USB ID Repository",
    role: "deviceIdDatabase",
    note: "usbIds",
    links: [
      { label: "official", href: "https://usb-ids.gowdy.us/" },
      { label: "license", href: "https://usb-ids.gowdy.us/" }
    ]
  },
  {
    group: "data",
    name: "PCI ID Repository",
    role: "deviceIdDatabase",
    note: "pciIds",
    links: [
      { label: "official", href: "https://pci-ids.ucw.cz/" },
      { label: "github", href: "https://github.com/pciutils/pciids" },
      { label: "license", href: "https://pci-ids.ucw.cz/" }
    ]
  },
  {
    group: "data",
    name: "Microsoft WinGet Community Repository / Wikidata",
    role: "softwareCatalog",
    note: "softwareCatalog",
    links: [
      { label: "github", href: "https://github.com/microsoft/winget-pkgs" },
      { label: "license", href: "https://github.com/microsoft/winget-pkgs/blob/master/LICENSE" },
      { label: "docs", href: "https://www.wikidata.org/wiki/Wikidata:Data_access" }
    ]
  },
  {
    group: "hardware",
    name: "Windows Performance Toolkit",
    role: "etwToolkit",
    note: "wpt",
    links: [{ label: "docs", href: "https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install" }]
  },
  {
    group: "hardware",
    name: "NVIDIA NVML",
    role: "nvidiaTelemetry",
    note: "nvml",
    links: [{ label: "docs", href: "https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html" }]
  },
  {
    group: "hardware",
    name: "NVIDIA NVAPI",
    role: "nvidiaExtension",
    note: "nvapi",
    links: [{ label: "docs", href: "https://docs.nvidia.com/nvapi/modules.html" }]
  },
  {
    group: "hardware",
    name: "AMD ADLX / ADL",
    role: "amdTelemetry",
    note: "adlx",
    links: [
      { label: "github", href: "https://github.com/GPUOpen-LibrariesAndSDKs/ADLX" },
      { label: "gpuOpen", href: "https://gpuopen.com/" }
    ]
  },
  {
    group: "hardware",
    name: "AMD Ryzen Master Monitoring SDK",
    role: "amdCpuSdk",
    note: "ryzenMaster",
    links: [
      { label: "official", href: "https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html" },
      { label: "eula", href: "https://www.amd.com/en/developer/ryzen-master-monitoring-sdk/ryzen-master-monitoring-sdk-eula.html" }
    ]
  },
  {
    group: "hardware",
    name: "Intel PCM",
    role: "intelTelemetry",
    note: "intelPcm",
    links: [{ label: "github", href: "https://github.com/intel/pcm" }]
  },
  {
    group: "hardware",
    name: "PawnIO / PawnIOLib / RyzenSMU module",
    role: "amdSmuBoundary",
    note: "pawnIo",
    links: [
      { label: "official", href: "https://pawnio.eu/" },
      { label: "github", href: "https://github.com/namazso/PawnIO.Setup" }
    ]
  },
  {
    group: "hardware",
    name: "LibreHardwareMonitor",
    role: "hardwareBridge",
    note: "libreHardwareMonitor",
    links: [
      { label: "github", href: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor" },
      { label: "license", href: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE" }
    ]
  },
  {
    group: "hardware",
    name: "Notebook FanControl",
    role: "notebookEc",
    note: "notebookFanControl",
    links: [
      { label: "github", href: "https://github.com/hirschmann/nbfc" },
      { label: "license", href: "https://github.com/hirschmann/nbfc/blob/master/LICENSE.md" }
    ]
  },
  {
    group: "hardware",
    name: "MSI Afterburner",
    role: "externalReference",
    note: "afterburner",
    links: [{ label: "official", href: "https://www.msi.com/Landing/afterburner/graphics-cards" }]
  },
  {
    group: "hardware",
    name: "LatencyMon",
    role: "latencyDiagnostics",
    note: "latencyMon",
    links: [{ label: "official", href: "https://www.resplendence.com/latencymon" }]
  }
];

export function createCreditGroups(copy: SettingsCopy): CreditGroup[] {
  const grouped = new Map<keyof SettingsCopy["credits"]["groups"], CreditItem[]>();
  for (const item of creditCatalog) {
    const links = item.links.map((link) => ({
      label: copy.credits.linkLabels[link.label],
      href: link.href
    }));
    const items = grouped.get(item.group) ?? [];
    items.push({
      name: item.name,
      role: copy.credits.roles[item.role],
      note: copy.credits.notes[item.note],
      links
    });
    grouped.set(item.group, items);
  }

  return (Object.keys(copy.credits.groups) as Array<keyof SettingsCopy["credits"]["groups"]>)
    .map((group) => ({
      title: copy.credits.groups[group],
      items: grouped.get(group) ?? []
    }));
}
