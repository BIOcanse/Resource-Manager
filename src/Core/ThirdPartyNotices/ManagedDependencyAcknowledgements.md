# Managed Dependency Acknowledgements

We sincerely thank every author and contributor listed below, including test tools and transitive dependencies. Special thanks to Microsoft, the .NET Foundation, and the Windows developer community: their platforms, APIs, runtime and tooling make this independent application possible. Microsoft has not signed, certified or endorsed Resource Manager.

This inventory covers the resolved build/publish NuGet graphs of APP, Native UI, Shared, Launcher, APP.Tests and both managed Adapter SDK projects, plus the selected runtime/reference packs, on 2026-09-13 (71 package/version entries). Names and authors come from their actual package metadata. Framework-provided code and test-only assemblies are not automatically separate shipped binaries. License expressions and license-file references are retained as declared; full notices are separate. ILLink.Tasks and Crossgen2 are publish tools, not application runtime dependencies.

| Package/version | Package authors | Declared license | Resolved project(s) |
| --- | --- | --- | --- |
| NETStandard.Library.Ref/2.1.0 | Microsoft | MIT | Adapter.Abstractions reference pack; not shipped |
| Microsoft.Bcl.AsyncInterfaces/10.0.0 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Buffers/4.6.1 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.IO.Pipelines/10.0.0 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Memory/4.6.3 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Runtime.CompilerServices.Unsafe/6.1.2 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Text.Encodings.Web/10.0.0 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Text.Json/10.0.0 | Microsoft | MIT | Adapter.Abstractions build dependency |
| System.Threading.Tasks.Extensions/4.6.3 | Microsoft | MIT | Adapter.Abstractions build dependency |
| Microsoft.NETCore.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | self-contained runtime pack |
| Microsoft.AspNetCore.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | self-contained runtime pack |
| Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.8 | Microsoft | MIT | self-contained runtime pack |
| Microsoft.NETCore.App.Crossgen2.win-x64/10.0.8 | Microsoft | MIT | publish tool; not shipped |
| Microsoft.NET.ILLink.Tasks/10.0.8 | Microsoft | MIT | APP, Native UI, Shared, Launcher publish tool |
| coverlet.collector/6.0.4 | tonerdo | MIT | APP.Tests |
| Microsoft.CodeCoverage/17.14.1 | Microsoft | MIT | APP.Tests |
| Microsoft.Data.Sqlite.Core/10.0.9 | Microsoft | MIT | APP, APP.Tests |
| Microsoft.Data.Sqlite/10.0.9 | Microsoft | MIT | APP, APP.Tests |
| Microsoft.Diagnostics.NETCore.Client/0.2.510501 | Microsoft | MIT | APP, APP.Tests |
| Microsoft.Diagnostics.Tracing.TraceEvent/3.2.4 | Microsoft | MIT | APP, APP.Tests |
| Microsoft.Extensions.Configuration.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.Binder/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.CommandLine/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.EnvironmentVariables/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.FileExtensions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.Json/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration.UserSecrets/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Configuration/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.DependencyInjection.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.DependencyInjection/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Diagnostics.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Diagnostics/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.FileProviders.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.FileProviders.Physical/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.FileSystemGlobbing/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Hosting.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Hosting.WindowsServices/10.0.11 | Microsoft | MIT | APP, APP.Tests |
| Microsoft.Extensions.Hosting/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.Abstractions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.Configuration/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.Console/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.Debug/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.EventLog/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging.EventSource/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Logging/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Options.ConfigurationExtensions/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Options/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.Extensions.Primitives/10.0.11 | Microsoft | MIT | APP.Tests |
| Microsoft.NET.Test.Sdk/17.14.1 | Microsoft | MIT | APP.Tests |
| Microsoft.TestPlatform.ObjectModel/17.14.1 | Microsoft | MIT | APP.Tests |
| Microsoft.TestPlatform.TestHost/17.14.1 | Microsoft | MIT | APP.Tests |
| Microsoft.Web.WebView2/1.0.2792.45 | Microsoft | LICENSE.txt | APP\NativeUi, APP.Tests |
| Newtonsoft.Json/13.0.3 | James Newton-King | MIT | APP.Tests |
| SourceGear.sqlite3/3.50.4.5 | Eric Sink | LICENSE.txt | APP, APP.Tests |
| SQLitePCLRaw.bundle_e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | APP, APP.Tests |
| SQLitePCLRaw.config.e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | APP, APP.Tests |
| SQLitePCLRaw.core/3.0.3 | Eric Sink | Apache-2.0 | APP, APP.Tests |
| SQLitePCLRaw.provider.e_sqlite3/3.0.3 | Eric Sink | Apache-2.0 | APP, APP.Tests |
| System.CodeDom/10.0.2 | Microsoft | MIT | APP, APP.Tests |
| System.Diagnostics.EventLog/10.0.11 | Microsoft | MIT | APP.Tests |
| System.Management/10.0.2 | Microsoft | MIT | APP, APP.Tests |
| System.Reflection.TypeExtensions/4.7.0 | Microsoft | MIT | APP, APP.Tests |
| System.ServiceProcess.ServiceController/10.0.11 | Microsoft | MIT | APP, APP.Tests |
| xunit.abstractions/2.0.3 | James Newkirk,Brad Wilson | See package license | APP.Tests |
| xunit.analyzers/1.18.0 | jnewkirk,bradwilson,marcind | Apache-2.0 | APP.Tests |
| xunit.assert/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
| xunit.core/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
| xunit.extensibility.core/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
| xunit.extensibility.execution/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
| xunit.runner.visualstudio/3.1.4 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
| xunit/2.9.3 | jnewkirk,bradwilson | Apache-2.0 | APP.Tests |
