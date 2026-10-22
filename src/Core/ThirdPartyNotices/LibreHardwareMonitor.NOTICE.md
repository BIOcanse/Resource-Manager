# LibreHardwareMonitorLib 0.9.6 and its resolved core dependencies

Official, unmodified NuGet build: https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6
Corresponding upstream source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701
Library license: MPL-2.0. Original upstream license and third-party notices accompany this file.
Embedded signed PawnIO modules retain their LGPL-2.1 notice in the upstream notices.
Resource Manager does not modify the library, ship the GUI, start its HTTP server,
or install a driver silently. Motherboard and Memory are the only enabled groups.
Source and module versions are separate; Resource Manager does not replace upstream
embedded modules with its AMD/Intel source's separately acquired modules.

The following separate, unmodified dependencies retain their own licenses:

- DiskInfoToolkit 1.1.2 (Florian K.), MPL-2.0:
  https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea
- RAMSPDToolkit-NDD 1.4.2 (Florian K.), MPL-2.0:
  https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce
- BlackSharp.Core 1.0.7 (Florian K.), MPL-2.0:
  https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0
- HidSharp 2.6.4 (James F. Bellinger), Apache-2.0: the original package
  LICENSE.txt is preserved as LibreHardwareMonitor.HidSharp.LICENSE.txt.
- Mono.Posix.NETStandard 1.0.0 (Microsoft), MIT class-library terms:
  its original NuGet licenseUrl https://go.microsoft.com/fwlink/?linkid=869050
  resolves to https://github.com/mono/mono/blob/main/LICENSE. That full upstream
  notice is preserved as LibreHardwareMonitor.Mono-LICENSE.txt; its descriptions
  of other Mono components do not relicense this class library as MPL or GPL.
- System.IO.Ports, System.Management, System.CodeDom and
  System.IO.FileSystem.AccessControl are Microsoft runtime components, MIT.
  See ManagedDependencyAcknowledgements.md for the resolved versions and graphs.

The MPL source links above provide the corresponding unmodified source.
No Resource Manager proprietary restriction overrides recipients' rights in these
separate components. GUI-only upstream acknowledgements retained verbatim in the
original notices do not imply that Resource Manager ships the GUI.
