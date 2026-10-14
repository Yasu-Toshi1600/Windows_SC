[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateRange(1, 2147483647)] [int] $TargetProcessId,
    [ValidateRange(1, 86400)] [int] $WatchSeconds = 1800,
    [ValidateRange(0, 60)] [int] $AfterSeconds = 15,
    [string] $LogDirectory = (Join-Path $env:LOCALAPPDATA 'Windows_SC\Logs\MemoryFlight'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\memory-flight')
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an administrator PowerShell. CSV recording itself does not require elevation.'
}
$targetProcess = Get-Process -Id $TargetProcessId
$instance = 'WindowsSCMemory-' + [guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $OutputDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $instance)
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$tracePath = Join-Path $runDirectory 'memory.etl'
$signalPath = Join-Path $LogDirectory 'trigger.txt'
$baseline = if (Test-Path -LiteralPath $signalPath) { Get-Content -LiteralPath $signalPath -Raw } else { '' }
$started = $false
$reason = 'interrupted'
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    # A unique instance owns both start and stop. Never cancel another recording.
    & wpr.exe -start ((Join-Path $PSScriptRoot 'MemoryFlight.wprp') + '!MemoryFlight') -instancename $instance
    if ($LASTEXITCODE -ne 0) { throw "WPR start failed: $LASTEXITCODE" }
    $started = $true
    @("instance=$instance", "pid=$TargetProcessId", "start=$([DateTimeOffset]::Now.ToString('O'))") |
        Set-Content -LiteralPath (Join-Path $runDirectory 'status.txt') -Encoding UTF8
    Write-Host "Waiting for PID $TargetProcessId memory change (maximum $WatchSeconds seconds)."
    while ($timer.Elapsed.TotalSeconds -lt $WatchSeconds) {
        $targetProcess.Refresh()
        if ($targetProcess.HasExited) { $reason = 'process-exited'; break }
        $signal = if (Test-Path -LiteralPath $signalPath) { Get-Content -LiteralPath $signalPath -Raw } else { '' }
        if ($signal -ne $baseline -and $signal.StartsWith("${TargetProcessId}:")) {
            $signal | Set-Content -LiteralPath (Join-Path $runDirectory 'trigger.txt') -Encoding UTF8
            $reason = 'trigger'
            $after = [Diagnostics.Stopwatch]::StartNew()
            while ($after.Elapsed.TotalSeconds -lt $AfterSeconds) { Start-Sleep -Milliseconds 500 }
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if ($reason -eq 'interrupted') { $reason = 'timeout' }
}
finally {
    if ($started) {
        try {
            & wpr.exe -status collectors -details -instancename $instance |
                Set-Content -LiteralPath (Join-Path $runDirectory 'wpr-status.txt') -Encoding UTF8
        }
        catch { Write-Warning 'Could not save WPR status; stopping the recording now.' }
        & wpr.exe -stop $tracePath -instancename $instance
        $stopExitCode = $LASTEXITCODE
        "reason=$reason`nstop-exit-code=$stopExitCode`nend=$([DateTimeOffset]::Now.ToString('O'))" |
            Add-Content -LiteralPath (Join-Path $runDirectory 'status.txt') -Encoding UTF8
        if ($stopExitCode -ne 0) {
            Write-Warning "WPR stop failed. Inspect this instance: wpr -status -instancename $instance"
        }
        # A bounded snapshot of the recorder-owned CSVs; no settings or ordinary logs.
        foreach ($baseName in @('samples', 'events')) {
            foreach ($suffix in @('', '.1', '.2', '.3')) {
                $source = Join-Path $LogDirectory "$baseName$suffix.csv"
                if (Test-Path -LiteralPath $source) {
                    try { Copy-Item -LiteralPath $source -Destination $runDirectory }
                    catch { Write-Warning "Could not copy $baseName$suffix.csv ($($_.Exception.GetType().Name))" }
                }
            }
        }
        Write-Host "Saved: $runDirectory"
    }
    $targetProcess.Dispose()
}
