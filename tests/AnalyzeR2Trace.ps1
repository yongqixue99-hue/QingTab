param(
    [Parameter(Mandatory = $true)]
    [string] $TracePath,

    [int] $MaximumDefaultPageExposureMilliseconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TracePath -PathType Leaf)) {
    throw "Trace file does not exist: $TracePath"
}

$requests = @(
    Get-Content -LiteralPath $TracePath |
        Where-Object { $_ -match 'responsive-native' }
)

if ($requests.Count -eq 0) {
    Write-Output 'INCONCLUSIVE: no responsive-native requests were captured.'
    exit 2
}

$failures = [System.Collections.Generic.List[string]]::new()
$index = 0

foreach ($request in $requests) {
    $index++
    $markers = @{}

    foreach ($segment in ($request -split '\s*\|\s*')) {
        if ($segment -match '(?:\[DEBUG-025\]\s*)?(?<milliseconds>\d+):(?<name>.+)$') {
            $markers[$Matches.name] = [int] $Matches.milliseconds
        }
    }

    if (-not $markers.ContainsKey('active-tab-command-sent')) {
        $failures.Add("request ${index}: new-tab command was not sent")
        continue
    }

    if (-not $markers.ContainsKey('active-native-claimed')) {
        $failures.Add("request ${index}: new tab was never claimed; the E-drive request appears unresponsive")
        continue
    }

    if (-not $markers.ContainsKey('active-navigate-Accepted')) {
        $failures.Add("request ${index}: target navigation was never accepted; the E-drive request did not complete")
        continue
    }

    $exposure = $markers['active-navigate-Accepted'] - $markers['active-tab-command-sent']
    if ($exposure -gt $MaximumDefaultPageExposureMilliseconds) {
        $failures.Add(
            "request ${index}: Explorer default-page exposure was ${exposure}ms " +
            "(limit ${MaximumDefaultPageExposureMilliseconds}ms); This PC can be visibly shown before E:\"
        )
    }
}

if ($failures.Count -gt 0) {
    Write-Output "RED: $($failures.Count) user-visible regression(s) across $($requests.Count) request(s)."
    $failures | ForEach-Object { Write-Output " - $_" }
    exit 1
}

Write-Output "GREEN: $($requests.Count) request(s) completed without a visible default-page window."
exit 0
