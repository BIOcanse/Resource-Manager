Set-StrictMode -Version Latest

function Test-ResourceManagerLegacyStartupAction {
    param([Parameter(Mandatory = $true)][object[]]$Actions)
    if ($Actions.Count -ne 1) { return $false }
    $action = $Actions[0]
    return [IO.Path]::IsPathRooted([string]$action.Execute) -and
        [IO.Path]::GetFileName([string]$action.Execute) -ieq 'ResourceManager.NativeUi.exe' -and
        ([string]$action.Arguments).Trim() -ceq '--background-startup'
}

function Remove-ResourceManagerLegacyStartupTask {
    # Upgrade cleanup only. This never registers or enables startup.
    $task = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop |
        Where-Object { $_.TaskName -ceq 'ResourceManager.UserLogonBootstrap' })
    if ($task.Count -eq 0) { return }
    if ($task.Count -ne 1 -or -not (Test-ResourceManagerLegacyStartupAction -Actions @($task[0].Actions))) {
        throw 'The legacy startup task has an unexpected action; it was not removed.'
    }
    Unregister-ScheduledTask -TaskName $task[0].TaskName -TaskPath '\' -Confirm:$false -ErrorAction Stop
}
