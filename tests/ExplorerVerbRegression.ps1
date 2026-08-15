param(
    [string]$DriveRoot = 'E:\',
    [string]$TargetFolder = (Join-Path $PSScriptRoot 'fixtures\native-window-target'),
    [string]$TabTargetFolder = (Join-Path $PSScriptRoot 'fixtures\native-tab-target'),
    [string]$CommandExecutable = '',
    [int]$TimeoutMilliseconds = 8000,
    [switch]$SkipDriveRoot,
    [switch]$SkipOpenNewWindow,
    [switch]$SkipOpenNewTab
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class QingTabWindowProbe
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private static string Text(IntPtr hwnd)
    {
        var value = new StringBuilder(2048);
        GetWindowText(hwnd, value, value.Capacity);
        return value.ToString();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var value = new StringBuilder(256);
        GetClassName(hwnd, value, value.Capacity);
        return value.ToString();
    }

    public static Dictionary<long, string> VisibleDialogs()
    {
        var result = new Dictionary<long, string>();
        EnumWindows((hwnd, ignored) =>
        {
            if (!IsWindowVisible(hwnd) || ClassName(hwnd) != "#32770")
                return true;

            var allText = new StringBuilder(Text(hwnd));
            EnumChildWindows(hwnd, (child, childIgnored) =>
            {
                var childText = Text(child);
                if (!String.IsNullOrWhiteSpace(childText))
                    allText.Append(" | ").Append(childText);
                return true;
            }, IntPtr.Zero);
            result[hwnd.ToInt64()] = allText.ToString();
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Get-ExplorerSnapshot {
    $shell = New-Object -ComObject Shell.Application
    $entries = @($shell.Windows()) |
        Where-Object { $_.FullName -like '*\explorer.exe' } |
        ForEach-Object {
            $path = ''
            if ([string]$_.LocationURL -like 'file:*') {
                try { $path = Get-NormalizedPath ([Uri]([string]$_.LocationURL)).LocalPath } catch { }
            }
            [pscustomobject]@{
                Hwnd = [long]$_.HWND
                Url = [string]$_.LocationURL
                Name = [string]$_.LocationName
                Path = $path
            }
        }

    [pscustomobject]@{
        Entries = @($entries)
        Hwnds = @($entries.Hwnd | Sort-Object -Unique)
    }
}

function Get-NormalizedPath([string]$Path) {
    try {
        return ([IO.Path]::GetFullPath($Path).TrimEnd('\')).ToUpperInvariant()
    }
    catch {
        return $Path.TrimEnd('\').ToUpperInvariant()
    }
}

function Close-DiagnosticTabs([string]$Path) {
    $target = Get-NormalizedPath $Path
    $shell = New-Object -ComObject Shell.Application
    foreach ($item in @($shell.Windows())) {
        try {
            if ($item.FullName -like '*\explorer.exe' -and [string]$item.LocationURL -like 'file:*') {
                $itemPath = Get-NormalizedPath ([Uri]([string]$item.LocationURL)).LocalPath
                if ($itemPath -eq $target) { $item.Quit() }
            }
        }
        catch { }
    }
}

function Invoke-ShellVerb([string]$Path, [string]$Verb) {
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute($Path, '', '', $Verb, 1)
}

function Invoke-RawRegisteredFolderOpen([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($CommandExecutable)) {
        $key = 'Registry::HKEY_CURRENT_USER\Software\Classes\Folder\shell\open\command'
        $template = (Get-Item -LiteralPath $key).GetValue('')
        if ($template -notmatch '^"(?<exe>[^"]+)"\s+(?<arguments>.+)$') {
            throw "Unexpected QingTab command registration: $template"
        }
        $executable = $Matches.exe
        $argumentTemplate = $Matches.arguments
    }
    else {
        $executable = (Resolve-Path -LiteralPath $CommandExecutable).Path
        $argumentTemplate = '--open-tab "%1"'
    }

    # ProcessStartInfo.Arguments is intentionally a raw command-line string here.
    # It reproduces the registry template's trailing-backslash-before-quote case.
    $arguments = $argumentTemplate.Replace('%1', $Path)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    [void][Diagnostics.Process]::Start($startInfo)
}

function Wait-Until([scriptblock]$Condition, [int]$Timeout) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($null -ne $value) { return $value }
        Start-Sleep -Milliseconds 100
    } while ($watch.ElapsedMilliseconds -lt $Timeout)
    return $null
}

$results = [ordered]@{}
$nativeVerbOverrides = @(
    'Registry::HKEY_CURRENT_USER\Software\Classes\Folder\shell\opennewtab',
    'Registry::HKEY_CURRENT_USER\Software\Classes\Folder\shell\opennewwindow'
) | Where-Object { Test-Path -LiteralPath $_ }
$results.NativeVerbOverrides = if ($nativeVerbOverrides.Count -eq 0) { 'PASS' } else { 'FAIL' }

# Exact user symptom: opening a drive root must not create an Explorer error dialog
# containing the malformed trailing quote URI (for example file:///E:%22).
if (!$SkipDriveRoot) {
    $dialogsBefore = [QingTabWindowProbe]::VisibleDialogs()
    $driveTarget = Get-NormalizedPath $DriveRoot
    $driveBefore = Get-ExplorerSnapshot
    $driveCountBefore = @($driveBefore.Entries | Where-Object { $_.Path -eq $driveTarget }).Count
    $driveWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-RawRegisteredFolderOpen -Path $DriveRoot
    $driveOutcome = Wait-Until -Timeout $TimeoutMilliseconds -Condition {
        $dialogs = [QingTabWindowProbe]::VisibleDialogs()
        foreach ($entry in $dialogs.GetEnumerator()) {
            if (!$dialogsBefore.ContainsKey($entry.Key) -and $entry.Value -match '^\u6587\u4ef6\u8d44\u6e90\u7ba1\u7406\u5668(?:\s*\||$)') {
                return [pscustomobject]@{ Kind = 'Dialog'; Dialog = $entry; Snapshot = $null }
            }
        }

        $snapshot = Get-ExplorerSnapshot
        $targetCount = @($snapshot.Entries | Where-Object { $_.Path -eq $driveTarget }).Count
        if ($targetCount -gt $driveCountBefore) {
            return [pscustomobject]@{ Kind = 'Opened'; Dialog = $null; Snapshot = $snapshot }
        }
        return $null
    }

    $driveOpened = $null -ne $driveOutcome -and $driveOutcome.Kind -eq 'Opened'
    $driveNewHwnds = if ($driveOpened) {
        @($driveOutcome.Snapshot.Hwnds | Where-Object { $_ -notin $driveBefore.Hwnds })
    } else { @() }
    $results.DriveRootOpen = if ($driveOpened -and $driveNewHwnds.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $results.DriveRootLatencyMs = $driveWatch.ElapsedMilliseconds
    $results.DriveRootNewHwnds = ($driveNewHwnds -join ',')
    $results.DriveRootDialog = if ($null -ne $driveOutcome -and $null -ne $driveOutcome.Dialog) {
        $driveOutcome.Dialog.Value
    } else { '' }
    if ($null -ne $driveOutcome -and $null -ne $driveOutcome.Dialog) {
        [void][QingTabWindowProbe]::PostMessage([IntPtr]$driveOutcome.Dialog.Key, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

# The native opennewwindow verb must leave behind a genuinely separate Explorer
# top-level window. QingTab may not immediately absorb it into a tab.
if (!$SkipOpenNewWindow) {
    $windowTarget = (Resolve-Path -LiteralPath $TargetFolder).Path
    Close-DiagnosticTabs -Path $windowTarget
    Start-Sleep -Milliseconds 250
    $beforeWindow = Get-ExplorerSnapshot
    Invoke-ShellVerb -Path $windowTarget -Verb 'opennewwindow'
    Start-Sleep -Milliseconds $TimeoutMilliseconds
    $afterWindow = Get-ExplorerSnapshot
    $normalizedWindowTarget = Get-NormalizedPath $windowTarget
    $newHwnds = @($afterWindow.Entries |
        Where-Object { $_.Path -eq $normalizedWindowTarget -and $_.Hwnd -notin $beforeWindow.Hwnds } |
        Select-Object -ExpandProperty Hwnd -Unique)
    $results.OpenNewWindow = if ($newHwnds.Count -gt 0) { 'PASS' } else { 'FAIL' }
    $results.NewWindowHwnds = ($newHwnds -join ',')

    # Close only the exact diagnostic windows created by this test.
    foreach ($hwnd in $newHwnds) {
        [void][QingTabWindowProbe]::PostMessage([IntPtr]$hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

# Windows' native opennewtab verb must remain reachable. An out-of-process
# ShellExecute call has no Explorer site and Windows may therefore choose a new
# top-level host; the in-Explorer context-menu topology cannot be inferred here.
if (!$SkipOpenNewTab) {
    $tabTarget = (Resolve-Path -LiteralPath $TabTargetFolder).Path
    Close-DiagnosticTabs -Path $tabTarget
    Start-Sleep -Milliseconds 250
    $beforeTab = Get-ExplorerSnapshot
    $normalizedTabTarget = Get-NormalizedPath $tabTarget
    $tabCountBefore = @($beforeTab.Entries | Where-Object { $_.Path -eq $normalizedTabTarget }).Count
    Invoke-ShellVerb -Path $tabTarget -Verb 'opennewtab'
    $tabOutcome = Wait-Until -Timeout $TimeoutMilliseconds -Condition {
        $snapshot = Get-ExplorerSnapshot
        $targetCount = @($snapshot.Entries | Where-Object { $_.Path -eq $normalizedTabTarget }).Count
        if ($targetCount -gt $tabCountBefore) { return $snapshot }
        return $null
    }
    $tabNewHwnds = if ($null -eq $tabOutcome) { @() } else {
        @($tabOutcome.Hwnds | Where-Object { $_ -notin $beforeTab.Hwnds })
    }
    $results.OpenNewTab = if ($null -ne $tabOutcome) { 'PASS' } else { 'FAIL' }
    $results.OpenNewTabTopology = if ($null -eq $tabOutcome) {
        'not-opened'
    } elseif ($tabNewHwnds.Count -eq 0) {
        'existing-window'
    } else {
        'external-call-created-host'
    }
    Close-DiagnosticTabs -Path $tabTarget
}

[pscustomobject]$results | Format-List

if ($results.Values -contains 'FAIL') {
    exit 1
}
