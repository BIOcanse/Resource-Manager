# Managed Dependency Acknowledgements

We thank every author and contributor listed below. Microsoft has not signed, certified or endorsed Resource Manager.

This inventory covers the resolved product, desktop, installer, uninstaller, external WinUI manager, test and Adapter SDK graphs. Names, authors and license declarations come from NuGet package metadata. FreedomPoint dev.45 declares Apache-2.0 and BIOcanse. The currently pinned Ledger and Disk packages do not declare a license in their nuspec or include a license file; this inventory records that absence without assigning a license. Runtime/reference packs and publish tools are identified separately from shipped application code. Existing upstream license files are preserved alongside this inventory.

| Package/version | Package authors | Declared license | Resolved project(s) |
| --- | --- | --- | --- |
| BlackSharp.Core/1.0.7 | BlackSharp.Core | MPL-2.0 | src/Core, tests/App |
| coverlet.collector/6.0.4 | tonerdo | MIT | tests/App |
| Disk.ReadWrite/0.1.0 | BIOcanse | Not declared in package metadata | src/Core, tests/App |
| DiskInfoToolkit/1.1.2 | DiskInfoToolkit | MPL-2.0 | src/Core, tests/App |
| FanControlCore.Library/0.1.0-dev.6 | BIOcanse | Apache-2.0 | src/Core, tests/App |
| FreedomPoint.Core/0.1.0-dev.45 | BIOcanse | Apache-2.0 | src/Core, src/UI/Core, tests/App |
| HidSharp/2.6.4 | James F. Bellinger | LICENSE.txt | src/Core, tests/App |
| LibreHardwareMonitorLib/0.9.6 | LibreHardwareMonitorLib | MPL-2.0 | src/Core, tests/App |
| Microsoft.AspNetCore.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | src/Uninstaller runtime/reference pack, src/Updater/WinUI runtime/reference pack |
| Microsoft.Bcl.AsyncInterfaces/10.0.0 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| Microsoft.CodeCoverage/17.14.1 | Microsoft | MIT | tests/App |
| Microsoft.Data.Sqlite.Core/10.0.9 | Microsoft | MIT | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| Microsoft.Data.Sqlite/10.0.9 | Microsoft | MIT | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| Microsoft.Diagnostics.NETCore.Client/0.2.510501 | Microsoft | MIT | src/Core, tests/App |
| Microsoft.Diagnostics.Tracing.TraceEvent/3.2.4 | Microsoft | MIT | src/Core, tests/App |
| Microsoft.Extensions.Configuration.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.Binder/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.CommandLine/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.EnvironmentVariables/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.FileExtensions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.Json/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration.UserSecrets/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Configuration/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.DependencyInjection.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.DependencyInjection/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Diagnostics.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Diagnostics/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.FileProviders.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.FileProviders.Physical/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.FileSystemGlobbing/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Hosting.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Hosting.WindowsServices/10.0.11 | Microsoft | MIT | src/Core, tests/App |
| Microsoft.Extensions.Hosting/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.Abstractions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.Configuration/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.Console/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.Debug/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.EventLog/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging.EventSource/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Logging/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Options.ConfigurationExtensions/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Options/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.Extensions.Primitives/10.0.11 | Microsoft | MIT | tests/App |
| Microsoft.NET.ILLink.Tasks/10.0.8 | Microsoft | MIT | src/Uninstaller, src/Updater/WinUI |
| Microsoft.NET.Test.Sdk/17.14.1 | Microsoft | MIT | tests/App |
| Microsoft.NETCore.App.Crossgen2.win-x64/10.0.8 | Microsoft | MIT | src/Uninstaller runtime/reference pack, src/Updater/WinUI runtime/reference pack |
| Microsoft.NETCore.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | src/Uninstaller runtime/reference pack, src/Updater/WinUI runtime/reference pack |
| Microsoft.TestPlatform.ObjectModel/17.14.1 | Microsoft | MIT | tests/App |
| Microsoft.TestPlatform.TestHost/17.14.1 | Microsoft | MIT | tests/App |
| Microsoft.Web.WebView2/1.0.2792.45 | Microsoft | LICENSE.txt | src/UI/Core, tests/App |
| Microsoft.Web.WebView2/1.0.3719.77 | Microsoft | file: LICENSE.txt | src/Updater/WinUI |
| Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100 | Microsoft | file: sdk_license.txt | src/Updater/WinUI |
| Microsoft.Windows.SDK.BuildTools/10.0.26100.9169 | Microsoft | https://aka.ms/WinSDKLicenseURL | src/Updater/WinUI |
| Microsoft.Windows.SDK.NET.Ref/10.0.26100.57 | Microsoft | https://aka.ms/WinSDKLicenseURL | src/Updater/WinUI runtime/reference pack |
| Microsoft.WindowsAppSDK.Base/2.0.4 | Microsoft | file: license.txt | src/Updater/WinUI |
| Microsoft.WindowsAppSDK.Foundation/2.3.12 | Microsoft | file: license.txt | src/Updater/WinUI |
| Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.9 | Microsoft | file: license.txt | src/Updater/WinUI |
| Microsoft.WindowsAppSDK.WinUI/2.3.9 | Microsoft | file: license.txt | src/Updater/WinUI |
| Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | src/Uninstaller runtime/reference pack, src/Updater/WinUI runtime/reference pack |
| Mono.Posix.NETStandard/1.0.0 | Microsoft | See package license URL | src/Core, tests/App |
| NETStandard.Library.Ref/2.1.0 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions runtime/reference pack |
| Newtonsoft.Json/13.0.3 | James Newton-King | MIT | tests/App |
| RAMSPDToolkit-NDD/1.4.2 | RAMSPDToolkit-NDD | MPL-2.0 | src/Core, tests/App |
| ResourceManager.Ledger/0.1.0-dev.2 | BIOcanse | Not declared in package metadata | src/Core, tests/App |
| runtime.android-arm.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.android-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.android-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.android-x86.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-arm.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-bionic-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-bionic-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-musl-arm.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-musl-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-musl-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.linux-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.maccatalyst-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.maccatalyst-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.osx-arm64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| runtime.osx-x64.runtime.native.System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| SourceGear.sqlite3/3.50.4.5 | Eric Sink | LICENSE.txt | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| SQLitePCLRaw.bundle_e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| SQLitePCLRaw.config.e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| SQLitePCLRaw.core/3.0.3 | Eric Sink | Apache-2.0 | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| SQLitePCLRaw.provider.e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | src/Core, src/Installer, src/UI/Launcher, src/Updater, src/Updater/WinUI, tests/App |
| System.Buffers/4.6.1 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.CodeDom/10.0.2 | Microsoft | MIT | src/Core, tests/App |
| System.Diagnostics.EventLog/10.0.11 | Microsoft | MIT | tests/App |
| System.IO.FileSystem.AccessControl/5.0.0 | Microsoft | MIT | src/Core, tests/App |
| System.IO.Pipelines/10.0.0 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.IO.Ports/10.0.3 | Microsoft | MIT | src/Core, tests/App |
| System.Management/10.0.2 | Microsoft | MIT | src/Core, tests/App |
| System.Memory/4.6.3 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.Reflection.TypeExtensions/4.7.0 | Microsoft | MIT | src/Core, tests/App |
| System.Runtime.CompilerServices.Unsafe/6.1.2 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.ServiceProcess.ServiceController/10.0.11 | Microsoft | MIT | src/Core, tests/App |
| System.Text.Encodings.Web/10.0.0 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.Text.Json/10.0.0 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| System.Threading.Tasks.Extensions/4.6.3 | Microsoft | MIT | sdk/csharp/ResourceManager.Adapter.Abstractions |
| xunit.abstractions/2.0.3 | James Newkirk,Brad Wilson | See package license | tests/App |
| xunit.analyzers/1.18.0 | jnewkirk,bradwilson,marcind | Apache-2.0 | tests/App |
| xunit.assert/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| xunit.core/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| xunit.extensibility.core/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| xunit.extensibility.execution/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| xunit.runner.visualstudio/3.1.4 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| xunit/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | tests/App |
| Microsoft.NET.ILLink.Tasks/10.0.8 | Microsoft | MIT | src/Uninstaller, src/Updater/WinUI |
| Microsoft.NETCore.App.Crossgen2.win-x64/10.0.8 | Microsoft | MIT | src/Uninstaller runtime/reference pack, src/Updater/WinUI runtime/reference pack |
