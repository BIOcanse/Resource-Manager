import { creditLinkKinds } from "./settingsTypes.ts";
import type { CreditCatalogItem, CreditGroup, CreditItem, SettingsCopy } from "./settingsTypes.ts";

/*
 * 致谢目录。规则见 docs/credits_sources.md：
 * - 一行（name + role）下是一个或多个子项目，每个子项目有它自己的链接；
 * - description 是该项目官方的原文（GitHub About，或官网、官方包页、官方文档页对它自己的一句话），原语言逐字，不翻译，
 *   source 是取自的页面（2026-10-25 读取）；找不到官方简介的子项目不写 description，界面就不显示说明；
 * - 按钮只有 creditLinkKinds 这几种，同一子项目里不重复、不指向同一地址。
 */

const projectGitHubUrl = "https://github.com/BIOcanse/Resource-Manager";

const creditCatalog: CreditCatalogItem[] = [
  {
    group: "project", name: "Resource Manager", role: "project",
    subjects: [{
      name: "Resource Manager",
      description: { text: "Detailed Windows resource monitoring, CPU scheduling, memory optimization, GPU placement, and application behavior reports.", source: projectGitHubUrl },
      links: [{ kind: "github", href: projectGitHubUrl }, { kind: "license", href: `${projectGitHubUrl}/blob/main/LICENSE` }]
    }]
  },
  {
    group: "runtime", name: "SolidJS", role: "frontendFramework",
    subjects: [{
      name: "SolidJS",
      description: { text: "A declarative, efficient, and flexible JavaScript library for building user interfaces.", source: "https://github.com/solidjs/solid" },
      links: [
        { kind: "official", href: "https://www.solidjs.com/" },
        { kind: "github", href: "https://github.com/solidjs/solid" },
        { kind: "license", href: "https://github.com/solidjs/solid/blob/v1.9.13/LICENSE" }
      ]
    }]
  },
  {
    group: "runtime", name: "Lucide / Feather", role: "icons",
    subjects: [{
      name: "Lucide",
      description: { text: "Beautiful & consistent icon toolkit made by the community. Open-source project and a fork of Feather Icons.", source: "https://github.com/lucide-icons/lucide" },
      links: [{ kind: "official", href: "https://lucide.dev/" }, { kind: "license", href: "https://lucide.dev/license" }]
    }]
  },
  {
    group: "runtime", name: "Seroval / seroval-plugins", role: "serialization",
    subjects: [{
      name: "Seroval",
      description: { text: "Stringify JS values", source: "https://github.com/lxsmnsyc/seroval" },
      links: [{ kind: "github", href: "https://github.com/lxsmnsyc/seroval" }]
    }]
  },
  {
    group: "runtime", name: "uPlot", role: "charts",
    subjects: [{
      name: "uPlot",
      description: { text: "A small, fast chart for time series, lines, areas, ohlc & bars", source: "https://www.npmjs.com/package/uplot" },
      links: [
        { kind: "github", href: "https://github.com/leeoniya/uPlot" },
        { kind: "license", href: "https://github.com/leeoniya/uPlot/blob/1.6.32/LICENSE" }
      ]
    }]
  },
  {
    group: "runtime", name: "Microsoft WebView2", role: "webView",
    subjects: [{
      name: "Microsoft WebView2",
      description: { text: "Host web content in your Win32, .NET, UWP apps with the Microsoft Edge WebView2 control.", source: "https://learn.microsoft.com/en-us/microsoft-edge/webview2/" },
      links: [
        { kind: "docs", href: "https://learn.microsoft.com/en-us/microsoft-edge/webview2/" },
        { kind: "nuget", href: "https://www.nuget.org/packages/Microsoft.Web.WebView2" }
      ]
    }]
  },
  {
    group: "runtime", name: "Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite", role: "database",
    subjects: [
      {
        name: "Microsoft.Data.Sqlite",
        description: { text: "Microsoft.Data.Sqlite is a lightweight ADO.NET provider for SQLite.", source: "https://www.nuget.org/packages/Microsoft.Data.Sqlite" },
        links: [{ kind: "github", href: "https://github.com/dotnet/efcore" }]
      },
      {
        name: "SQLitePCLRaw",
        description: { text: "A Portable Class Library (PCL) for low-level (raw) access to SQLite", source: "https://github.com/ericsink/SQLitePCL.raw" },
        links: [{ kind: "github", href: "https://github.com/ericsink/SQLitePCL.raw" }]
      },
      {
        name: "SQLite",
        description: { text: "SQLite is an in-process library that implements a self-contained, serverless, zero-configuration, transactional SQL database engine.", source: "https://www.sqlite.org/about.html" },
        links: [{ kind: "official", href: "https://www.sqlite.org/" }, { kind: "license", href: "https://www.sqlite.org/copyright.html" }]
      }
    ]
  },
  {
    group: "runtime", name: ".NET / ASP.NET Core / Windows Forms", role: "dotnetRuntime",
    subjects: [
      {
        name: ".NET",
        description: { text: ".NET is a developer platform with tools and libraries for building any type of app, including web, mobile, desktop, games, IoT, cloud, and microservices.", source: "https://dotnet.microsoft.com/" },
        links: [{ kind: "official", href: "https://dotnet.microsoft.com/" }, { kind: "github", href: "https://github.com/dotnet/runtime" }]
      },
      {
        name: "ASP.NET Core",
        description: { text: "ASP.NET Core is a cross-platform .NET framework for building modern cloud-based web applications on Windows, Mac, or Linux.", source: "https://github.com/dotnet/aspnetcore" },
        links: [{ kind: "github", href: "https://github.com/dotnet/aspnetcore" }]
      },
      {
        name: "Windows Forms",
        description: { text: "Windows Forms is a .NET UI framework for building Windows desktop applications.", source: "https://github.com/dotnet/winforms" },
        links: [{ kind: "github", href: "https://github.com/dotnet/winforms" }]
      }
    ]
  },
  {
    group: "runtime", name: "Free Software Foundation / libgcc / libstdc++ / Hewlett-Packard / Silicon Graphics / Lockless Inc.", role: "dotnetRuntime",
    subjects: [
      {
        name: "libstdc++",
        description: { text: "The GNU Standard C++ Library v3 is an ongoing project to implement the ISO 14882 C++ Standard Library as described in clauses 20 through 33 and annex D (prior to the 2017 standard the library clauses started with 17).", source: "https://gcc.gnu.org/onlinedocs/libstdc++/faq.html" },
        links: [{ kind: "docs", href: "https://gcc.gnu.org/onlinedocs/libstdc++/" }]
      },
      {
        name: "MinGW-w64",
        description: { text: "A complete runtime environment for GCC & LLVM for 32-bit (x86), 64-bit (x64), and ARM64 Windows", source: "https://www.mingw-w64.org/" },
        links: [{ kind: "official", href: "https://www.mingw-w64.org/" }]
      }
    ]
  },
  {
    // .NET 与 WebView2 原始第三方声明里的作者是合集，没有官方简介；ANTLR 是其中有自己仓库的一项。
    group: "runtime", name: "ANTLR / .NET & WebView2 third-party contributors", role: "dotnetRuntime",
    subjects: [{
      name: "ANTLR",
      description: { text: "ANTLR (ANother Tool for Language Recognition) is a powerful parser generator for reading, processing, executing, or translating structured text or binary files.", source: "https://github.com/antlr/antlr4" },
      links: [{ kind: "official", href: "https://www.antlr.org/" }, { kind: "github", href: "https://github.com/antlr/antlr4" }]
    }]
  },
  {
    // Windows 文档首页的说明讲的是文档本身，不是 Windows：没有官方简介。
    group: "windows", name: "Microsoft Windows / Win32 / DirectX / WMI / ETW", role: "windowsManagement",
    subjects: [{ name: "Microsoft Windows / Win32 / DirectX / WMI / ETW", links: [{ kind: "docs", href: "https://learn.microsoft.com/windows/" }] }]
  },
  {
    group: "windows", name: "Microsoft.Extensions.Hosting.WindowsServices / System.ServiceProcess.ServiceController / System.Reflection.TypeExtensions / System.IO.Ports / System.IO.FileSystem.AccessControl", role: "dotnetRuntime",
    subjects: [{
      name: "dotnet/runtime",
      description: { text: ".NET is a cross-platform runtime for cloud, mobile, desktop, and IoT apps.", source: "https://github.com/dotnet/runtime" },
      links: [{ kind: "github", href: "https://github.com/dotnet/runtime" }]
    }]
  },
  {
    group: "windows", name: "Khronos / Vulkan-Headers / Valve / LunarG", role: "hookLibrary",
    subjects: [{
      name: "Vulkan-Headers",
      description: { text: "Vulkan header files and API registry", source: "https://github.com/KhronosGroup/Vulkan-Headers" },
      links: [{ kind: "github", href: "https://github.com/KhronosGroup/Vulkan-Headers" }]
    }]
  },
  {
    group: "windows", name: "Mesa / WGL contributors", role: "hookLibrary",
    subjects: [{
      name: "Mesa",
      description: { text: "Open source implementations of OpenGL, OpenGL ES, Vulkan, OpenCL, and more!", source: "https://www.mesa3d.org/" },
      links: [{ kind: "official", href: "https://www.mesa3d.org/" }]
    }]
  },
  {
    group: "windows", name: "MinHook / Hacker Disassembler Engine", role: "hookLibrary",
    subjects: [{
      name: "MinHook",
      description: { text: "The Minimalistic x86/x64 API Hooking Library for Windows", source: "https://github.com/TsudaKageyu/minhook" },
      links: [
        { kind: "github", href: "https://github.com/TsudaKageyu/minhook" },
        { kind: "license", href: "https://github.com/TsudaKageyu/minhook/blob/v1.3.4/LICENSE.txt" }
      ]
    }]
  },
  {
    group: "windows", name: "Microsoft Detours", role: "startupInjectionLibrary",
    subjects: [{
      name: "Microsoft Detours",
      description: { text: "Detours is a software package for monitoring and instrumenting API calls on Windows.  It is distributed in source code form.", source: "https://github.com/microsoft/Detours" },
      links: [
        { kind: "github", href: "https://github.com/microsoft/Detours" },
        { kind: "license", href: "https://github.com/microsoft/Detours/blob/v4.0.1/LICENSE.md" }
      ]
    }]
  },
  {
    group: "windows", name: "Microsoft TraceEvent / Diagnostics Client", role: "traceLibrary",
    subjects: [{
      name: "TraceEvent",
      description: { text: "The TraceEvent library conains the classes needed to control ETW providers (including .NET EventSources) and parse the events they emit.", source: "https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent" },
      links: [
        { kind: "github", href: "https://github.com/microsoft/perfview" },
        { kind: "nuget", href: "https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent" }
      ]
    }]
  },
  {
    group: "windows", name: "System.Management / WMI / System.CodeDom", role: "windowsManagement",
    subjects: [
      {
        name: "System.Management",
        description: { text: "Provides access to a rich set of management information and management events about the system, devices, and applications instrumented to the Windows Management Instrumentation (WMI) infrastructure.", source: "https://www.nuget.org/packages/System.Management" },
        links: [{ kind: "nuget", href: "https://www.nuget.org/packages/System.Management" }]
      },
      {
        name: "WMI",
        description: { text: "Windows Management Instrumentation (WMI) is the infrastructure for management data and operations on Windows-based operating systems.", source: "https://learn.microsoft.com/en-us/windows/win32/wmisdk/wmi-start-page" },
        links: [{ kind: "docs", href: "https://learn.microsoft.com/en-us/windows/win32/wmisdk/wmi-start-page" }]
      }
    ]
  },
  {
    group: "data", name: "UL Solutions / 3DMark Steel Nomad", role: "externalReference",
    subjects: [{
      name: "3DMark",
      description: { text: "Benchmark your PC, tablet and smartphone with 3DMark, The Gamer's Benchmark.", source: "https://benchmarks.ul.com/3dmark" },
      links: [{ kind: "official", href: "https://benchmarks.ul.com/compare/best-gpus" }]
    }]
  },
  {
    group: "data", name: "USB ID Repository", role: "deviceIdDatabase",
    subjects: [{
      name: "USB ID Repository",
      description: { text: "This is a public repository of all known ID's used in USB devices: ID's of vendors, devices, subsystems and device classes.", source: "https://usb-ids.gowdy.us/" },
      links: [{ kind: "official", href: "https://usb-ids.gowdy.us/" }]
    }]
  },
  {
    group: "data", name: "PCI ID Repository", role: "deviceIdDatabase",
    subjects: [{
      name: "PCI ID Repository",
      description: { text: "This is a public repository of all known ID's used in PCI devices: ID's of vendors, devices, subsystems and device classes.", source: "https://pci-ids.ucw.cz/" },
      links: [{ kind: "official", href: "https://pci-ids.ucw.cz/" }, { kind: "github", href: "https://github.com/pciutils/pciids" }]
    }]
  },
  {
    group: "data", name: "Microsoft WinGet Community Repository / Wikidata", role: "softwareCatalog",
    subjects: [
      {
        name: "winget-pkgs",
        description: { text: "The Microsoft community Windows Package Manager manifest repository", source: "https://github.com/microsoft/winget-pkgs" },
        links: [
          { kind: "github", href: "https://github.com/microsoft/winget-pkgs" },
          { kind: "license", href: "https://github.com/microsoft/winget-pkgs/blob/master/LICENSE" }
        ]
      },
      {
        name: "Wikidata",
        description: { text: "Wikidata is a free, collaborative, multilingual, secondary knowledge base, collecting structured data to provide support for Wikipedia, Wikimedia Commons, the other wikis of the Wikimedia movement, and to anyone in the world.", source: "https://www.wikidata.org/wiki/Wikidata:Introduction" },
        links: [{ kind: "docs", href: "https://www.wikidata.org/wiki/Wikidata:Data_access" }]
      }
    ]
  },
  {
    group: "build", name: ".NET SDK / MSBuild / NuGet / PowerShell", role: "buildToolchain",
    subjects: [{
      name: "dotnet",
      description: { text: "Home of the open source .NET platform", source: "https://github.com/dotnet" },
      links: [{ kind: "github", href: "https://github.com/dotnet" }]
    }]
  },
  {
    group: "build", name: "GCC / MinGW-w64 / winpthreads / WinLibs", role: "nativeCompiler",
    subjects: [
      {
        name: "GCC",
        description: { text: "The GNU Compiler Collection includes front ends for C, C++, Objective-C, Objective-C++, Fortran, Ada, Go, D, Modula-2, COBOL, Rust, and Algol 68 as well as libraries for these languages (libstdc++,...).", source: "https://gcc.gnu.org/" },
        links: [{ kind: "official", href: "https://gcc.gnu.org/" }]
      },
      {
        name: "WinLibs",
        description: { text: "In short: it's a free C and C++ compiler for Microsoft Windows.", source: "https://winlibs.com/" },
        links: [{ kind: "official", href: "https://winlibs.com/" }]
      }
    ]
  },
  {
    group: "build", name: "npm / Babel / Rolldown / Oxc / Lightning CSS / PostCSS / Browserslist", role: "buildToolchain",
    subjects: [{
      name: "npm",
      description: { text: "the package manager for JavaScript", source: "https://github.com/npm/cli" },
      links: [{ kind: "official", href: "https://www.npmjs.com/" }]
    }]
  },
  {
    group: "build", name: "Playwright / xUnit.net / Coverlet / Microsoft.NET.Test.Sdk", role: "buildToolchain",
    subjects: [
      {
        name: "Playwright",
        description: { text: "Playwright is a framework for Web Testing and Automation. It allows testing Chromium, Firefox and WebKit with a single API.", source: "https://github.com/microsoft/playwright" },
        links: [{ kind: "official", href: "https://playwright.dev/" }]
      },
      {
        name: "xUnit.net",
        description: { text: "xUnit.net is a free, open source, community-focused unit testing tool for .NET.", source: "https://github.com/xunit/xunit" },
        links: [{ kind: "official", href: "https://xunit.net/" }]
      },
      {
        name: "Coverlet",
        description: { text: "Cross platform code coverage for .NET", source: "https://github.com/coverlet-coverage/coverlet" },
        links: [{ kind: "github", href: "https://github.com/coverlet-coverage/coverlet" }]
      }
    ]
  },
  {
    group: "build", name: "Vite / vite-plugin-solid", role: "buildToolchain",
    subjects: [
      {
        name: "Vite",
        description: { text: "Next generation frontend tooling. It's fast!", source: "https://github.com/vitejs/vite" },
        links: [{ kind: "official", href: "https://vite.dev/" }, { kind: "github", href: "https://github.com/vitejs/vite" }]
      },
      {
        name: "vite-plugin-solid",
        description: { text: "A simple integration to run solid-js with vite", source: "https://github.com/solidjs/solid-vite-plugin" },
        links: [{ kind: "github", href: "https://github.com/solidjs/vite-plugin-solid" }]
      }
    ]
  },
  {
    group: "build", name: "TypeScript", role: "typeSystem",
    subjects: [{
      name: "TypeScript",
      description: { text: "TypeScript is a superset of JavaScript that compiles to clean JavaScript output.", source: "https://github.com/microsoft/TypeScript" },
      links: [{ kind: "official", href: "https://www.typescriptlang.org/" }, { kind: "github", href: "https://github.com/microsoft/TypeScript" }]
    }]
  },
  {
    group: "build", name: "Node.js / DefinitelyTyped / CSSType", role: "buildRuntime",
    subjects: [
      {
        name: "Node.js",
        description: { text: "Node.js® is a free, open-source, cross-platform JavaScript runtime environment that lets developers create servers, web apps, command line tools and scripts.", source: "https://nodejs.org/" },
        links: [{ kind: "official", href: "https://nodejs.org/" }]
      },
      {
        name: "DefinitelyTyped",
        description: { text: "The repository for high quality TypeScript type definitions.", source: "https://github.com/DefinitelyTyped/DefinitelyTyped" },
        links: [{ kind: "github", href: "https://github.com/DefinitelyTyped/DefinitelyTyped" }]
      },
      {
        name: "CSSType",
        description: { text: "Strict TypeScript and Flow types for style based on MDN data", source: "https://github.com/frenic/csstype" },
        links: [{ kind: "github", href: "https://github.com/frenic/csstype" }]
      }
    ]
  },
  {
    group: "build", name: "Zig", role: "nativeCompiler",
    subjects: [{
      name: "Zig",
      description: { text: "Zig is a general-purpose programming language and toolchain for maintaining robust, optimal and reusable software.", source: "https://ziglang.org/" },
      links: [{ kind: "official", href: "https://ziglang.org/" }, { kind: "license", href: "https://github.com/ziglang/zig/blob/master/LICENSE" }]
    }]
  },
  {
    group: "hardware", name: "NVIDIA NVML", role: "nvidiaTelemetry",
    subjects: [{
      name: "NVIDIA NVML",
      description: { text: "The NVIDIA Management Library (NVML) is a C-based programmatic interface for monitoring and managing various states within Data Center GPUs.", source: "https://developer.nvidia.com/management-library-nvml" },
      links: [{ kind: "docs", href: "https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html" }]
    }]
  },
  {
    group: "hardware", name: "NVIDIA NVAPI", role: "nvidiaExtension",
    subjects: [{
      name: "NVIDIA NVAPI",
      description: { text: "NVAPI is NVIDIA's core software development kit that allows direct access to NVIDIA GPUs and drivers on supported platforms.", source: "https://github.com/NVIDIA/nvapi" },
      links: [{ kind: "docs", href: "https://docs.nvidia.com/nvapi/modules.html" }]
    }]
  },
  {
    group: "hardware", name: "AMD ADLX / ADL", role: "amdTelemetry",
    subjects: [{
      name: "AMD ADLX",
      description: { text: "AMD Device Library eXtra", source: "https://github.com/GPUOpen-LibrariesAndSDKs/ADLX" },
      links: [{ kind: "official", href: "https://gpuopen.com/" }, { kind: "github", href: "https://github.com/GPUOpen-LibrariesAndSDKs/ADLX" }]
    }]
  },
  {
    group: "hardware", name: "PawnIO / PawnIOLib", role: "signedHardwareAccess",
    subjects: [{
      name: "PawnIO",
      description: { text: "PawnIO - A scriptable universal kernel driver for hardware access using the Pawn scripting language", source: "https://pawnio.eu/" },
      links: [{ kind: "official", href: "https://pawnio.eu/" }, { kind: "github", href: "https://github.com/namazso/PawnIO.Setup" }]
    }]
  },
  {
    group: "hardware", name: "PawnIO.Modules", role: "signedHardwareAccess",
    subjects: [{
      name: "PawnIO.Modules",
      description: { text: "Modules for use with PawnIO", source: "https://github.com/namazso/PawnIO.Modules" },
      links: [
        { kind: "github", href: "https://github.com/namazso/PawnIO.Modules" },
        { kind: "license", href: "https://github.com/namazso/PawnIO.Modules/blob/main/COPYING" }
      ]
    }]
  },
  {
    group: "hardware", name: "LibreHardwareMonitorLib", role: "hardwareBridge",
    subjects: [{
      name: "LibreHardwareMonitor",
      description: { text: "Libre Hardware Monitor is free software that can monitor the temperature sensors, fan speeds, voltages, load and clock speeds of your computer.", source: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor" },
      links: [
        { kind: "github", href: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor" },
        { kind: "license", href: "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE" }
      ]
    }]
  },
  {
    group: "hardware", name: "DiskInfoToolkit / RAMSPDToolkit-NDD / BlackSharp.Core", role: "hardwareBridge",
    subjects: [
      {
        name: "DiskInfoToolkit",
        description: { text: "A toolkit for Storage Device informations. Primarily used for reading SMART data from storage devices.", source: "https://github.com/Blacktempel/DiskInfoToolkit" },
        links: [{ kind: "github", href: "https://github.com/Blacktempel/DiskInfoToolkit" }]
      },
      {
        name: "RAMSPDToolkit",
        description: { text: "A toolkit for accessing RAMs SPD. Primarily used for reading temperature and data from SPD.", source: "https://github.com/Blacktempel/RAMSPDToolkit" },
        links: [{ kind: "github", href: "https://github.com/Blacktempel/RAMSPDToolkit" }]
      },
      {
        name: "BlackSharp",
        description: { text: "A framework to eliminate duplicate code across projects.", source: "https://www.nuget.org/packages/BlackSharp.Core" },
        links: [{ kind: "github", href: "https://github.com/Blacktempel/BlackSharp" }]
      }
    ]
  },
  {
    group: "hardware", name: "HidSharp", role: "hardwareBridge",
    subjects: [{
      name: "HidSharp",
      description: { text: "HIDSharp is a multiplatform C# library for USB HID devices.", source: "https://software.seekye.com/hidsharp" },
      links: [{ kind: "official", href: "https://software.seekye.com/hidsharp" }, { kind: "license", href: "https://www.apache.org/licenses/LICENSE-2.0" }]
    }]
  },
  {
    group: "hardware", name: "Mono.Posix.NETStandard", role: "dotnetRuntime",
    subjects: [{
      name: "Mono.Posix.NETStandard",
      description: { text: "Provides functionality to access Posix/Unix features", source: "https://www.nuget.org/packages/Mono.Posix.NETStandard" },
      links: [
        { kind: "github", href: "https://github.com/mono/mono/tree/master/mcs/class/Mono.Posix" },
        { kind: "license", href: "https://github.com/mono/mono/blob/master/LICENSE" }
      ]
    }]
  }
];

/** 目录本身（语言无关），给合同测试核对原文和来源。 */
export const creditSources: readonly CreditCatalogItem[] = creditCatalog;

const linkOrder = new Map(creditLinkKinds.map((kind, index) => [kind, index]));

export function createCreditGroups(copy: SettingsCopy): CreditGroup[] {
  const grouped = new Map<keyof SettingsCopy["credits"]["groups"], CreditItem[]>();
  for (const item of creditCatalog) {
    const single = item.subjects.length === 1 && item.subjects[0].name === item.name;
    const items = grouped.get(item.group) ?? [];
    items.push({
      name: item.name,
      role: copy.credits.roles[item.role],
      subjects: item.subjects.map((subject) => ({
        name: single ? null : subject.name,
        description: subject.description ?? null,
        links: [...subject.links]
          .sort((a, b) => linkOrder.get(a.kind)! - linkOrder.get(b.kind)!)
          .map((link) => ({
            kind: link.kind,
            label: copy.credits.linkLabels[link.kind],
            accessibleName: `${copy.credits.linkLabels[link.kind]} · ${subject.name}`,
            href: link.href
          }))
      }))
    });
    grouped.set(item.group, items);
  }

  return (Object.keys(copy.credits.groups) as Array<keyof SettingsCopy["credits"]["groups"]>)
    .map((group) => ({
      title: copy.credits.groups[group],
      items: grouped.get(group) ?? []
    }));
}
