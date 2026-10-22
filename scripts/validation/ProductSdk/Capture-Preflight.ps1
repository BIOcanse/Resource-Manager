param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][ValidateSet('Bootstrap','Probe','Product')][string]$Scope,
    [Parameter(Mandatory)][ValidateRange(67108864, 8589934592)][ulong]$JobMemoryLimitBytes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$faultTaskRoot = 'D:\Code\Resource-Manager-Development\.local\subscription-core-stage-20261007'
$faultEvidence = [IO.Path]::GetFullPath((Join-Path $faultTaskRoot '../20261007-freeze-owner-evidence.json'))
$faultReview = [IO.Path]::GetFullPath((Join-Path $faultTaskRoot '../20261007-freeze-owner-response.md'))
if (-not (Test-Path -LiteralPath $faultEvidence -PathType Leaf) -or
    -not (Test-Path -LiteralPath $faultReview -PathType Leaf)) { throw 'Preserved fault evidence is missing.' }
if ($Scope -ne 'Product' -and $JobMemoryLimitBytes -gt 1073741824) { throw 'Bootstrap and metadata probe are limited to 1 GiB.' }
if (-not ('ResourceManagerStage.ExecutionMemory' -as [type])) {
    # OS page counters and their byte conversion follow PERFORMANCE_INFORMATION:
    # https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-performance_information
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ResourceManagerStage {
    public static class ExecutionMemory {
        [StructLayout(LayoutKind.Sequential)]
        public struct PerformanceInformation {
            public uint Size;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
            public UIntPtr SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);
        public static PerformanceInformation Read() {
            PerformanceInformation result;
            if (!GetPerformanceInfo(out result, checked((uint)Marshal.SizeOf<PerformanceInformation>())))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
    }
}
'@
}
$memory = [ResourceManagerStage.ExecutionMemory]::Read()
$observedAt = [DateTimeOffset]::UtcNow
$pageSize = $memory.PageSize.ToUInt64()
if ($pageSize -eq 0) { throw 'OS page size is unavailable.' }
$commitLimit = [ulong]($memory.CommitLimit.ToUInt64() * $pageSize)
$committed = [ulong]($memory.CommitTotal.ToUInt64() * $pageSize)
$availablePhysical = [ulong]($memory.PhysicalAvailable.ToUInt64() * $pageSize)
if ($commitLimit -eq 0 -or $commitLimit -lt $committed) { throw 'OS commit counters are unavailable or inconsistent.' }
$commitAvailable = $commitLimit - $committed
$commitReserve = [ulong]2147483648
$physicalReserve = [ulong]0
# User ruling2026-10-21: Job limits constrain committed memory. Physical
# availability is diagnostic only; Windows may page during a serial build.
if ($commitAvailable -lt ($JobMemoryLimitBytes + $commitReserve)) { throw 'Current commit headroom does not admit this command envelope.' }
$captureRoot = Join-Path $TaskRoot ('execution-preflight-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($captureRoot)
$headroomPath = Join-Path $captureRoot 'headroom.json'
$reviewPath = Join-Path $captureRoot 'fault-review.json'
$bootstrapPath = Join-Path $captureRoot 'bootstrap-preflight.json'
$headroom = [ordered]@{
    observedAt = $observedAt; source = 'GetPerformanceInfo PERFORMANCE_INFORMATION';
    commitLimitBytes = $commitLimit; committedBytes = $committed;
    commitAvailableBytes = $commitAvailable; availablePhysicalBytes = $availablePhysical
}
$review = [ordered]@{
    reviewed = $true; reviewer = 'Codex primary /root'; evidencePath = $faultEvidence;
    reviewPath = $faultReview; causeConfirmed = $false;
    conclusion = 'Abnormal shutdown/reboot confirmed; original process-tree peaks are unavailable. No stress reproduction.'
}
$bootstrap = [ordered]@{
    scope = 'execution-job-adapter-bootstrap-only'; observedAt = $observedAt;
    faultEvidenceReviewed = $true; adapterBootstrapPermitted = ($Scope -eq 'Bootstrap');
    evidencePath = $faultEvidence; commitAvailableBytes = $commitAvailable;
    availablePhysicalBytes = $availablePhysical; commitReserveBytes = $commitReserve;
    physicalReserveBytes = $physicalReserve
}
foreach ($entry in @(@{Path=$headroomPath;Data=$headroom},@{Path=$reviewPath;Data=$review},@{Path=$bootstrapPath;Data=$bootstrap})) {
    [IO.File]::WriteAllText($entry.Path, (ConvertTo-Json -InputObject $entry.Data -Depth 5), [Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{
    scope=$Scope; headroomPath=$headroomPath; faultReviewPath=$reviewPath;
    bootstrapPreflightPath=$bootstrapPath; observedAt=$observedAt;
    commitAvailableBytes=$commitAvailable; availablePhysicalBytes=$availablePhysical;
    jobMemoryLimitBytes=$JobMemoryLimitBytes; commitReserveBytes=$commitReserve;
    physicalReserveBytes=$physicalReserve
}
