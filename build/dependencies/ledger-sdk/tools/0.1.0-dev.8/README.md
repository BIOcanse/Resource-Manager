# Ledger SDK local tools

This tool manifest pins `ledger-build` and `ledger-configuration` to the candidate's package version. Configure NuGet with the ledger package directory and the official FreedomPoint feed, then run `dotnet tool restore` from this directory. For the local feed layout, the ledger source is `../../packages`; add the formal FreedomPoint feed for the `FreedomPoint.Core` and `FreedomPoint.Generators` dependencies.

`ledger-configuration` generates/checks the FreedomPoint-backed ledger structure and values. `ledger-build` generates and verifies the per-consumer owner and Native entry source. The candidate manifest records which official FreedomPoint package versions were used to build the configuration tool.
