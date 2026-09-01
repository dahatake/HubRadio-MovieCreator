#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or
    $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testProject = Join-Path $repoRoot `
    'tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj'
$testExecutable = Join-Path $repoRoot (
    'tests\HubRadio.App.Tests\bin\x64\{0}\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.Tests.exe' -f
        $Configuration)
$webRoot = Join-Path $repoRoot 'src\HubRadio.Web'
$workRoot = Join-Path $repoRoot (
    'benchmarks\work\q005-traceability-{0}' -f
        [Guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $workRoot 'acceptance-core.json'
$dotnet = (Get-Command -Name 'dotnet.exe' -ErrorAction Stop).Source
$git = (Get-Command -Name 'git.exe' -ErrorAction Stop).Source
$npm = (Get-Command -Name 'npm.cmd' -ErrorAction Stop).Source
$previousResultPath = $env:HUBRADIO_Q005_RESULT_PATH

function Assert-Condition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [ValidateRange(1, 1800)]
        [int]$TimeoutSeconds = 600
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw 'A Q-005 child process could not be started.'
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'A Q-005 child process exceeded its bounded timeout.'
        }

        [void][Threading.Tasks.Task]::WhenAll(
            [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
        ).WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(10000)
        }
        $process.Dispose()
    }
}

function Invoke-RequiredProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [ValidateRange(1, 1800)]
        [int]$TimeoutSeconds = 600
    )

    $result = Invoke-CapturedProcess -FileName $FileName `
        -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory `
        -TimeoutSeconds $TimeoutSeconds
    if ($result.exitCode -ne 0) {
        throw "$Description failed with exit code $($result.exitCode)."
    }
    return $result
}

function Get-SourceFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $fullPath = Join-Path $repoRoot $RelativePath
    Assert-Condition -Condition (
        Test-Path -LiteralPath $fullPath -PathType Leaf
    ) -Message 'A Q-005 fingerprint source is missing.'
    $digest = [Security.Cryptography.SHA256]::HashData(
        [IO.File]::ReadAllBytes($fullPath))
    return [pscustomobject][ordered]@{
        relativePath = $RelativePath.Replace('\', '/')
        sha256 = [Convert]::ToHexString($digest).ToLowerInvariant()
    }
}

$sourceFiles = @(
    'tests\Acceptance\AC005_Traceability.cs'
    'tests\Acceptance\Invoke-Q005TraceabilityAcceptance.ps1'
    'tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj'
    'src\HubRadio.App\Analysis\SuggestionService.cs'
    'src\HubRadio.App\Delivery\DeliveryService.cs'
    'src\HubRadio.App\Audit\ProvenanceAuditService.cs'
    'src\HubRadio.App\Transcripts\TranscriptEditor.cs'
    'src\HubRadio.Core\Analysis\ModelRun.cs'
    'src\HubRadio.Core\Analysis\Suggestion.cs'
    'src\HubRadio.Core\Projects\Asset.cs'
    'src\HubRadio.Core\Projects\RightsRecord.cs'
    'src\HubRadio.Core\Provenance\OriginRef.cs'
    'src\HubRadio.Core\Provenance\SourceSpan.cs'
    'src\HubRadio.Core\Provenance\TimelineMap.cs'
    'src\HubRadio.Core\Provenance\TimelineMapBuilder.cs'
    'src\HubRadio.Core\Rendering\RenderGraphCanonicalEvidence.cs'
    'src\HubRadio.Core\Rendering\RenderGraphValidator.cs'
)

$initialHeadCommit = (Invoke-RequiredProcess `
    -Description 'Initial repository revision inspection' `
    -FileName $git -ArgumentList @('-C', $repoRoot, 'rev-parse', 'HEAD') `
    -WorkingDirectory $repoRoot -TimeoutSeconds 30).stdout.Trim()
Assert-Condition -Condition (
    $initialHeadCommit -cmatch '\A[0-9a-f]{40}\z'
) -Message 'The initial Q-005 repository revision is invalid.'
$initialFingerprints = @(
    foreach ($relativePath in $sourceFiles) {
        Get-SourceFingerprint -RelativePath $relativePath
    }
)

try {
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $env:HUBRADIO_Q005_RESULT_PATH = $resultPath

    $null = Invoke-RequiredProcess -Description 'Web dependency restore' `
        -FileName $npm -ArgumentList @('ci') -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web TypeScript verification' `
        -FileName $npm -ArgumentList @('run', 'typecheck') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web production build' `
        -FileName $npm -ArgumentList @('run', 'build') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Q-005 test restore' `
        -FileName $dotnet -WorkingDirectory $repoRoot `
        -ArgumentList @(
            'restore', $testProject,
            '-p:Platform=x64',
            '-p:TreatWarningsAsErrors=true',
            '--verbosity', 'minimal'
        )
    $null = Invoke-RequiredProcess -Description 'Q-005 warnings-as-errors build' `
        -FileName $dotnet -WorkingDirectory $repoRoot -TimeoutSeconds 900 `
        -ArgumentList @(
            'build', $testProject,
            '--configuration', $Configuration,
            '--no-restore',
            '-p:Platform=x64',
            '-p:TreatWarningsAsErrors=true',
            '-warnaserror',
            '--verbosity', 'minimal'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $testExecutable -PathType Leaf
    ) -Message 'The built Q-005 test executable is missing.'
    $null = Invoke-RequiredProcess -Description 'Q-005 focused acceptance test' `
        -FileName $testExecutable -WorkingDirectory $repoRoot `
        -ArgumentList @(
            '--filter',
            'FullyQualifiedName~HubRadio.App.Tests.AC005_Traceability',
            '--minimum-expected-tests', '1',
            '--progress', 'off'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $resultPath -PathType Leaf
    ) -Message 'The Q-005 focused test did not produce acceptance evidence.'

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $coreJson = $strictUtf8.GetString([IO.File]::ReadAllBytes($resultPath))
    try {
        $core = $coreJson | ConvertFrom-Json
    }
    catch {
        throw 'The Q-005 focused test produced invalid JSON evidence.'
    }

    $checks = [ordered]@{
        schemaVersion = [string]$core.schemaVersion -ceq '1.0.0'
        requirement = [string]$core.requirement -ceq 'Q-005'
        sourceBackedEntryCount = [int]$core.timelineCoverage.entryCount -eq 2
        contiguousFromZero = [bool]$core.timelineCoverage.contiguousFromZero
        intervalPartitionProof = [bool]$core.timelineCoverage.intervalPartitionProof
        representativeTimes = [int]$core.timelineCoverage.representativeOutputTimeCount -eq 8
        roundTrips = [int]$core.timelineCoverage.reverseRoundTripCount -eq 8
        outputEndRejected = [bool]$core.timelineCoverage.outputEndRejected
        outputTimesResolved = [int]$core.timelineCoverage.unresolvedOutputTimeCount -eq 0
        originsResolved = [int]$core.reverseTraceability.unresolvedOriginCount -eq 0
        usedRanges = @($core.reverseTraceability.usedRangeInventory).Count -eq 2
        machineSubtitle = [int]$core.subtitleProvenance.machineGeneratedSceneCount -eq 1
        humanSubtitle = [int]$core.subtitleProvenance.humanModifiedSceneCount -eq 1
        revisionDigests = [bool]$core.subtitleProvenance.sourceRevisionDigestMatches -and
            [bool]$core.subtitleProvenance.childRevisionDigestMatches -and
            [bool]$core.subtitleProvenance.editRecordDigestMatches
        modelRunBound = [bool]$core.decisionEvidence.syntheticModelRunContractBound
        humanDecisionBound = [bool]$core.decisionEvidence.humanDecisionBound
        rightsResolved = [int]$core.rights.unresolvedRightsCount -eq 0
        rightsApproved = [int]$core.rights.approvedUsedAssetCount -eq 1 -and
            [bool]$core.rights.externalPublicationAllowed
        deliveryDigests = [bool]$core.delivery.compositionDigestMatches -and
            [bool]$core.delivery.timelineDigestMatches -and
            [bool]$core.delivery.usedSourceSpanDigestStableAcrossInputOrder -and
            [bool]$core.delivery.approvalEvidenceDigestStableAcrossInputOrder -and
            [bool]$core.delivery.deliveryEvidenceDigestStableAcrossInputOrder
        auditRecords = [int]$core.audit.recordCount -eq 3 -and
            [int]$core.audit.canonicalRecordCount -eq 3 -and
            [int]$core.audit.contentLeakCount -eq 0
        negativeControl = [bool]$core.negativeControl.detected -and
            [bool]$core.negativeControl.renderGraphIssueDetected -and
            [string]$core.negativeControl.expectedDeliveryFailure -ceq
                'ProvenanceInvalid' -and
            [int]$core.negativeControl.dependencySideEffectCount -eq 0
        noRealMediaClaim = -not [bool]$core.scope.actualMediaUsed
        noRealModelClaim = -not [bool]$core.scope.actualModelInferenceUsed
        noHumanAcceptanceClaim = -not [bool]$core.scope.humanAcceptanceClaimed
        noEditorialRecordClaim = -not
            [bool]$core.originResolution.editorialRecordEvidenceClaimed
        passed = [bool]$core.passed
    }
    $failedChecks = @(
        $checks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedChecks.Count -ne 0) {
        throw "Q-005 evidence validation failed: $($failedChecks.Key -join ', ')."
    }

    $dotnetVersion = (Invoke-RequiredProcess `
        -Description '.NET SDK version inspection' `
        -FileName $dotnet -ArgumentList @('--version') `
        -WorkingDirectory $repoRoot -TimeoutSeconds 30).stdout.Trim()
    $fingerprints = @(
        foreach ($relativePath in $sourceFiles) {
            Get-SourceFingerprint -RelativePath $relativePath
        }
    )
    $finalHeadCommit = (Invoke-RequiredProcess `
        -Description 'Final repository revision inspection' `
        -FileName $git -ArgumentList @('-C', $repoRoot, 'rev-parse', 'HEAD') `
        -WorkingDirectory $repoRoot -TimeoutSeconds 30).stdout.Trim()
    Assert-Condition -Condition (
        $finalHeadCommit -cmatch '\A[0-9a-f]{40}\z'
    ) -Message 'The final Q-005 repository revision is invalid.'
    $sourceFilesUnchangedDuringRun =
        $initialHeadCommit -ceq $finalHeadCommit -and
        $initialFingerprints.Count -eq $fingerprints.Count
    if ($sourceFilesUnchangedDuringRun) {
        for ($index = 0; $index -lt $fingerprints.Count; $index++) {
            if ($initialFingerprints[$index].relativePath -cne
                    $fingerprints[$index].relativePath -or
                $initialFingerprints[$index].sha256 -cne
                    $fingerprints[$index].sha256) {
                $sourceFilesUnchangedDuringRun = $false
                break
            }
        }
    }
    Assert-Condition -Condition $sourceFilesUnchangedDuringRun `
        -Message 'The Q-005 evidence sources changed during the acceptance run.'
    $result = [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        requirement = 'Q-005'
        measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = [pscustomobject][ordered]@{
            operatingSystem = 'Windows'
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            powershellEdition = $PSVersionTable.PSEdition
            powershellVersion = $PSVersionTable.PSVersion.ToString()
            dotnetSdkVersion = $dotnetVersion
        }
        execution = [pscustomobject][ordered]@{
            webPrerequisitesBuilt = $true
            warningsAsErrorsBuildPassed = $true
            focusedTestMinimum = 1
            focusedTestPassed = $true
            sourceFilesUnchangedDuringRun =
                $sourceFilesUnchangedDuringRun
        }
        evidence = $core
        repositoryHeadCommit = $finalHeadCommit
        sourceFingerprints = $fingerprints
        passed = $true
    }
    $json = $result | ConvertTo-Json -Depth 20 -Compress
    $privacyChecks = [ordered]@{
        windowsAbsolutePathAbsent = $json -notmatch '(?i)[A-Z]:[\\/]'
        uncPathAbsent = $json -notmatch '\\\\'
        wslWindowsPathAbsent = $json -notmatch '(?i)/mnt/[a-z]/'
        sidAbsent = $json -notmatch 'S-[0-9]+(?:-[0-9]+){2,}'
        userNameAbsent = -not $json.Contains(
            [Environment]::UserName,
            [StringComparison]::OrdinalIgnoreCase)
        transcriptContentAbsent =
            -not $json.Contains('synthetic-machine-caption', [StringComparison]::Ordinal) -and
            -not $json.Contains('synthetic-review-candidate', [StringComparison]::Ordinal) -and
            -not $json.Contains('synthetic-reviewed-caption', [StringComparison]::Ordinal)
        storageReferenceAbsent =
            -not $json.Contains('projects/q005/renders', [StringComparison]::Ordinal) -and
            -not $json.Contains('projects/q005/deliveries', [StringComparison]::Ordinal)
        credentialAssignmentAbsent =
            $json -notmatch '(?i)(password|api[_-]?key|access[_-]?token|client[_-]?secret|connection[_-]?string|shared[_-]?access[_-]?signature)\s*[:=]'
    }
    $failedPrivacyChecks = @(
        $privacyChecks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedPrivacyChecks.Count -ne 0) {
        throw "Q-005 result privacy validation failed: $($failedPrivacyChecks.Key -join ', ')."
    }

    $json
}
finally {
    if ($null -eq $previousResultPath) {
        Remove-Item Env:HUBRADIO_Q005_RESULT_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:HUBRADIO_Q005_RESULT_PATH = $previousResultPath
    }
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}