[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [string]$CertificateThumbprint = $env:QINGTAB_SIGNING_CERTIFICATE_THUMBPRINT,
    [string]$CertificatePath = $env:QINGTAB_SIGNING_CERTIFICATE_PATH,
    [string]$CertificatePasswordEnvironmentVariable = 'QINGTAB_SIGNING_CERTIFICATE_PASSWORD',
    [string]$TimestampUrl = $(
        if ([string]::IsNullOrWhiteSpace($env:QINGTAB_TIMESTAMP_URL))
        {
            'http://timestamp.digicert.com'
        }
        else
        {
            $env:QINGTAB_TIMESTAMP_URL
        }
    ),
    [string]$ExpectedSignerSubject = $env:QINGTAB_EXPECTED_SIGNER_SUBJECT,
    [string]$ExpectedSignerThumbprint = $env:QINGTAB_EXPECTED_SIGNER_THUMBPRINT,
    [string]$SignToolPath = $env:SIGNTOOL_PATH,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$codeSigningEkuOid = '1.3.6.1.5.5.7.3.3'
$timestampingEkuOid = '1.3.6.1.5.5.7.3.8'

function Normalize-Thumbprint
{
    param([AllowNull()][string]$Thumbprint)

    if ([string]::IsNullOrWhiteSpace($Thumbprint))
    {
        return $null
    }

    $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$')
    {
        throw "A SHA-1 certificate thumbprint must contain exactly 40 hexadecimal characters: $Thumbprint"
    }

    return $normalized
}

function Test-CertificateEku
{
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)]
        [string]$RequiredOid
    )

    return @($Certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $RequiredOid }).Count -gt 0
}

function Assert-TimestampUrl
{
    param([Parameter(Mandatory = $true)][string]$Url)

    $parsed = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -notin @([Uri]::UriSchemeHttp, [Uri]::UriSchemeHttps))
    {
        throw "The RFC 3161 timestamp URL must be an absolute HTTP or HTTPS URL: $Url"
    }
}

function Resolve-SignTool
{
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath))
    {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf))
        {
            throw "SignTool does not exist: $RequestedPath"
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

    $candidate = $sdkRoots |
        ForEach-Object {
            Get-ChildItem -LiteralPath $_ -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue
        } |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $candidate)
    {
        throw 'SignTool was not found. Install the Windows SDK or set SIGNTOOL_PATH.'
    }

    return $candidate.FullName
}

function Resolve-CodeSigningCertificate
{
    param([string]$Thumbprint)

    $normalized = Normalize-Thumbprint -Thumbprint $Thumbprint

    foreach ($location in @('CurrentUser', 'LocalMachine'))
    {
        $certificate = Get-ChildItem -LiteralPath "Cert:\$location\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $normalized } |
            Select-Object -First 1
        if ($null -eq $certificate)
        {
            continue
        }

        if (-not $certificate.HasPrivateKey)
        {
            throw "The certificate $normalized has no private key."
        }
        if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date))
        {
            throw "The certificate $normalized is not currently valid."
        }
        if (-not (Test-CertificateEku -Certificate $certificate -RequiredOid $codeSigningEkuOid))
        {
            throw "The certificate $normalized is not valid for Code Signing (EKU $codeSigningEkuOid)."
        }

        return [pscustomobject]@{
            Certificate = $certificate
            StoreLocation = $location
            Thumbprint = $normalized
        }
    }

    throw "No code-signing certificate with private key was found for thumbprint $normalized."
}

function Invoke-SignTool
{
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$FailureMessage (SignTool exit code $LASTEXITCODE)."
    }
}

$resolvedPaths = foreach ($item in $Path)
{
    if (-not (Test-Path -LiteralPath $item -PathType Leaf))
    {
        throw "File to sign does not exist: $item"
    }
    (Resolve-Path -LiteralPath $item).Path
}

$expectedThumbprint = Normalize-Thumbprint -Thumbprint $ExpectedSignerThumbprint
Assert-TimestampUrl -Url $TimestampUrl
$signTool = Resolve-SignTool -RequestedPath $SignToolPath

if (-not $VerifyOnly)
{
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
        -not [string]::IsNullOrWhiteSpace($CertificatePath))
    {
        throw 'Specify either a certificate thumbprint or a PFX path, not both.'
    }

    $certificateArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint))
    {
        $certificateInfo = Resolve-CodeSigningCertificate -Thumbprint $CertificateThumbprint
        $certificateArguments += @('/sha1', $certificateInfo.Thumbprint, '/s', 'My')
        if ($certificateInfo.StoreLocation -eq 'LocalMachine')
        {
            $certificateArguments += '/sm'
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CertificatePath))
    {
        if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf))
        {
            throw "PFX file does not exist: $CertificatePath"
        }
        $certificateArguments += @('/f', (Resolve-Path -LiteralPath $CertificatePath).Path)
        $password = [Environment]::GetEnvironmentVariable($CertificatePasswordEnvironmentVariable)
        if (-not [string]::IsNullOrEmpty($password))
        {
            $certificateArguments += @('/p', $password)
        }
    }
    else
    {
        throw 'Formal signing requires QINGTAB_SIGNING_CERTIFICATE_THUMBPRINT or QINGTAB_SIGNING_CERTIFICATE_PATH.'
    }

    foreach ($resolvedPath in $resolvedPaths)
    {
        $signArguments = @(
            'sign',
            '/fd', 'SHA256',
            '/tr', $TimestampUrl,
            '/td', 'SHA256',
            '/v'
        ) + $certificateArguments + @($resolvedPath)
        Invoke-SignTool -Executable $signTool -Arguments $signArguments -FailureMessage "Signing failed for $resolvedPath"
    }
}

$results = foreach ($resolvedPath in $resolvedPaths)
{
    $verifyArguments = @('verify', '/pa', '/all', '/tw', '/v', $resolvedPath)
    Invoke-SignTool -Executable $signTool -Arguments $verifyArguments -FailureMessage "Signature verification failed for $resolvedPath"

    $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
    {
        throw "Authenticode status is $($signature.Status), not Valid: $resolvedPath"
    }
    if ($null -eq $signature.TimeStamperCertificate)
    {
        throw "The file has no trusted timestamp certificate: $resolvedPath"
    }
    if (-not (Test-CertificateEku -Certificate $signature.SignerCertificate -RequiredOid $codeSigningEkuOid))
    {
        throw "The Authenticode signer certificate is not valid for Code Signing: $resolvedPath"
    }
    if (-not (Test-CertificateEku -Certificate $signature.TimeStamperCertificate -RequiredOid $timestampingEkuOid))
    {
        throw "The timestamp certificate is not valid for Time Stamping: $resolvedPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -and
        ($null -eq $signature.SignerCertificate -or
         $signature.SignerCertificate.Subject -notlike "*$ExpectedSignerSubject*"))
    {
        throw "The signer subject does not contain '$ExpectedSignerSubject': $resolvedPath"
    }
    if ($null -ne $expectedThumbprint -and
        -not [string]::Equals($signature.SignerCertificate.Thumbprint, $expectedThumbprint, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The signer thumbprint does not match the pinned release certificate: $resolvedPath"
    }

    [pscustomobject]@{
        Path = $resolvedPath
        Status = $signature.Status.ToString()
        SignerSubject = $signature.SignerCertificate.Subject
        SignerThumbprint = $signature.SignerCertificate.Thumbprint
        SignerNotBefore = $signature.SignerCertificate.NotBefore.ToUniversalTime().ToString('o')
        SignerNotAfter = $signature.SignerCertificate.NotAfter.ToUniversalTime().ToString('o')
        TimestampSubject = $signature.TimeStamperCertificate.Subject
        TimestampCertificateThumbprint = $signature.TimeStamperCertificate.Thumbprint
        TimestampNotBefore = $signature.TimeStamperCertificate.NotBefore.ToUniversalTime().ToString('o')
        TimestampNotAfter = $signature.TimeStamperCertificate.NotAfter.ToUniversalTime().ToString('o')
        Sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash
    }
}

$results
