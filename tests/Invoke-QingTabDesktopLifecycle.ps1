[CmdletBinding()]
param(
    [ValidateSet('Audit', 'ExplorerRestart', 'Logoff')]
    [string]$Scenario = 'Audit',
    [ValidateSet('Prepare', 'Verify')]
    [string]$Phase = 'Prepare',
    [Parameter(Mandatory = $true)]
    [string]$CandidateExe,
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'QingTab\lifecycle-test-state.json'),
    [string]$ReportPath = (Join-Path $env:LOCALAPPDATA 'QingTab\lifecycle-test-report.json'),
    [switch]$AllowDesktopDisruption
)

$ErrorActionPreference = 'Stop'

function Get-CandidateInfo
{
    param([string]$Executable)

    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf))
    {
        throw "Candidate does not exist: $Executable"
    }
    $resolved = (Resolve-Path -LiteralPath $Executable).Path
    $item = Get-Item -LiteralPath $resolved
    if ($item.VersionInfo.FileVersion -ne '0.2.7.0')
    {
        throw "This harness requires QingTab 0.2.7.0, found $($item.VersionInfo.FileVersion)."
    }

    [pscustomobject]@{
        Path = $resolved
        FileVersion = $item.VersionInfo.FileVersion
        Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    }
}

function Assert-DisruptionAllowed
{
    if (-not $AllowDesktopDisruption)
    {
        throw 'This scenario intentionally restarts Explorer or logs off Windows. Re-run with -AllowDesktopDisruption in a dedicated test account after saving all work.'
    }
}

function Assert-NoQingTabProcess
{
    $running = Get-Process -Name 'QingTab' -ErrorAction SilentlyContinue
    if ($running)
    {
        throw 'A QingTab process is already running. This harness refuses to stop or replace it.'
    }
}

function Write-JsonFile
{
    param([string]$Path, [object]$Value)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory))
    {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $Value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding UTF8
}

$candidate = Get-CandidateInfo -Executable $CandidateExe

if ($Scenario -eq 'Audit')
{
    [pscustomobject]@{
        Result = 'PASS'
        Scenario = 'Audit'
        Candidate = $candidate
        QingTabProcesses = @(Get-Process -Name 'QingTab' -ErrorAction SilentlyContinue).Count
        ExplorerProcesses = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue).Count
        Note = 'Read-only audit; no process, registry, Explorer window, or login session was changed.'
    }
    return
}

Assert-DisruptionAllowed

if ($Scenario -eq 'ExplorerRestart')
{
    Assert-NoQingTabProcess
    $ownedCandidate = $null
    try
    {
        $ownedCandidate = Start-Process -FilePath $candidate.Path -ArgumentList @(
            '--portable',
            '--no-registration-repair',
            '--test-enable-direct-open'
        ) -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 3
        if ($ownedCandidate.HasExited)
        {
            throw 'The isolated QingTab candidate exited before Explorer restart testing began.'
        }

        $explorerBefore = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)
        if ($explorerBefore.Count -eq 0)
        {
            throw 'No Explorer shell process was found.'
        }
        $explorerBefore | Stop-Process -Force

        $explorerAfter = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline)
        {
            Start-Sleep -Milliseconds 500
            $explorerAfter = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)
            if ($explorerAfter.Count -gt 0)
            {
                break
            }
        }
        if ($null -eq $explorerAfter -or $explorerAfter.Count -eq 0)
        {
            throw 'Explorer did not restart within 30 seconds.'
        }
        $ownedCandidate.Refresh()
        if ($ownedCandidate.HasExited)
        {
            throw 'QingTab did not survive the Explorer restart.'
        }

        $report = [pscustomobject]@{
            Result = 'PASS'
            Scenario = 'ExplorerRestart'
            CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
            Candidate = $candidate
            ExplorerProcessCountBefore = $explorerBefore.Count
            ExplorerProcessCountAfter = $explorerAfter.Count
            CandidateSurvived = $true
        }
        Write-JsonFile -Path $ReportPath -Value $report
        $report
    }
    finally
    {
        if ($null -ne $ownedCandidate)
        {
            $ownedCandidate.Refresh()
            if (-not $ownedCandidate.HasExited)
            {
                Stop-Process -Id $ownedCandidate.Id -Force -ErrorAction SilentlyContinue
            }
            $ownedCandidate.Dispose()
        }
    }
    return
}

if ($Phase -eq 'Prepare')
{
    Assert-NoQingTabProcess
    $ownedCandidate = Start-Process -FilePath $candidate.Path -ArgumentList @(
        '--portable',
        '--no-registration-repair',
        '--test-enable-direct-open'
    ) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    if ($ownedCandidate.HasExited)
    {
        throw 'The isolated QingTab candidate exited before logoff testing began.'
    }

    $state = [pscustomobject]@{
        Scenario = 'Logoff'
        PreparedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Candidate = $candidate
        ProcessId = $ownedCandidate.Id
        ProcessStartTimeUtc = $ownedCandidate.StartTime.ToUniversalTime().ToString('o')
    }
    Write-JsonFile -Path $StatePath -Value $state

    $shell = (Get-Process -Id $PID).Path
    $commandFormat = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -Scenario Logoff -Phase Verify -CandidateExe "{2}" -StatePath "{3}" -ReportPath "{4}"'
    $command = $commandFormat -f $shell, $PSCommandPath, $candidate.Path, $StatePath, $ReportPath
    $runOnceArguments = @{
        LiteralPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
        Name = 'QingTabLifecycleVerify'
        PropertyType = 'String'
        Value = $command
        Force = $true
    }
    New-ItemProperty @runOnceArguments | Out-Null

    shutdown.exe /l
    if ($LASTEXITCODE -ne 0)
    {
        Stop-Process -Id $ownedCandidate.Id -Force -ErrorAction SilentlyContinue
        throw "Windows logoff command failed with exit code $LASTEXITCODE."
    }
    return
}

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf))
{
    throw "Logoff checkpoint is missing: $StatePath"
}
$state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
$sameProcessSurvived = $false
$process = Get-Process -Id ([int]$state.ProcessId) -ErrorAction SilentlyContinue
if ($null -ne $process)
{
    $sameProcessSurvived = $process.ProcessName -eq 'QingTab' -and
        $process.StartTime.ToUniversalTime().ToString('o') -eq [string]$state.ProcessStartTimeUtc
}
if ($sameProcessSurvived)
{
    throw 'The pre-logoff QingTab process unexpectedly survived into the new login session.'
}

$report = [pscustomobject]@{
    Result = 'PASS'
    Scenario = 'Logoff'
    VerifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Candidate = $candidate
    PreLogoffProcessTerminated = $true
    Checkpoint = $state
}
Write-JsonFile -Path $ReportPath -Value $report
$report
