# Static analysis

Opt-in entries that run off-the-shelf analyzers. They change no product project, build setting, lockfile or release step, and normal builds enable no extra rules. The private validation Lab and third-party code are excluded.

Run them from the repository with the worker launcher's short PATH:

```powershell
pwsh -NoProfile -File scripts/analysis/Invoke-DotnetStaticAnalysis.ps1 -RestoreAnalyzers
pwsh -NoProfile -File scripts/analysis/Invoke-WebNativeStaticAnalysis.ps1
```

## Queue and memory

- Each stage goes through `JobQueue.ps1 -Action Run` in its own serial slot, with a 4 GiB process-tree ceiling and BelowNormal priority.
- Never queue the entry script itself. It would hold its slot while its stages wait for later slots.
- CodeQL runs with `--threads=2 --ram=3072`.
- A limit failure is reported, never retried with a larger limit.
- Builds keep `--disable-build-servers`.

## Workspaces

- Results and CodeQL databases live in registered `.local` tasks.
- CodeQL databases use zero completed retention and are reclaimed once SARIF is written.
- Use a new `-RunName` for each run.
- A `lost` queue job, a partial database or partial SARIF is never accepted as input. Reclaim it through `Manage-GeneratedWorkspace` and rerun that stage.

## C# (`Invoke-DotnetStaticAnalysis.ps1`)

- Covers every tracked project under `src`, `sdk/csharp` and `tests`.
- SDK analyzers use `latest-all`. Third-party analyzers use their defaults and are pinned in `analyzers.json`.
- Analyzer assemblies are injected, so product restore graphs stay clean.
- `IDisposableAnalyzers` 4.0.8 was dropped: it crashes (AD0001) on function-pointer calls in ReadWriteCore.
- CodeQL uses no-build extraction.
- Explicit partial runs:
  - `-SkipCodeQL`;
  - `-RoslynOnly`, then `-Resume`;
  - `-UseExistingCodeQL`, which checks the CLI version, the SARIF hash and the input fingerprint;
  - `-AllowMissingAnalyzers`.
- The status manifest records every omission.
- Only files in `accepted-sarif.json` count as findings. A compile failure or missing SARIF is a coverage failure, not zero findings.
- After human review:
  - `Write-StaticAnalysisTriage.ps1` checks the review manifest's coverage;
  - `Write-StaticAnalysisReport.ps1` writes the short report.
  - Neither runs an analyzer; both work from existing results.

## Web, native and scripts (`Invoke-WebNativeStaticAnalysis.ps1`)

- Tools:
  - ESLint;
  - knip, with and without tests;
  - CodeQL for JavaScript and C/C++;
  - Cppcheck;
  - PSScriptAnalyzer with default rules;
  - Zig syntax checks only (Zig 0.16.0 has nothing deeper).
- `-Tools <name>` runs one tool.
- Analysis packages restore only under `node/`, with exact pins and their own lockfile. Restore the frontend from its own lockfile first.
- `-KeepOpen` leaves the result task running for triage. Complete it afterwards.
- `disposable-workspace-policy.json` is used only with an explicit owned-task selection, never to sweep other work.

## Output

Each run writes SARIF per tool, plus `summary.json` and `summary.md`, grouped by tool, rule, severity and area. Triage decides priority. Failed or partial outputs never feed a summary.
