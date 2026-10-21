# Owned core protocol consumer

This finite console process constructs production `FanCore` with one injected in-memory backend. It never runs default discovery or a real hardware backend. Its actual JSON server, temperature store, software curve engine and cleanup run unchanged. EOF disposes the core; exit 0 requires the fake fan to have returned to automatic control. A 30-second guard bounds a stalled test process.

The project references the existing sibling `FanControlCore` checkout only for this local validation fixture; it adds no production dependency. Build from the Resource Manager workspace, granting access to that sibling's build outputs when required:

```powershell
dotnet build tests/Fixtures/FanCoreProtocolProbe/FanCoreProtocolProbe.csproj --ignore-failed-sources -o .local/review-repairs-20261002/fan-core-protocol-probe
```

Set `RESOURCE_MANAGER_FAN_CORE_CONSUMER_TEST=1` and `RM_FAN_CORE_PROTOCOL_PROBE` to the built EXE. The gated `FanCurveReceiptTests` launch it through the original client process factory and communicate over actual redirected stdin/stdout. Target selection, writes, temperatures, receipt decoding, manual restoration and lost-temperature restoration are production consumers; only the final hardware read/write effects are in memory. The original three-second sample freshness and five-second blind timeout are preserved. No product service, UI, live sensor or physical fan is operated.

The redetection cases set fixture-only `RM_FAN_CORE_PROBE_DEFER_OPEN` to 1 or 2. The injected backend rejects that many initial open attempts and then accepts the next explicit retry. Like the product entrypoint, failed initial discovery leaves the command server running. Exit 0 also requires that detection has never driven the backend. Other cases explicitly disable this mode and verify refresh preserves an already active software plan and helper identity.

`RM_FAN_CORE_PROBE_FAIL_RESTORE=1` injects two fans: one has five failed software writes and its first release throws an owned `IOException`; the other keeps responding to temperature. The host case checks retry and continued writes. Direct `--restore-all-check` and `--engine-stop-all-check` modes exercise the original core/engine cleanup entrypoints, requiring the failed fan to stay owned and the other to be released before retry. All operations are in memory; the engine StopAll case uses an explicit long interval to isolate cleanup from ticking. Other tests explicitly disable fault mode.
