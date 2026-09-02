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
$testProject = Join-Path $repoRoot 'tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj'
$testExecutable = Join-Path $repoRoot (
    'tests\HubRadio.App.Tests\bin\x64\{0}\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.Tests.exe' -f
        $Configuration)
$webRoot = Join-Path $repoRoot 'src\HubRadio.Web'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'HubRadio.Q001.Runner.{0}' -f
        [Guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $workRoot 'acceptance-core.json'
$repeatResultPath = Join-Path $workRoot 'acceptance-core-repeat.json'
$dotnet = (Get-Command -Name 'dotnet.exe' -ErrorAction Stop).Source
$git = (Get-Command -Name 'git.exe' -ErrorAction Stop).Source
$npm = (Get-Command -Name 'npm.cmd' -ErrorAction Stop).Source
$previousResultPath = $env:HUBRADIO_Q001_RESULT_PATH

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
            throw 'A Q-001 child process could not be started.'
        }

        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'A Q-001 child process exceeded its bounded timeout.'
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
    ) -Message 'A Q-001 fingerprint source is missing.'
    $digest = [Security.Cryptography.SHA256]::HashData(
        [IO.File]::ReadAllBytes($fullPath))
    return [pscustomobject][ordered]@{
        relativePath = $RelativePath.Replace('\', '/')
        sha256 = [Convert]::ToHexString($digest).ToLowerInvariant()
    }
}

function Assert-NoDuplicateJsonProperties {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement]$Element,

        [Parameter(Mandatory)]
        [string]$Path
    )

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $seen.Add($property.Name)) {
                    throw "Duplicate JSON property detected at ${Path}: '$($property.Name)'."
                }

                Assert-NoDuplicateJsonProperties -Element $property.Value -Path "$Path.$($property.Name)"
            }
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            $index = 0
            foreach ($item in $Element.EnumerateArray()) {
                Assert-NoDuplicateJsonProperties -Element $item -Path "$Path[$index]"
                $index++
            }
        }
        default {
        }
    }
}

$sourceFiles = @(
    'tests\Acceptance\AC001_LongForm.cs'
    'tests\Acceptance\Invoke-Q001LongFormAcceptance.ps1'
    'tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj'
    'src\HubRadio.App\Analysis\SuggestionService.cs'
    'src\HubRadio.App\Compositions\LongFormComposer.cs'
    'src\HubRadio.App\Analysis\ContextDeviationDetector.cs'
    'src\HubRadio.App\Analysis\TopicBoundaryService.cs'
    'src\HubRadio.App\Analysis\TopicMetadataService.cs'
    'src\HubRadio.App\Delivery\DeliveryService.cs'
    'src\HubRadio.App\Subtitles\SubtitleService.cs'
    'src\HubRadio.App\Subtitles\SubtitleDisplayMapper.cs'
    'src\HubRadio.App\Transcripts\TranscriptEditor.cs'
    'src\HubRadio.Core\Rendering\RenderGraph.cs'
    'src\HubRadio.Core\Rendering\RenderGraphValidator.cs'
    'src\HubRadio.Core\Subtitles\SubtitleSegmenter.cs'
    'src\HubRadio.Core\Subtitles\SubtitleLayoutEngine.cs'
    'src\HubRadio.Core\Provenance\TimelineMap.cs'
    'src\HubRadio.Core\Provenance\TimelineMapBuilder.cs'
    'src\HubRadio.Infrastructure\Subtitles\WebVttSubtitleWriter.cs'
)

$initialHeadCommit = (Invoke-RequiredProcess `
    -Description 'Initial repository revision inspection' `
    -FileName $git -ArgumentList @('-C', $repoRoot, 'rev-parse', 'HEAD') `
    -WorkingDirectory $repoRoot -TimeoutSeconds 30).stdout.Trim()
Assert-Condition -Condition (
    $initialHeadCommit -cmatch '\A[0-9a-f]{40}\z'
) -Message 'The initial Q-001 repository revision is invalid.'
$initialFingerprints = @(
    foreach ($relativePath in $sourceFiles) {
        Get-SourceFingerprint -RelativePath $relativePath
    }
)

try {
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $env:HUBRADIO_Q001_RESULT_PATH = $resultPath

    $null = Invoke-RequiredProcess -Description 'Web dependency restore' `
        -FileName $npm -ArgumentList @('ci') -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web TypeScript verification' `
        -FileName $npm -ArgumentList @('run', 'typecheck') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web production build' `
        -FileName $npm -ArgumentList @('run', 'build') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Q-001 test restore' `
        -FileName $dotnet -WorkingDirectory $repoRoot `
        -ArgumentList @(
            'restore', $testProject,
            '-p:Platform=x64',
            '-p:TreatWarningsAsErrors=true',
            '--verbosity', 'minimal'
        )
    $null = Invoke-RequiredProcess -Description 'Q-001 warnings-as-errors build' `
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
    ) -Message 'The built Q-001 test executable is missing.'
    $null = Invoke-RequiredProcess -Description 'Q-001 focused acceptance test' `
        -FileName $testExecutable -WorkingDirectory $repoRoot `
        -ArgumentList @(
            '--filter',
            'FullyQualifiedName~HubRadio.App.Tests.AC001_LongForm',
            '--minimum-expected-tests', '1',
            '--progress', 'off'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $resultPath -PathType Leaf
    ) -Message 'The Q-001 focused test did not produce acceptance evidence.'

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $coreBytes = [IO.File]::ReadAllBytes($resultPath)
    Assert-Condition -Condition ($coreBytes.Length -gt 0) `
        -Message 'The Q-001 focused test produced empty evidence.'
    $hasUtf8Bom = $coreBytes.Length -ge 3 -and
        $coreBytes[0] -eq 0xEF -and
        $coreBytes[1] -eq 0xBB -and
        $coreBytes[2] -eq 0xBF
    Assert-Condition -Condition (-not $hasUtf8Bom) `
        -Message 'The Q-001 focused test produced UTF-8 BOM evidence.'

    $env:HUBRADIO_Q001_RESULT_PATH = $repeatResultPath
    $null = Invoke-RequiredProcess `
        -Description 'Q-001 deterministic repeat acceptance test' `
        -FileName $testExecutable -WorkingDirectory $repoRoot `
        -ArgumentList @(
            '--filter',
            'FullyQualifiedName~HubRadio.App.Tests.AC001_LongForm',
            '--minimum-expected-tests', '1',
            '--progress', 'off'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $repeatResultPath -PathType Leaf
    ) -Message 'The Q-001 repeat test did not produce acceptance evidence.'
    $repeatBytes = [IO.File]::ReadAllBytes($repeatResultPath)
    $deterministicCoreEvidence =
        $coreBytes.Length -eq $repeatBytes.Length -and
        [Linq.Enumerable]::SequenceEqual[byte]($coreBytes, $repeatBytes)
    Assert-Condition -Condition $deterministicCoreEvidence `
        -Message 'The Q-001 canonical core evidence is not deterministic.'

    $coreJson = $strictUtf8.GetString($coreBytes)
    $jsonDocumentOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $jsonDocumentOptions.AllowTrailingCommas = $false
    $jsonDocumentOptions.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $jsonDocumentOptions.MaxDepth = 64
    try {
        $coreDocument = [System.Text.Json.JsonDocument]::Parse(
            $coreJson,
            $jsonDocumentOptions)
    }
    catch {
        throw 'The Q-001 focused test produced invalid strict JSON evidence.'
    }

    try {
        Assert-NoDuplicateJsonProperties -Element $coreDocument.RootElement -Path '$'
    }
    finally {
        $coreDocument.Dispose()
    }

    try {
        $core = $coreJson | ConvertFrom-Json
    }
    catch {
        throw 'The Q-001 focused test produced invalid JSON evidence.'
    }

    $humanItems = @($core.humanChecklist.items)
    $pendingHumanItems = @(
        $humanItems |
            Where-Object { [string]$_.status -ceq 'PENDING' }
    )
    $checks = [ordered]@{
        schemaVersion = [string]$core.schemaVersion -ceq '1.0.0'
        requirement = [string]$core.requirement -ceq 'Q-001'
        fixtureKind = [string]$core.scope.fixtureKind -ceq
            'deterministic-rights-safe-synthetic-contract'
        noActualMedia = -not [bool]$core.scope.actualMediaUsed
        noActualAsr = -not [bool]$core.scope.actualAsrExecuted
        noActualModelInference = -not [bool]$core.scope.actualModelInferenceUsed
        noActualExportPolicyAuthorization = -not [bool]$core.scope.actualExportPolicyAuthorized
        noActualFinalRender = -not [bool]$core.scope.actualFinalRenderExecuted
        noActualFfmpeg = -not [bool]$core.scope.actualFfmpegExecuted
        noActualMediaQc = -not [bool]$core.scope.actualMediaQcExecuted
        noActualThumbnail = -not [bool]$core.scope.actualThumbnailGenerated
        syntheticApprovalFixture = [bool]$core.scope.syntheticApprovalFixtureUsed
        syntheticDecisionRecord = [bool]$core.scope.syntheticDecisionRecordUsed
        syntheticDownstreamFixture = [bool]$core.scope.syntheticDownstreamFixtureUsed
        noHumanCandidateDispositionClaim = -not [bool]$core.scope.humanCandidateDispositionClaimed
        noHumanReleaseApprovalClaim = -not [bool]$core.scope.humanReleaseApprovalClaimed
        noHumanAcceptanceClaim = -not [bool]$core.scope.humanAcceptanceClaimed
        deleteCandidates = [bool]$core.contracts.deleteRangeCandidatesAvailable
        contextDeviationDecisionRecorded = [bool]$core.contracts.contextDeviationSuggestionDecisionRecorded
        longFormCoreElements = [bool]$core.contracts.longFormOpeningBodyEndingIncluded
        planMappedToComposition = [bool]$core.contracts.planMappedToComposition
        subtitleCorrectionReflected = [bool]$core.contracts.subtitleCorrectionReflected
        topicHeadingsConfigured = [bool]$core.contracts.topicHeadingsConfigured
        sourceTraceabilityClosed = [bool]$core.contracts.sourceTraceabilityClosed
        syntheticDeliveryBindingsValidated = [bool]$core.contracts.syntheticDeliveryBindingsValidated
        twoTopics = [int]$core.longForm.topicElementCount -eq 2
        longFormNotExternalPublicationReady = -not
            [bool]$core.longForm.externalPublicationReady
        deliveryUsedTwoSourceSpans = [int]$core.traceability.usedSourceSpanCount -eq 2
        traceRoundTrips = [int]$core.traceability.reverseRoundTripCount -eq
            [int]$core.traceability.usedSourceSpanCount
        outputProbeCountPositive = [int]$core.traceability.outputProbeCount -ge 1
        outputProbeResolvedAll = [int]$core.traceability.outputProbeCount -eq [int]$core.traceability.resolvedOutputProbeCount
        noUnresolvedOrigins = [int]$core.traceability.unresolvedOriginCount -eq 0
        noUnresolvedOutputTimes = [int]$core.traceability.unresolvedOutputTimeCount -eq 0
        oneChangedToken = [int]$core.subtitleEvidence.changedTokenCount -eq 1
        revisedCandidateBoundToCorrection = [bool]$core.subtitleEvidence.revisedCandidateBoundToCorrection
        revisedCandidateTextDigestValid = [bool]$core.subtitleEvidence.revisedCandidateTextDigestValid
        revisedCandidateRevisionDigestMatches = [bool]$core.subtitleEvidence.revisedCandidateSourceRevisionDigestMatches
        webVttDigestValid = [bool]$core.subtitleEvidence.webVttDigestValid
        webVttCueCount = [int]$core.subtitleEvidence.webVttCueCount -eq 1
        atLeastOneDeleteCandidate = [int]$core.deleteCandidates.candidateCount -ge 1
        leadingDeleteCandidate = [int]$core.deleteCandidates.leadingCandidateCount -eq 1
        leadingPosition = [string]$core.deleteCandidates.representativePosition -ceq
            'LeadingBeforeFirstTopic'
        candidateProposed = [bool]$core.deleteCandidates.candidateProposed
        candidateRejected = [bool]$core.deleteCandidates.candidateRejected
        decisionKindRejected = [string]$core.deleteCandidates.decisionKind -ceq 'Rejected'
        decisionStatusRejected = [string]$core.deleteCandidates.statusAfterDecision -ceq 'Rejected'
        decisionSourceDigestMatched = [bool]$core.deleteCandidates.decisionSourceDigestMatches
        decisionActorBound = [bool]$core.deleteCandidates.decisionActorBound
        qualityControlEvidenceLevel = [string]$core.qualityControl.evidenceLevel -ceq
            'synthetic-static-contract'
        qualityControlStaticValidated = [bool]$core.qualityControl.staticEvidenceValidated
        qualityControlNoActualQc = -not [bool]$core.qualityControl.actualMediaQcExecuted
        qualityControlSyntheticFixture = [bool]$core.qualityControl.syntheticDownstreamFixtureUsed
        qualityControlNoNumericDisclosure = -not [bool]$core.qualityControl.actualNumericMeasurementsDisclosed
        deliveryEvidenceLevel = [string]$core.delivery.approvalEvidenceLevel -ceq
            'synthetic-contract-only'
        deliveryNoHumanApprovalClaim = -not [bool]$core.delivery.humanApprovalClaimed
        deliveryApprovalDigestValid = [bool]$core.delivery.approvalEvidenceDigestValid
        deliveryEvidenceDigestValid = [bool]$core.delivery.deliveryEvidenceDigestValid
        deliveryDigestStability = [bool]$core.delivery.digestStabilityValidated
        brokenProvenanceDetected = [bool]$core.negativeControls.brokenProvenance.detected
        brokenProvenanceFailure = [string]$core.negativeControls.brokenProvenance.expectedDeliveryFailure -ceq
            'ProvenanceInvalid'
        brokenProvenanceBoundary = [string]$core.negativeControls.brokenProvenance.boundary -ceq
            'delivery-create'
        brokenProvenanceSideEffectFree = [int]$core.negativeControls.brokenProvenance.dependencySideEffectCount -eq 0
        staleApprovalDetected = [bool]$core.negativeControls.staleApproval.detected
        staleApprovalFailure = [string]$core.negativeControls.staleApproval.expectedDeliveryFailure -ceq
            'StaleApproval'
        staleApprovalBoundary = [string]$core.negativeControls.staleApproval.boundary -ceq
            'delivery-create'
        staleApprovalSideEffectFree = [int]$core.negativeControls.staleApproval.dependencySideEffectCount -eq 0
        unconfirmedRightsDetected = [bool]$core.negativeControls.unconfirmedRights.detected
        unconfirmedRightsFailure = [string]$core.negativeControls.unconfirmedRights.expectedDeliveryFailure -ceq
            'ExternalRightsNotApproved'
        unconfirmedRightsBoundary = [string]$core.negativeControls.unconfirmedRights.boundary -ceq
            'render-graph-precompile'
        unconfirmedRightsSideEffectFree = [int]$core.negativeControls.unconfirmedRights.dependencySideEffectCount -eq 0
        requiredHumanChecks = [int]$core.humanChecklist.requiredCount -eq 8
        noHumanChecksComplete = [int]$core.humanChecklist.completedCount -eq 0
        allHumanChecksPresent = $humanItems.Count -eq 8
        allHumanChecksPending = $pendingHumanItems.Count -eq 8
        checklistDecisionPending = [string]$core.humanChecklist.decision -ceq 'PENDING'
        automatedCoverageEnumeratedOnly = [bool]$core.automatedCoverage.enumeratedAutomatedContractsOnly
        automatedCoverageLimitationsMinimum = @($core.automatedCoverage.knownLimitations).Count -ge 4
        automatedPassed = [bool]$core.automatedPassed
        humanDecisionPending = [string]$core.humanDecision -ceq 'PENDING'
        q001Incomplete = -not [bool]$core.q001Complete
    }
    $failedChecks = @(
        $checks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedChecks.Count -ne 0) {
        throw "Q-001 evidence validation failed: $($failedChecks.Key -join ', ')."
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
    ) -Message 'The final Q-001 repository revision is invalid.'
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
        -Message 'The Q-001 evidence sources changed during the acceptance run.'

    $result = [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        requirement = 'Q-001'
        measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = [pscustomobject][ordered]@{
            operatingSystem = 'Windows'
            architecture =
                [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            powershellEdition = $PSVersionTable.PSEdition
            powershellVersion = $PSVersionTable.PSVersion.ToString()
            dotnetSdkVersion = $dotnetVersion
        }
        execution = [pscustomobject][ordered]@{
            webPrerequisitesBuilt = $true
            warningsAsErrorsBuildPassed = $true
            focusedTestMinimum = 1
            focusedTestPassed = $true
            deterministicRepeatPassed = $true
            deterministicCoreEvidence = $deterministicCoreEvidence
            ffmpegDiscoveryAttempted = $false
            ffmpegExecuted = $false
            sourceFilesUnchangedDuringRun =
                $sourceFilesUnchangedDuringRun
        }
        evidence = $core
        repositoryHeadCommit = $finalHeadCommit
        sourceFingerprints = $fingerprints
        automatedPassed = $true
        humanDecision = 'PENDING'
        q001Complete = $false
    }
    $json = $result | ConvertTo-Json -Depth 30 -Compress
    $privacyChecks = [ordered]@{
        windowsAbsolutePathAbsent = $json -notmatch '(?i)[A-Z]:[\\/]'
        uncPathAbsent = $json -notmatch '\\\\'
        wslWindowsPathAbsent = $json -notmatch '(?i)/mnt/[a-z]/'
        sidAbsent = $json -notmatch 'S-[0-9]+(?:-[0-9]+){2,}'
        userNameAbsent = -not $json.Contains(
            [Environment]::UserName,
            [StringComparison]::OrdinalIgnoreCase)
        transcriptContentAbsent =
            -not $json.Contains('synthetic machine topic statement', [StringComparison]::Ordinal) -and
            -not $json.Contains('synthetic reviewed topic statement', [StringComparison]::Ordinal) -and
            -not $json.Contains('synthetic delivery statement', [StringComparison]::Ordinal) -and
            -not $json.Contains('ここはカット synthetic prelude', [StringComparison]::Ordinal)
        actorSidAbsent =
            -not $json.Contains('S-1-5-21-1000-2000-3000-5001', [StringComparison]::Ordinal)
        assetFileNameAbsent =
            -not $json.Contains('opening.mp4', [StringComparison]::Ordinal) -and
            -not $json.Contains('key-visual.png', [StringComparison]::Ordinal) -and
            -not $json.Contains('logo.svg', [StringComparison]::Ordinal) -and
            -not $json.Contains('jingle.wav', [StringComparison]::Ordinal) -and
            -not $json.Contains('synthetic-source.mov', [StringComparison]::Ordinal) -and
            -not $json.Contains('ending.mp4', [StringComparison]::Ordinal)
        storageReferenceAbsent =
            -not $json.Contains('projects/q001/renders/final.mp4', [StringComparison]::Ordinal)
        credentialAssignmentAbsent =
            $json -notmatch '(?i)(password|api[_-]?key|access[_-]?token|client[_-]?secret|connection[_-]?string|shared[_-]?access[_-]?signature)\s*[:=]'
    }
    $failedPrivacyChecks = @(
        $privacyChecks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedPrivacyChecks.Count -ne 0) {
        throw "Q-001 result privacy validation failed: $($failedPrivacyChecks.Key -join ', ')."
    }

    $json
}
finally {
    if ($null -eq $previousResultPath) {
        Remove-Item Env:HUBRADIO_Q001_RESULT_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:HUBRADIO_Q001_RESULT_PATH = $previousResultPath
    }
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}