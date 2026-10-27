#Requires -Version 5.1
<#
.SYNOPSIS
Create a Resource Manager hook that owns its sampling subscription; print its first met event.
.DESCRIPTION
Uses the existing loopback API with the current process's Windows credentials.
Keyless protected reads require an elevated caller, as with other Resource Manager reads.
An explicit trusted access key needs metrics.read and processes.read.
All numeric thresholds use the selected metric's unit (percentOfTotal uses percent).
.EXAMPLE
pwsh -NoProfile -File .\scripts\tools\Wait-RmCondition.ps1 -Binding machine -MetricId memory.usage -Quantity remaining -Comparison below -Threshold 1073741824 -DurationSeconds 60 -IntervalMs 1000 -TopOwners 5
#>
[CmdletBinding()]
param(
    [ValidateSet('machine', 'software', 'process', 'device')][string]$Binding = 'machine',
    [Parameter(Mandatory)][string]$MetricId,
    [ValidateSet('value', 'remaining', 'percentOfTotal')][string]$Quantity = 'value',
    [Parameter(Mandatory)][ValidateSet('below', 'atOrAbove')][string]$Comparison,
    [Parameter(Mandatory)][double]$Threshold,
    [Parameter(Mandatory)][double]$DurationSeconds,
    [ValidateRange(1, 2147483647)][int]$IntervalMs = 1000,
    [ValidateRange(0, 2147483647)][int]$TopOwners = 0,
    [string]$SoftwareId,
    [int]$ProcessId,
    [string]$StartKey,
    [string]$DeviceIdentity,
    [uri]$BaseUri = 'http://127.0.0.1:9321',
    [string]$AccessKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$client = $null
$handler = $null
$request = $null
$response = $null
$reader = $null
$exitCode = 1
try {
    if ([double]::IsNaN($Threshold) -or [double]::IsInfinity($Threshold) -or
        [double]::IsNaN($DurationSeconds) -or [double]::IsInfinity($DurationSeconds) -or $DurationSeconds -le 0) {
        throw 'Threshold must be finite and DurationSeconds must be finite and positive.'
    }
    if (-not $BaseUri.IsAbsoluteUri -or -not $BaseUri.IsLoopback -or $BaseUri.Scheme -notin @('http', 'https') -or
        $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.AbsolutePath -ne '/') {
        throw 'BaseUri must be a loopback HTTP(S) origin.'
    }
    if (($Binding -eq 'software') -ne (-not [string]::IsNullOrWhiteSpace($SoftwareId))) {
        throw 'Supply SoftwareId exactly when Binding is software.'
    }
    if ($Binding -eq 'process') {
        $parsedStartKey = [ulong]0
        if ($ProcessId -le 0 -or -not [ulong]::TryParse($StartKey, [ref]$parsedStartKey) -or $parsedStartKey -eq 0) {
            throw 'A process binding requires a positive ProcessId and StartKey (Windows creation file time).'
        }
        $StartKey = $parsedStartKey.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ($ProcessId -ne 0 -or -not [string]::IsNullOrEmpty($StartKey)) { throw 'ProcessId and StartKey require Binding process.' }
    if (($Binding -eq 'device') -ne (-not [string]::IsNullOrWhiteSpace($DeviceIdentity))) { throw 'Supply DeviceIdentity exactly when Binding is device.' }
    $rule = [ordered]@{
        binding = $Binding; metricId = $MetricId; quantity = $Quantity; comparison = $Comparison
        threshold = $Threshold; holdSeconds = $DurationSeconds; topOwners = $TopOwners
    }
    if ($Binding -eq 'software') { $rule.softwareId = $SoftwareId }
    if ($Binding -eq 'process') { $rule.processId = $ProcessId; $rule.startKey = $StartKey }
    if ($Binding -eq 'device') { $rule.deviceIdentity = $DeviceIdentity }
    $body = @{ version = 1; hooks = @(@{ id = 'condition'; rule = $rule; intervalMs = $IntervalMs }) } | ConvertTo-Json -Depth 5 -Compress

    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseDefaultCredentials = $true
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, [uri]::new($BaseUri, '/api/subscriptions/stream'))
    $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')
    if (-not [string]::IsNullOrWhiteSpace($AccessKey)) { $request.Headers.Add('X-Resource-Manager-Key', $AccessKey) }
    $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $reason = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "Condition channel HTTP $([int]$response.StatusCode): $reason"
    }
    $reader = [System.IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw 'Condition channel ended before the condition was met.' }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $frame = $line | ConvertFrom-Json
        if ($null -eq $frame.PSObject.Properties['hookId'] -or $frame.hookId -ne 'condition') { throw 'Unexpected hook frame.' }
        $value = $frame.value
        if ($null -ne $value.PSObject.Properties['statusCode']) { throw "Subscription or hook rejected ($($value.statusCode)): $($value.error)" }
        if ($value.state -eq 'met') { [Console]::Out.WriteLine($line); $exitCode = 0; break }
        if ($value.state -eq 'objectEnded') { throw "Condition object ended: $($value.reason)" }
        if ($value.state -notin @('cleared', 'unavailable')) { throw "Unexpected condition state: $($value.state)" }
    }
}
catch { [Console]::Error.WriteLine($_.Exception.Message) }
finally {
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $response) { $response.Dispose() }
    if ($null -ne $request) { $request.Dispose() }
    if ($null -ne $client) { $client.Dispose() }
    elseif ($null -ne $handler) { $handler.Dispose() }
}
exit $exitCode
