# Owned CPU ETL recorder

This finite Windows fixture records Process, Thread and ContextSwitch events into a fresh ETL file for `EtwCpuCoreResidencyEtlReplayTests`. It uses the same TraceEvent version as the backend.

Build from the development workspace:

```powershell
dotnet build tests/Fixtures/KernelEtwCaptureProbe/KernelEtwCaptureProbe.csproj -o .local/kernel-etl-capture
```

Run the built EXE elevated, passing one new ETL path in an existing evidence directory. The fixture refuses to overwrite an existing file. It runs at most four bounded workers for twelve seconds, stops its own uniquely named session and checks that the session is gone. It does not start product services, perform global orphan cleanup, change process placement or tune hardware.

Set `RESOURCE_MANAGER_ETL_REPLAY_PATH` to that recorded file and run the existing replay facts:

```powershell
dotnet test 'tests/App/Resource Manager-APP.Tests.csproj' --filter 'FullyQualifiedName~EtwCpuCoreResidencyEtlReplayTests'
```

An omitted replay path skips these facts. This CPU recording does not cover GPU allocation, cross-session installation or actual PID reuse.
