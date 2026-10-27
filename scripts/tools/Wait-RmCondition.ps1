#Requires -Version 5.1
<#
.SYNOPSIS
Wait for a condition string or a low-level hook JSON; print its first met event.
.DESCRIPTION
Uses the existing loopback API with the current process's Windows credentials.
Keyless local reads use the same verified Windows caller policy as the subscription channel; elevation is also accepted.
The backend's /api/hooks/vocabulary describes syntax, units, defaults and credential scopes.
Condition strings use /api/hooks/wait. HookJson is one raw hook object sent unchanged to the channel.
The explicit field parameters remain a direct low-level hook constructor.
.EXAMPLE
pwsh -NoProfile -File .\scripts\tools\Wait-RmCondition.ps1 -Condition 'memory.free < 1GiB for 60s'
#>
[CmdletBinding(DefaultParameterSetName = 'Condition')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Condition', Position = 0)][string]$Condition,
    [Parameter(Mandatory, ParameterSetName = 'Json')][string]$HookJson,
    [Parameter(ParameterSetName = 'Fields')][ValidateSet('machine', 'software', 'process', 'device')][string]$Binding = 'machine',
    [Parameter(Mandatory, ParameterSetName = 'Fields')][string]$MetricId,
    [Parameter(ParameterSetName = 'Fields')][ValidateSet('value', 'remaining', 'percentOfTotal')][string]$Quantity = 'value',
    [Parameter(Mandatory, ParameterSetName = 'Fields')][ValidateSet('below', 'atOrAbove')][string]$Comparison,
    [Parameter(Mandatory, ParameterSetName = 'Fields')][double]$Threshold,
    [Parameter(Mandatory, ParameterSetName = 'Fields')][double]$DurationSeconds,
    [Parameter(ParameterSetName = 'Fields')][ValidateRange(1, 2147483647)][int]$IntervalMs = 1000,
    [Parameter(ParameterSetName = 'Fields')][ValidateRange(0, 2147483647)][int]$TopOwners = 0,
    [Parameter(ParameterSetName = 'Fields')][string]$SoftwareId,
    [Parameter(ParameterSetName = 'Fields')][int]$ProcessId,
    [Parameter(ParameterSetName = 'Fields')][string]$StartKey,
    [Parameter(ParameterSetName = 'Fields')][string]$DeviceIdentity,
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
    if (-not $BaseUri.IsAbsoluteUri -or -not $BaseUri.IsLoopback -or $BaseUri.Scheme -notin @('http', 'https') -or
        $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.AbsolutePath -ne '/') {
        throw 'BaseUri must be a loopback HTTP(S) origin.'
    }
    if ($PSCmdlet.ParameterSetName -eq 'Fields') {
    if ([double]::IsNaN($Threshold) -or [double]::IsInfinity($Threshold) -or
        [double]::IsNaN($DurationSeconds) -or [double]::IsInfinity($DurationSeconds) -or $DurationSeconds -le 0) {
        throw 'Threshold must be finite and DurationSeconds must be finite and positive.'
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
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'Json') {
        # Preserve raw JSON so backend duplicate-property and strict-type checks still apply.
        $body = '{"version":1,"hooks":[' + $HookJson + ']}'
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseDefaultCredentials = $true
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
    if ($PSCmdlet.ParameterSetName -eq 'Condition') {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get,
            [uri]::new($BaseUri, '/api/hooks/wait?condition=' + [uri]::EscapeDataString($Condition)))
    }
    else {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, [uri]::new($BaseUri, '/api/subscriptions/stream'))
        $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')
    }
    if (-not [string]::IsNullOrWhiteSpace($AccessKey)) { $request.Headers.Add('X-Resource-Manager-Key', $AccessKey) }
    $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $reason = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "Condition channel HTTP $([int]$response.StatusCode): $reason"
    }
    if ($PSCmdlet.ParameterSetName -eq 'Condition') {
        $resultJson = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $result = $resultJson | ConvertFrom-Json
        if ($result.value.state -ne 'met') { throw 'Wait endpoint returned an unexpected event.' }
        [Console]::Out.WriteLine($resultJson)
        $exitCode = 0
    }
    else {
    $reader = [System.IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw 'Condition channel ended before the condition was met.' }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $frame = $line | ConvertFrom-Json
        $value = $frame.value
        if ($null -ne $value.PSObject.Properties['statusCode']) { throw "Subscription or hook rejected ($($value.statusCode)): $($value.error)" }
        if ($null -eq $frame.PSObject.Properties['hookId'] -or [string]::IsNullOrEmpty($frame.hookId)) { throw 'Unexpected hook frame.' }
        if ($value.state -eq 'met') { [Console]::Out.WriteLine($line); $exitCode = 0; break }
        if ($value.state -eq 'objectEnded') { throw "Condition object ended: $($value.reason)" }
        if ($value.state -notin @('expanded', 'cleared', 'unavailable')) { throw "Unexpected condition state: $($value.state)" }
    }
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
