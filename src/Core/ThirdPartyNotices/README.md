# Third-party notices

Resource Manager's own code is licensed under Apache-2.0.
See the accompanying `LICENSE` and `NOTICE` files. Copyright (c) 2026 BIOcanse.
That license does not replace or restrict the separate licenses of the components
listed here. Project and component names do not imply endorsement.

This inventory follows the current source pins and resolved dependency graphs.
An entry for a dependency or SDK does not imply that every upstream tool, platform
asset or optional runtime is included in a particular package.

## Our thanks

Our deepest thanks go to Microsoft, the .NET Foundation, and every author and
maintainer whose work supports Resource Manager. Windows, .NET, WebView2,
PowerShell, MSBuild, NuGet and the Windows SDK give an individual developer a
foundation that could not realistically be built alone. We are genuinely
grateful for that work. Using an upstream signed component does not mean that
Microsoft has signed, certified, or endorsed this independently developed app.

We acknowledge dependencies even when their terms do not require attribution:
public-domain SQLite, CC0 data and 0BSD utilities are not exceptions. The complete
resolved lists, including build, optional and test dependencies, are retained in
`FrontendDependencyAcknowledgements.md` and `ManagedDependencyAcknowledgements.md`.
They complement, rather than replace, the original license and notice files.

Thanks also to the GCC and MinGW-w64 communities, the Zig contributors, Tsuda
Kageyu and Vyacheslav Patkov (MinHook/Hacker Disassembler Engine), the
OpenHardwareMonitor and LibreHardwareMonitor contributors, and the authors of
PawnIO, PawnIOLib and the RyzenSMU module used by the optional provider. Exact
redistribution provenance for selected native/runtime payloads remains a release
check; gratitude is not a substitute for that check. Windows PDH, PSAPI, ETW,
BCrypt, Direct3D/DXGI, WinHTTP, Shell, COM and other platform APIs, along with
Node.js/npm, Playwright, the Vite/Solid toolchain, and the authors credited in the
unmodified .NET/WebView2 notices, are part of this acknowledgement too.

## Frontend components

| Component | Version | License and accompanying text |
| --- | --- | --- |
| solid-js | 1.9.13 | MIT; `SolidJS.LICENSE.txt`; Copyright (c) 2016-2025 Ryan Carniato |
| lucide-solid | 1.24.0 | ISC plus Feather-derived MIT notices; retain all of `Lucide.LICENSE.txt`; Lucide Icons and Contributors, Cole Bemis |
| seroval | 1.5.4 | MIT; `Seroval.LICENSE.txt`; Copyright (c) 2025 Alexis Munsayac |
| seroval-plugins | 1.5.4 | MIT; `SerovalPlugins.LICENSE.txt`; Copyright (c) 2025 Alexis Munsayac |
| csstype | 3.2.3 | MIT; `CSSType.LICENSE.txt`; Copyright (c) 2017-2018 Fredrik Nicol; type-only dependency |

Seroval and seroval-plugins are transitive Solid dependencies. Bundlers may omit
unused code; their notices are retained without claiming all their modules ship.
Upstream: [Solid](https://github.com/solidjs/solid),
[Lucide](https://lucide.dev/license), [Seroval](https://github.com/lxsmnsyc/seroval),
[CSSType](https://github.com/frenic/csstype).

## Backend and desktop SDK

The Microsoft MIT packages below carry the package attribution
"Copyright (c) Microsoft Corporation. All rights reserved."
The accompanying `Microsoft.MIT.txt` contains that attribution and the MIT
permission and disclaimer. `.NET Foundation and Contributors` attribution and
the runtime MIT text are additionally retained in `DotNet.LICENSE.txt`.

| Package | Version | License / material |
| --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.9 | MIT; dependency metapackage |
| Microsoft.Data.Sqlite.Core | 10.0.9 | MIT; Microsoft.Data.Sqlite.dll |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | MIT; diagnostics client |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.4 | MIT package; managed ETW library; see native-helper review below |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.11 | MIT; Windows service hosting |
| System.ServiceProcess.ServiceController | 10.0.11 | MIT; service controller |
| System.Management | 10.0.2 | MIT; Windows management |
| System.CodeDom | 10.0.2 | MIT; CodeDom runtime dependency |
| System.Reflection.TypeExtensions | 4.7.0 | MIT; resolved placeholder-only package for the current net10 target, not a separate shipped DLL |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.3 | Apache-2.0; dependency metapackage |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.3 | Apache-2.0; native-provider configuration |
| SQLitePCLRaw.core | 3.0.3 | Apache-2.0; low-level SQLite bindings |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.3 | Apache-2.0; native SQLite provider |
| SourceGear.sqlite3 | 3.50.4.5 | Native SQLite; public-domain declaration in `SQLite.LICENSE.txt` |
| Microsoft.Web.WebView2 | 1.0.2792.45 | SDK: BSD-3-Clause; `WebView2.LICENSE.txt` and unmodified `WebView2.NOTICE.txt` |

SQLitePCLRaw core/provider: Copyright 2014-2025 SourceGear, LLC.
SQLitePCLRaw bundle/config: Copyright 2014-2026 SourceGear, LLC.
The full license is in `Apache-2.0.txt`. These are unmodified NuGet dependencies;
no separate NOTICE file was present in the inspected SQLitePCLRaw packages.
SourceGear.sqlite3 package attribution: Copyright 2014-2025 SourceGear, LLC;
the packaged license separately states that SQLite itself is public domain.

`DotNet.THIRD-PARTY-NOTICES.txt` is the unmodified, byte-identical notice supplied
by the four current WindowsServices, ServiceController, System.Management and
System.CodeDom packages. It is retained in full, not localized or reinterpreted
as a list of all binaries in Resource Manager.

The WebView2 SDK and its loader are distinct from the system-installed WebView2
Evergreen browser runtime. No Chromium/CEF browser distribution is authorized or
implied by this SDK notice. The SDK NOTICE's listed tools are preserved as SDK
notices, not asserted to be separate application binaries.

Upstream: [dotnet/runtime](https://github.com/dotnet/runtime),
[dotnet/efcore](https://github.com/dotnet/efcore),
[dotnet/diagnostics](https://github.com/dotnet/diagnostics),
[PerfView/TraceEvent](https://github.com/microsoft/perfview),
[SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw),
[SQLite](https://sqlite.org/copyright.html),
[WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

## Native components and offline data

- GCC/MinGW-w64/winpthreads/WinLibs and Zig runtime code:
  `NativeRuntime.NOTICE.md` and the complete accompanying license/exception texts.
- UL Solutions / 3DMark Steel Nomad numeric GPU reference data:
  `UL-SteelNomad.NOTICE.md`. Thank you to UL and the benchmark participants;
  this is an external data-source acknowledgement, not an open-source license.
- Self-contained ASP.NET Core runtime contributors:
  `AspNetCore.THIRD-PARTY-NOTICES.txt`, retained unmodified from the selected pack.
- Khronos Vulkan-Headers `v1.4.341`: `Vulkan-Headers.NOTICE.md`, the original
  `Vulkan-Headers.LICENSE.md`, `Vulkan-Headers.Apache-2.0.txt` and
  `Vulkan-Headers.MIT.txt`. Source: https://github.com/KhronosGroup/Vulkan-Headers.
  We gratefully acknowledge Khronos, Valve, LunarG and all contributors. The
  native layer uses their C/loader interfaces; no Vulkan driver/runtime is bundled.
- MinHook, including HDE32/HDE64 attribution: `MinHook.LICENSE.txt` (complete
  vendored BSD notices). Source: https://github.com/TsudaKageyu/minhook.
- Microsoft Detours 4.0.1 and the documented adapted startup-restore subset:
  `MicrosoftDetours.LICENSE.md` (MIT). Source:
  https://github.com/microsoft/Detours. Local source changes remain identified in
  the vendored README and adapted source header.
- USB and PCI ID data: `DeviceIds/THIRD_PARTY_NOTICES.md` in the application
  payload retains the selected BSD-3-Clause text and snapshot provenance.
- Software metadata: `Infrastructure/Resources/SoftwareMetadata/THIRD_PARTY_NOTICES.md`
  retains the Microsoft WinGet MIT notice and Wikidata CC0 declaration.

## Build tools and external support

Node.js, TypeScript, Vite, vite-plugin-solid, type definitions, Zig and the C/C++
toolchain are development tools, not applications automatically installed for
users. Compiler-supplied code linked into a binary is a separate distribution
question and must not be dismissed as build-only.

NVML/NVAPI and AMD driver interfaces use the installed vendor drivers. Ryzen
Master SDK, Intel PCM, PawnIO, LibreHardwareMonitor, Notebook FanControl, Windows
Performance Toolkit, MSI Afterburner and LatencyMon are external or optional
support. Listing them in credits does not bundle them, install them, replace
their license terms or authorize redistributing their drivers.

## Release assembly checks

Before distributing a new final image, match these notices to that image's exact
contents. The beta compiler/runtime provenance is recorded in
`NativeRuntime.NOTICE.md`; original .NET and ASP.NET Core notices are retained.
Optional provider and driver listings do not authorize bundling their binaries.
Validate source-to-package notice copies and resolved dependency membership;
gratitude is not a substitute for the applicable upstream terms.
