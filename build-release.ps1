[CmdletBinding()]
param(
    [string]$Version = '0.2.7',
    [string]$Configuration = 'Release',
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$Sign,
    [string]$CertificateThumbprint = $env:QINGTAB_SIGNING_CERTIFICATE_THUMBPRINT,
    [string]$CertificatePath = $env:QINGTAB_SIGNING_CERTIFICATE_PATH,
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
    [string]$PreSignedExecutablePath = $env:QINGTAB_PRESIGNED_EXECUTABLE_PATH,
    [switch]$SkipSourceRebuildVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedBehaviorChecks = 237
$expectedLifecycleChecks = 23
$solution = Join-Path $PSScriptRoot 'QingTab.sln'
$project = Join-Path $PSScriptRoot 'QingTab\QingTab.csproj'
$behaviorTestProject = Join-Path $PSScriptRoot 'tests\QingTab.Tests\QingTab.Tests.csproj'
$behaviorTestExecutable = Join-Path $PSScriptRoot "tests\QingTab.Tests\bin\$Configuration\net481\QingTab.Tests.exe"
$lifecycleTestProject = Join-Path $PSScriptRoot 'tests\QingTab.LifecycleTests\QingTab.LifecycleTests.csproj'
$lifecycleTestExecutable = Join-Path $PSScriptRoot "tests\QingTab.LifecycleTests\bin\$Configuration\net481\QingTab.LifecycleTests.exe"
$projectOutput = Join-Path $PSScriptRoot "QingTab\bin\$Configuration\net481"
$signingScript = Join-Path $PSScriptRoot 'scripts\Sign-QingTab.ps1'
$packageName = "QingTab-v$Version-portable"
$packageDirectory = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$sourceName = "QingTab-v$Version-source"
$sourceDirectory = Join-Path $OutputRoot $sourceName
$sourceZipPath = Join-Path $OutputRoot "$sourceName.zip"
$releaseReportPath = Join-Path $OutputRoot "QingTab-v$Version-RELEASE-REPORT.md"
$releaseManifestPath = Join-Path $OutputRoot "QingTab-v$Version-release-manifest.json"
$versionTestReport = Join-Path $PSScriptRoot "TEST-REPORT-$Version-DRAFT.md"
$testReport = if (Test-Path -LiteralPath $versionTestReport)
{
    $versionTestReport
}
else
{
    Join-Path $PSScriptRoot 'TEST-REPORT.md'
}

function Invoke-CheckedProcess
{
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0)
    {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Invoke-CheckExecutable
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedChecks,
        [Parameter(Mandatory = $true)]
        [string]$SummaryLabel
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try
    {
        # Windows PowerShell can promote a native program's stderr stream to a
        # terminating NativeCommandError when the outer release script uses
        # ErrorActionPreference=Stop. Capture the full test output first, then
        # enforce the native exit code and exact PASS summary ourselves.
        $ErrorActionPreference = 'Continue'
        $output = @(& $Executable 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0)
    {
        throw "$SummaryLabel failed (exit code $exitCode)."
    }

    $expectedSummary = "PASS: $ExpectedChecks $SummaryLabel"
    if (-not ($output -contains $expectedSummary))
    {
        throw "$SummaryLabel did not emit the exact expected summary: $expectedSummary"
    }

    return $expectedSummary
}

function Assert-ChildPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,
        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to operate outside the intended parent directory: $childFull"
    }
}

[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$declaredVersion = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ($declaredVersion -ne $Version)
{
    throw "Requested package version $Version does not match QingTab.csproj version $declaredVersion."
}

if ($Sign -and -not [string]::IsNullOrWhiteSpace($PreSignedExecutablePath))
{
    throw 'Choose either local -Sign or -PreSignedExecutablePath, never both.'
}
if (-not [string]::IsNullOrWhiteSpace($PreSignedExecutablePath))
{
    if (-not (Test-Path -LiteralPath $PreSignedExecutablePath -PathType Leaf))
    {
        throw "The SignPath-returned executable does not exist: $PreSignedExecutablePath"
    }
    $PreSignedExecutablePath = (Resolve-Path -LiteralPath $PreSignedExecutablePath).Path
}

if (-not (Test-Path -LiteralPath $OutputRoot))
{
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path

$releaseTargets = @(
    $packageDirectory,
    $zipPath,
    "$zipPath.sha256",
    $sourceDirectory,
    $sourceZipPath,
    "$sourceZipPath.sha256",
    $releaseReportPath,
    $releaseManifestPath
)
foreach ($target in $releaseTargets)
{
    if (Test-Path -LiteralPath $target)
    {
        throw "Release target already exists; choose a new output directory or move the existing artifact first: $target"
    }
}

Invoke-CheckedProcess -FailureMessage 'Release build failed' -Command {
    dotnet build $solution -c $Configuration --nologo
}

$behaviorSummary = Invoke-CheckExecutable -Executable $behaviorTestExecutable -ExpectedChecks $expectedBehaviorChecks -SummaryLabel 'QingTab behavior checks'

$lifecycleSummary = Invoke-CheckExecutable -Executable $lifecycleTestExecutable -ExpectedChecks $expectedLifecycleChecks -SummaryLabel 'QingTab lifecycle checks'

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

$packagedExecutableSource = if ([string]::IsNullOrWhiteSpace($PreSignedExecutablePath))
{
    Join-Path $projectOutput 'QingTab.exe'
}
else
{
    $PreSignedExecutablePath
}
$files = @(
    @{ Source = $packagedExecutableSource; Destination = 'QingTab.exe' },
    @{ Source = (Join-Path $projectOutput 'QingTab.exe.config'); Destination = 'QingTab.exe.config' },
    @{ Source = (Join-Path $PSScriptRoot 'README.md'); Destination = 'README.md' },
    @{ Source = (Join-Path $PSScriptRoot 'LICENSE'); Destination = 'LICENSE' },
    @{ Source = (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md'); Destination = 'THIRD-PARTY-NOTICES.md' },
    @{ Source = (Join-Path $PSScriptRoot 'PRIVACY.md'); Destination = 'PRIVACY.md' },
    @{ Source = (Join-Path $PSScriptRoot 'CODE-SIGNING.md'); Destination = 'CODE-SIGNING.md' },
    @{ Source = (Join-Path $PSScriptRoot 'RELEASE-PROCESS.md'); Destination = 'RELEASE-PROCESS.md' },
    @{ Source = (Join-Path $PSScriptRoot 'MEMORY-BENCHMARK-0.2.6-B-2026-08-13.md'); Destination = 'MEMORY-BENCHMARK-0.2.6-B-2026-08-13.md' },
    @{ Source = $testReport; Destination = 'TEST-REPORT.md' }
)

foreach ($file in $files)
{
    Copy-Item -LiteralPath $file.Source -Destination (Join-Path $packageDirectory $file.Destination) -Force
}

$uninstallScript = Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.cmd' | Select-Object -First 1
if ($null -eq $uninstallScript)
{
    throw 'The uninstall command file is missing.'
}
Copy-Item -LiteralPath $uninstallScript.FullName -Destination (Join-Path $packageDirectory $uninstallScript.Name) -Force

$packagedExecutable = Join-Path $packageDirectory 'QingTab.exe'
$signingResult = $null
$signingStatus = 'NOT REQUESTED'
$signingRequested = [bool]$Sign -or -not [string]::IsNullOrWhiteSpace($PreSignedExecutablePath)
if ($Sign)
{
    if (-not (Test-Path -LiteralPath $signingScript -PathType Leaf))
    {
        throw "Signing script is missing: $signingScript"
    }

    $signParameters = @{
        Path = @($packagedExecutable)
        TimestampUrl = $TimestampUrl
        ExpectedSignerSubject = $ExpectedSignerSubject
        ExpectedSignerThumbprint = $ExpectedSignerThumbprint
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint))
    {
        $signParameters.CertificateThumbprint = $CertificateThumbprint
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath))
    {
        $signParameters.CertificatePath = $CertificatePath
    }

    $signingResult = @(& $signingScript @signParameters)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Signing or trusted timestamp verification failed.'
    }
    $signingResult = $signingResult | Where-Object { $_ -is [psobject] -and $_.PSObject.Properties['Status'] } | Select-Object -Last 1
    if ($null -eq $signingResult -or $signingResult.Status -ne 'Valid')
    {
        throw 'The signing script did not return a valid signed-file result.'
    }
    $signingStatus = 'VALID AUTHENTICODE + TRUSTED TIMESTAMP'
}
elseif (-not [string]::IsNullOrWhiteSpace($PreSignedExecutablePath))
{
    if (-not (Test-Path -LiteralPath $signingScript -PathType Leaf))
    {
        throw "Signing verification script is missing: $signingScript"
    }

    $expectedFileVersion = "$Version.0"
    $actualFileVersion = (Get-Item -LiteralPath $packagedExecutable).VersionInfo.FileVersion
    if ($actualFileVersion -ne $expectedFileVersion)
    {
        throw "The SignPath-returned executable has FileVersion $actualFileVersion; expected $expectedFileVersion."
    }

    $verifyParameters = @{
        Path = @($packagedExecutable)
        VerifyOnly = $true
        TimestampUrl = $TimestampUrl
        ExpectedSignerSubject = $ExpectedSignerSubject
        ExpectedSignerThumbprint = $ExpectedSignerThumbprint
    }
    $signingResult = @(& $signingScript @verifyParameters)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'SignPath Authenticode or trusted timestamp verification failed.'
    }
    $signingResult = $signingResult | Where-Object { $_ -is [psobject] -and $_.PSObject.Properties['Status'] } | Select-Object -Last 1
    if ($null -eq $signingResult -or $signingResult.Status -ne 'Valid')
    {
        throw 'The SignPath verification step did not return a valid signed-file result.'
    }
    $signingStatus = 'VALID SIGNPATH AUTHENTICODE + TRUSTED TIMESTAMP'
}

$exeHash = (Get-FileHash -LiteralPath $packagedExecutable -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $packageDirectory 'SHA256SUMS.txt') -Encoding ASCII -Value "$exeHash *QingTab.exe"

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII -Value "$zipHash *$(Split-Path $zipPath -Leaf)"

New-Item -ItemType Directory -Path $sourceDirectory | Out-Null

$sourceRootFiles = @(
    'QingTab.sln',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'build-release.ps1',
    'icon-preview.png',
    'CODE-SIGNING.md',
    'PRIVACY.md',
    'RELEASE-PROCESS.md',
    'MEMORY-BENCHMARK-0.2.6-B-2026-08-13.md'
)
foreach ($sourceRootFile in $sourceRootFiles)
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $sourceRootFile) -Destination $sourceDirectory
}
Copy-Item -LiteralPath $testReport -Destination (Join-Path $sourceDirectory 'TEST-REPORT.md')
Copy-Item -LiteralPath $uninstallScript.FullName -Destination (Join-Path $sourceDirectory $uninstallScript.Name)

$sourceProjectDirectory = Join-Path $sourceDirectory 'QingTab'
New-Item -ItemType Directory -Path $sourceProjectDirectory | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'QingTab') -File |
    Copy-Item -Destination $sourceProjectDirectory
foreach ($sourceSubdirectory in @('Helpers', 'Hooks', 'Interop', 'Models', 'Tools', 'WinAPI'))
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "QingTab\$sourceSubdirectory") -Destination $sourceProjectDirectory -Recurse
}

$sourceTestsDirectory = Join-Path $sourceDirectory 'tests'
New-Item -ItemType Directory -Path $sourceTestsDirectory | Out-Null
foreach ($testRootFile in @('ArgDump.cs', 'ExplorerVerbRegression.ps1', 'Invoke-QingTabDesktopLifecycle.ps1', 'SigningPipeline.Tests.ps1'))
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "tests\$testRootFile") -Destination $sourceTestsDirectory
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tests\fixtures') -Destination $sourceTestsDirectory -Recurse
foreach ($testProjectName in @('QingTab.Tests', 'QingTab.LifecycleTests', 'ZeroFlickerHarness'))
{
    $testDestination = Join-Path $sourceTestsDirectory $testProjectName
    New-Item -ItemType Directory -Path $testDestination | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "tests\$testProjectName") -File |
        Copy-Item -Destination $testDestination
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scripts') -Destination $sourceDirectory -Recurse
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.github'))
{
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '.github') -Destination $sourceDirectory -Recurse
}

Compress-Archive -Path (Join-Path $sourceDirectory '*') -DestinationPath $sourceZipPath -CompressionLevel Optimal
$sourceZipHash = (Get-FileHash -LiteralPath $sourceZipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath "$sourceZipPath.sha256" -Encoding ASCII -Value "$sourceZipHash *$(Split-Path $sourceZipPath -Leaf)"

$sourceRebuildStatus = 'SKIPPED'
if (-not $SkipSourceRebuildVerification)
{
    # Keep the .NET Framework 4.8.1 test executable below legacy MAX_PATH
    # limits even when the repository itself lives in a deeply nested folder.
    $rebuildParent = Join-Path ([IO.Path]::GetTempPath()) 'QingTabReleaseRebuild'
    if (-not (Test-Path -LiteralPath $rebuildParent))
    {
        New-Item -ItemType Directory -Path $rebuildParent -Force | Out-Null
    }
    $rebuildParent = (Resolve-Path -LiteralPath $rebuildParent).Path
    $rebuildRoot = Join-Path $rebuildParent ([Guid]::NewGuid().ToString('N'))
    Assert-ChildPath -Parent $rebuildParent -Child $rebuildRoot
    try
    {
        New-Item -ItemType Directory -Path $rebuildRoot | Out-Null
        Expand-Archive -LiteralPath $sourceZipPath -DestinationPath $rebuildRoot
        Invoke-CheckedProcess -FailureMessage 'Independent source-package rebuild failed' -Command {
            dotnet build (Join-Path $rebuildRoot 'QingTab.sln') -c $Configuration --nologo
        }
        $rebuiltBehaviorTests = Join-Path $rebuildRoot "tests\QingTab.Tests\bin\$Configuration\net481\QingTab.Tests.exe"
        Invoke-CheckExecutable -Executable $rebuiltBehaviorTests -ExpectedChecks $expectedBehaviorChecks -SummaryLabel 'QingTab behavior checks' | Out-Null
        $rebuiltLifecycleTests = Join-Path $rebuildRoot "tests\QingTab.LifecycleTests\bin\$Configuration\net481\QingTab.LifecycleTests.exe"
        Invoke-CheckExecutable -Executable $rebuiltLifecycleTests -ExpectedChecks $expectedLifecycleChecks -SummaryLabel 'QingTab lifecycle checks' | Out-Null
        $sourceRebuildStatus = 'PASS'
    }
    finally
    {
        if (Test-Path -LiteralPath $rebuildRoot)
        {
            Assert-ChildPath -Parent $rebuildParent -Child $rebuildRoot
            Remove-Item -LiteralPath $rebuildRoot -Recurse -Force
        }
    }
}

$fileVersion = (Get-Item -LiteralPath $packagedExecutable).VersionInfo.FileVersion
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'QingTab'
    version = $Version
    fileVersion = $fileVersion
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    checks = [ordered]@{
        behavior = [ordered]@{ expected = $expectedBehaviorChecks; result = 'PASS'; summary = $behaviorSummary }
        lifecycle = [ordered]@{ expected = $expectedLifecycleChecks; result = 'PASS'; summary = $lifecycleSummary }
        sourceRebuild = $sourceRebuildStatus
    }
    signing = [ordered]@{
        requested = $signingRequested
        source = if (-not [string]::IsNullOrWhiteSpace($PreSignedExecutablePath)) { 'SignPath' } elseif ($Sign) { 'LocalCertificate' } else { $null }
        status = $signingStatus
        signerSubject = if ($null -eq $signingResult) { $null } else { $signingResult.SignerSubject }
        signerThumbprint = if ($null -eq $signingResult) { $null } else { $signingResult.SignerThumbprint }
        signerNotBefore = if ($null -eq $signingResult) { $null } else { $signingResult.SignerNotBefore }
        signerNotAfter = if ($null -eq $signingResult) { $null } else { $signingResult.SignerNotAfter }
        timestampSubject = if ($null -eq $signingResult) { $null } else { $signingResult.TimestampSubject }
        timestampCertificateThumbprint = if ($null -eq $signingResult) { $null } else { $signingResult.TimestampCertificateThumbprint }
        timestampCertificateNotBefore = if ($null -eq $signingResult) { $null } else { $signingResult.TimestampNotBefore }
        timestampCertificateNotAfter = if ($null -eq $signingResult) { $null } else { $signingResult.TimestampNotAfter }
        timestampUrl = if ($Sign) { $TimestampUrl } else { $null }
    }
    artifacts = @(
        [ordered]@{ name = 'QingTab.exe'; path = $packagedExecutable; bytes = (Get-Item -LiteralPath $packagedExecutable).Length; sha256 = $exeHash },
        [ordered]@{ name = (Split-Path $zipPath -Leaf); path = $zipPath; bytes = (Get-Item -LiteralPath $zipPath).Length; sha256 = $zipHash },
        [ordered]@{ name = (Split-Path $sourceZipPath -Leaf); path = $sourceZipPath; bytes = (Get-Item -LiteralPath $sourceZipPath).Length; sha256 = $sourceZipHash }
    )
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestPath -Encoding UTF8

$signerText = if ($null -eq $signingResult) { '未请求签名（开发验证包）' } else { $signingResult.SignerSubject }
$timestampText = if ($null -eq $signingResult) { '未请求' } else { $signingResult.TimestampSubject }
$report = @"
# QingTab v$Version Release 报告

生成时间（UTC）：$([DateTimeOffset]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))

## 结果

- Release 构建：PASS
- 行为检查：PASS（$expectedBehaviorChecks 项）
- 生命周期检查：PASS（$expectedLifecycleChecks 项）
- 源码 ZIP 独立重建：$sourceRebuildStatus
- 正式签名状态：$signingStatus
- 签名者：$signerText
- 时间戳证书：$timestampText

## 文件与 SHA-256

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| QingTab.exe | $((Get-Item -LiteralPath $packagedExecutable).Length) | ``$exeHash`` |
| $(Split-Path $zipPath -Leaf) | $((Get-Item -LiteralPath $zipPath).Length) | ``$zipHash`` |
| $(Split-Path $sourceZipPath -Leaf) | $((Get-Item -LiteralPath $sourceZipPath).Length) | ``$sourceZipHash`` |

## 生命周期覆盖

- 退出前恢复 Windows 文件夹打开方式；失败时禁止退出。
- 重复退出和重复释放保持幂等，只允许一次资源释放。
- 注销后的新会话使用隔离的互斥体、事件和 IPC 名称。
- Explorer 断开后旧请求代际立即失效；最后一个旧请求只触发一次清理；重新连接只接收新代际请求。
- 真实 Explorer 重启与注销脚本带明确的桌面干扰开关，只允许在保存工作后的专用测试会话中执行。

机器可读明细：$(Split-Path $releaseManifestPath -Leaf)
"@
Set-Content -LiteralPath $releaseReportPath -Encoding UTF8 -Value $report

[pscustomobject]@{
    PortablePackage = $zipPath
    PortablePackageSha256 = $zipHash
    ExecutableSha256 = $exeHash
    SourcePackage = $sourceZipPath
    SourcePackageSha256 = $sourceZipHash
    ReleaseReport = $releaseReportPath
    ReleaseManifest = $releaseManifestPath
    BehaviorChecks = $expectedBehaviorChecks
    LifecycleChecks = $expectedLifecycleChecks
    SourceRebuild = $sourceRebuildStatus
    Signing = $signingStatus
}
