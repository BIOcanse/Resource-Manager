# GPU allocation capture configuration

The telemetry owner reads `Monitoring:GpuAllocationLiveTrackingThresholdSeconds`
once when it compiles its capture plan. The default is **10 seconds**: on candidate
`.16`, the measured ~280 ms rundown cost was equivalent to 8–10 seconds of
~35 ms/s live tracking.

At each native subscription due time, the shortest currently held GPU interval
selects the mechanism. An interval at or above the threshold uses one bounded
rundown and disables providers afterward. A shorter interval uses a rundown to
establish a live ledger, then publishes a complete snapshot at each due time.
Subscriptions and releases change the selection at the next due time.

Override through the host's normal configuration, for example the environment
variable `Monitoring__GpuAllocationLiveTrackingThresholdSeconds=15`. Zero always
uses rundown; a sufficiently large finite number always uses live tracking.
Negative and non-finite values are rejected. Restart the owner to apply a new
threshold. There is no periodic rescan configuration or timer.

Live event loss or decoding failure publishes an empty due value. Providers are
paused while the invalid ledger is discarded; the next due time performs one
rundown to rebuild. Reads only return the current published value.
