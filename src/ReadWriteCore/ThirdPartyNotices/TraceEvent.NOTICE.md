# TraceEvent

The ETW implementation uses Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0 by
Microsoft, through a NuGet dependency. The selected PerfView v4.0.0 project
license is included verbatim as TraceEvent.LICENSE.txt; it carries the
.NET Foundation and Contributors copyright. Microsoft.MIT.txt preserves the
existing Microsoft component notice and does not replace that selected license.

Project and selected release:

- https://github.com/microsoft/perfview
- https://github.com/microsoft/perfview/releases/tag/v4.0.0
- https://raw.githubusercontent.com/microsoft/perfview/v4.0.0/LICENSE.TXT

The dependency package also contains native ETW and symbol helpers. Its NuGet
build assets control which helpers are copied into a consuming application;
these are not included as native assets in this ReadWriteCore package.
The selected PerfView build uses Microsoft.Diagnostics.Tracing.TraceEvent.SupportFiles
1.0.30 from Microsoft's perfview-build feed. Its package declares MIT with
Microsoft Corporation copyright; the accompanying Microsoft.MIT.txt preserves
that copyright and license. The selected TraceEvent package's KernelTraceControl,
msdia140, Dia2Lib and TraceReloggerLib helpers match that original package's files.
No per-file license exception or EULA is included in that original package.

SupportFiles source and selected build configuration:

- https://pkgs.dev.azure.com/dnceng/public/_packaging/perfview-build/nuget/v3/index.json
- https://raw.githubusercontent.com/microsoft/perfview/v4.0.0/Nuget.config
- https://raw.githubusercontent.com/microsoft/perfview/v4.0.0/src/Directory.Packages.props

The final distribution review must retain these notices alongside the actual
dependency assets. This records the original package's license declaration;
it does not grant separate Visual Studio or DIA SDK product rights.

We thank Microsoft and the TraceEvent contributors. Microsoft has not endorsed
Resource Manager or this SDK.
