[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$signingScript = Join-Path $repositoryRoot 'scripts\Sign-QingTab.ps1'
$releaseScript = Join-Path $repositoryRoot 'build-release.ps1'
$releaseWorkflow = Join-Path $repositoryRoot '.github\workflows\release.yml'
$checks = 0

function Assert-True
{
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $script:checks++
    if (-not $Condition)
    {
        throw "FAIL: $Message"
    }
}

function Read-Utf8File
{
    param([Parameter(Mandatory = $true)][string]$Path)
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8
}

$signingText = Read-Utf8File -Path $signingScript
$releaseText = Read-Utf8File -Path $releaseScript
$workflowText = Read-Utf8File -Path $releaseWorkflow

Assert-True ($signingText -match "'http://timestamp\.digicert\.com'") 'The default must remain DigiCert''s documented RFC 3161 endpoint.'
Assert-True ($signingText -match '\[Uri\]::TryCreate') 'The timestamp URL must be parsed and validated before SignTool is called.'
Assert-True ($signingText -match "Scheme -notin") 'The signing script must reject a non-HTTP(S) timestamp endpoint.'
Assert-True ($signingText -match "EnhancedKeyUsageList") 'A certificate-store signer must be checked for the Code Signing EKU.'
Assert-True ($signingText -match "1\.3\.6\.1\.5\.5\.7\.3\.3") 'The Code Signing EKU OID must be enforced.'
Assert-True ($signingText -match "'verify', '/pa', '/all', '/tw', '/v'") 'Authenticode and timestamp trust must be verified with SignTool policy checks.'
Assert-True ($signingText -match "TimestampCertificateThumbprint") 'The timestamp certificate identity must be returned for the release manifest.'
Assert-True ($signingText -match "SignerNotAfter") 'The signer validity window must be recorded for release auditing.'
Assert-True ($releaseText -match "signerNotAfter") 'The release manifest must include signer certificate expiry.'
Assert-True ($releaseText -match "timestampCertificateThumbprint") 'The release manifest must include the timestamp certificate thumbprint.'
Assert-True ($workflowText -match '(?m)^\s*environment:\s*code-signing\s*$') 'The signing job must use the protected code-signing GitHub environment.'
Assert-True ($workflowText -match 'QINGTAB_EXPECTED_SIGNER_THUMBPRINT') 'The release workflow must pin the expected signer thumbprint.'
Assert-True ($workflowText -match 'actions/checkout@[0-9a-f]{40}') 'Release checkout must be pinned to a full commit SHA.'
Assert-True ($workflowText -match 'actions/setup-dotnet@[0-9a-f]{40}') 'Release .NET setup must be pinned to a full commit SHA.'
Assert-True ($workflowText -match 'actions/upload-artifact@[0-9a-f]{40}') 'Release artifact upload must be pinned to a full commit SHA.'

Write-Output "PASS: $checks QingTab signing pipeline checks"
