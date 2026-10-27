[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output, [switch]$Names)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Thin build adapter; registry semantics belong to the shared generator.
& (Join-Path $PSScriptRoot 'Generate-StateItems.ps1') -Registry (Join-Path $PSScriptRoot '../../src/Shared/StateItems/state-items.json') -Process desktop -Language Zig -Names ([bool]$Names) -Output $Output
