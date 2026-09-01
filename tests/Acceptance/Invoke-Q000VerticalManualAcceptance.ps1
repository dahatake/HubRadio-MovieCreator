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
$ffmpegManifestPath = Join-Path $repoRoot 'packaging\ffmpeg\manifest.json'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'hubradio-q000-vertical-manual-{0}' -f
        [Guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $workRoot 'acceptance-core.json'
$repeatResultPath = Join-Path $workRoot 'acceptance-core-repeat.json'
$dotnet = (Get-Command -Name 'dotnet.exe' -ErrorAction Stop).Source
$git = (Get-Command -Name 'git.exe' -ErrorAction Stop).Source
$npm = (Get-Command -Name 'npm.cmd' -ErrorAction Stop).Source
$previousResultPath = $env:HUBRADIO_Q000_RESULT_PATH

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
            throw 'A Q-000 child process could not be started.'
        }

        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'A Q-000 child process exceeded its bounded timeout.'
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
    ) -Message 'A Q-000 fingerprint source is missing.'
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
        [Text.Json.JsonElement]$Element
    )

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw 'Q-000 JSON evidence contains a duplicate property.'
            }

            Assert-NoDuplicateJsonProperties -Element $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties -Element $item
        }
    }
}

function Test-PixelRect {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Rect,

        [Parameter(Mandatory)]
        [int]$X,

        [Parameter(Mandatory)]
        [int]$Y,

        [Parameter(Mandatory)]
        [int]$Width,

        [Parameter(Mandatory)]
        [int]$Height
    )

    return [int]$Rect.x -eq $X -and
        [int]$Rect.y -eq $Y -and
        [int]$Rect.width -eq $Width -and
        [int]$Rect.height -eq $Height
}

$sourceFiles = @(
    'tests\Acceptance\Must_VerticalManual.cs'
    'tests\Acceptance\Invoke-Q000VerticalManualAcceptance.ps1'
    'tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj'
    'src\HubRadio.Web\package.json'
    'src\HubRadio.Web\package-lock.json'
    'packaging\ffmpeg\manifest.json'
    'src\HubRadio.Core\Layout\ManualLayoutResolver.cs'
    'src\HubRadio.Core\Layout\KeyframeTrack.cs'
    'src\HubRadio.Core\Layout\LayoutResolver.cs'
    'src\HubRadio.Core\Subtitles\SubtitleSegmenter.cs'
    'src\HubRadio.Core\Subtitles\SubtitleLayoutEngine.cs'
    'src\HubRadio.Core\Subtitles\SubtitleDisplayPolicy.cs'
    'src\HubRadio.App\Subtitles\SubtitleService.cs'
    'src\HubRadio.App\Subtitles\SubtitleDisplayMapper.cs'
    'src\HubRadio.Infrastructure\Subtitles\SubtitleWriterContract.cs'
    'src\HubRadio.Infrastructure\Subtitles\AssSubtitleWriter.cs'
    'src\HubRadio.Core\Rendering\RenderGraph.cs'
    'src\HubRadio.Core\Rendering\RenderGraphValidator.cs'
    'src\HubRadio.Core\Rendering\RenderManifest.cs'
    'src\HubRadio.Core\Rendering\DeliveryManifest.cs'
    'src\HubRadio.Infrastructure\Rendering\FfmpegCompiler.cs'
    'src\HubRadio.Infrastructure\Rendering\OutputQc.cs'
    'src\HubRadio.App\Delivery\DeliveryService.cs'
    'src\HubRadio.Infrastructure\Integrity\BinaryVerifier.cs'
    'src\HubRadio.Infrastructure\Processes\ProcessRunRequest.cs'
)

$initialHeadCommit = (Invoke-RequiredProcess `
    -Description 'Initial repository revision inspection' `
    -FileName $git -ArgumentList @('-C', $repoRoot, 'rev-parse', 'HEAD') `
    -WorkingDirectory $repoRoot -TimeoutSeconds 30).stdout.Trim()
Assert-Condition -Condition (
    $initialHeadCommit -cmatch '\A[0-9a-f]{40}\z'
) -Message 'The initial Q-000 repository revision is invalid.'
$headSymbolicRef = Invoke-CapturedProcess -FileName $git `
    -ArgumentList @('-C', $repoRoot, 'symbolic-ref', '-q', 'HEAD') `
    -WorkingDirectory $repoRoot -TimeoutSeconds 30
Assert-Condition -Condition (
    $headSymbolicRef.exitCode -ne 0
) -Message 'The Q-000 acceptance worktree must remain detached.'
$initialFingerprints = @(
    foreach ($relativePath in $sourceFiles) {
        Get-SourceFingerprint -RelativePath $relativePath
    }
)

try {
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $env:HUBRADIO_Q000_RESULT_PATH = $resultPath

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $manifestBytes = [IO.File]::ReadAllBytes($ffmpegManifestPath)
    $manifestJson = $strictUtf8.GetString($manifestBytes)
    $manifestDocument = [Text.Json.JsonDocument]::Parse(
        $manifestJson,
        [Text.Json.JsonDocumentOptions]@{
            AllowTrailingCommas = $false
            CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
            MaxDepth = 64
        })
    try {
        Assert-NoDuplicateJsonProperties -Element $manifestDocument.RootElement
    }
    finally {
        $manifestDocument.Dispose()
    }

    try {
        $ffmpegManifest = $manifestJson | ConvertFrom-Json
    }
    catch {
        throw 'The FFmpeg authority manifest contains invalid JSON.'
    }

    $ffmpegEntries = @(
        $ffmpegManifest.selectedFiles |
            Where-Object { [string]$_.role -ceq 'ffmpeg' }
    )
    Assert-Condition -Condition (
        $ffmpegEntries.Count -eq 1
    ) -Message 'The FFmpeg authority manifest must identify one FFmpeg binary.'
    $ffmpegEntry = $ffmpegEntries[0]
    $licenseLegalApproval =
        [bool]$ffmpegManifest.licenseAssessment.legalApprovalGranted
    $releaseLegalApproval =
        [bool]$ffmpegManifest.releaseGates.legalApprovalGranted
    $releaseApproval =
        [bool]$ffmpegManifest.releaseGates.releaseApprovalGranted
    Assert-Condition -Condition (
        $licenseLegalApproval -eq $releaseLegalApproval
    ) -Message 'The FFmpeg legal approval gates disagree.'
    Assert-Condition -Condition (
        [string]$ffmpegEntry.sha256 -ceq
            '6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb'
    ) -Message 'The FFmpeg authority digest is not the approved fixed digest.'
    Assert-Condition -Condition (
        [long]$ffmpegEntry.byteSize -eq 113536512
    ) -Message 'The FFmpeg authority byte size is not the approved fixed size.'
    $ffmpegGateClosed = -not (
        $licenseLegalApproval -and $releaseApproval)
    Assert-Condition -Condition $ffmpegGateClosed `
        -Message 'This contract-only runner is fail-closed when FFmpeg gates change.'
    $ffmpegBinaryDiscoveryAttempted = $false
    $ffmpegExecutionAttempted = $false

    $null = Invoke-RequiredProcess -Description 'Web dependency restore' `
        -FileName $npm -ArgumentList @('ci') -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web TypeScript verification' `
        -FileName $npm -ArgumentList @('run', 'typecheck') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Web production build' `
        -FileName $npm -ArgumentList @('run', 'build') `
        -WorkingDirectory $webRoot
    $null = Invoke-RequiredProcess -Description 'Q-000 test restore' `
        -FileName $dotnet -WorkingDirectory $repoRoot `
        -ArgumentList @(
            'restore', $testProject,
            '-p:Platform=x64',
            '-p:TreatWarningsAsErrors=true',
            '--verbosity', 'minimal'
        )
    $null = Invoke-RequiredProcess -Description 'Q-000 warnings-as-errors build' `
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
    ) -Message 'The built Q-000 test executable is missing.'
    $null = Invoke-RequiredProcess -Description 'Q-000 focused acceptance test' `
        -FileName $testExecutable -WorkingDirectory $repoRoot `
        -ArgumentList @(
            '--filter',
            'FullyQualifiedName~HubRadio.App.Tests.Must_VerticalManual',
            '--minimum-expected-tests', '1',
            '--progress', 'off'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $resultPath -PathType Leaf
    ) -Message 'The Q-000 focused test did not produce acceptance evidence.'

    $coreBytes = [IO.File]::ReadAllBytes($resultPath)
    Assert-Condition -Condition (
        $coreBytes.Length -gt 0
    ) -Message 'The Q-000 focused test produced empty evidence.'
    $hasUtf8Bom = $coreBytes.Length -ge 3 -and
        $coreBytes[0] -eq 0xEF -and
        $coreBytes[1] -eq 0xBB -and
        $coreBytes[2] -eq 0xBF
    Assert-Condition -Condition (-not $hasUtf8Bom) `
        -Message 'The Q-000 focused test produced UTF-8 BOM evidence.'
    $env:HUBRADIO_Q000_RESULT_PATH = $repeatResultPath
    $null = Invoke-RequiredProcess `
        -Description 'Q-000 deterministic repeat acceptance test' `
        -FileName $testExecutable -WorkingDirectory $repoRoot `
        -ArgumentList @(
            '--filter',
            'FullyQualifiedName~HubRadio.App.Tests.Must_VerticalManual',
            '--minimum-expected-tests', '1',
            '--progress', 'off'
        )
    Assert-Condition -Condition (
        Test-Path -LiteralPath $repeatResultPath -PathType Leaf
    ) -Message 'The Q-000 repeat test did not produce acceptance evidence.'
    $repeatCoreBytes = [IO.File]::ReadAllBytes($repeatResultPath)
    $deterministicCoreEvidence =
        $coreBytes.Length -eq $repeatCoreBytes.Length -and
        [Linq.Enumerable]::SequenceEqual[byte](
            $coreBytes,
            $repeatCoreBytes)
    Assert-Condition -Condition $deterministicCoreEvidence `
        -Message 'The Q-000 canonical core evidence is not deterministic.'
    $coreJson = $strictUtf8.GetString($coreBytes)
    $coreDocument = [Text.Json.JsonDocument]::Parse(
        $coreJson,
        [Text.Json.JsonDocumentOptions]@{
            AllowTrailingCommas = $false
            CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
            MaxDepth = 64
        })
    try {
        Assert-NoDuplicateJsonProperties -Element $coreDocument.RootElement
    }
    finally {
        $coreDocument.Dispose()
    }

    try {
        $core = $coreJson | ConvertFrom-Json
    }
    catch {
        throw 'The Q-000 focused test produced invalid JSON evidence.'
    }

    $keyframeSamples = @($core.keyframes.samples)
    $humanItems = @($core.humanChecklist.items)
    $pendingHumanItems = @(
        $humanItems |
            Where-Object { [string]$_.status -ceq 'PENDING' }
    )
    $checks = [ordered]@{
        schemaVersion = [string]$core.schemaVersion -ceq '1.0.0'
        requirement = [string]$core.requirement -ceq 'Q-000'
        fixtureKind = [string]$core.scope.fixtureKind -ceq
            'deterministic-rights-safe-synthetic-contract'
        noActualMedia = -not [bool]$core.scope.actualMediaUsed
        noActualFfmpeg = -not [bool]$core.scope.actualFfmpegExecuted
        noActualMediaQc = -not [bool]$core.scope.actualMediaQcExecuted
        noActualFaceDetection = -not
            [bool]$core.scope.actualFaceDetectionExecuted
        noPlatformSafeZoneCertification = -not
            [bool]$core.scope.platformSpecificSafeZoneCertified
        noHumanIntervalSelection = -not
            [bool]$core.scope.humanSelectedInterval
        noHumanApprovalClaim = -not [bool]$core.scope.humanApprovalClaimed
        standInInterval = [string]$core.interval.selectionKind -ceq
            'deterministic-stand-in'
        intervalNotHumanSelected = -not [bool]$core.interval.humanSelected
        intervalStartsAtZero = [string]$core.interval.outputStart -ceq '0/1'
        intervalEndsAtFinalKeyframe =
            [string]$core.interval.outputDuration -ceq '2/1'
        rightsApproved = [string]$core.interval.rightsStatus -ceq 'Approved'
        externalPublicationAllowed =
            [bool]$core.interval.externalPublicationAllowed
        manualLayoutResolved =
            [string]$core.layout.staticResolutionStatus -ceq 'Resolved'
        manualKeyframesRequired =
            [string]$core.layout.dynamicResolutionStatus -ceq
                'ManualKeyframesRequired'
        outputWidth = [int]$core.layout.outputPixelWidth -eq 1080
        outputHeight = [int]$core.layout.outputPixelHeight -eq 1920
        outputAspectWidth = [int]$core.layout.outputAspectWidth -eq 9
        outputAspectHeight = [int]$core.layout.outputAspectHeight -eq 16
        oneSourcePlacement = [int]$core.layout.sourcePlacementCount -eq 1
        genericFullFrameOnly =
            [bool]$core.layout.genericFullFrameSafeZoneOnly
        subtitleInGenericSafeZone =
            [bool]$core.layout.subtitleContainedByGenericSafeZone
        noPlatformSafeZoneClaim = -not
            [bool]$core.layout.platformSpecificSafeZoneClaimed
        exactInterpolationRule =
            [string]$core.keyframes.interpolationRule -ceq
                'exact-rational-nearest-to-even'
        twoKeyframes = [int]$core.keyframes.keyframeCount -eq 2
        threeKeyframeSamples = $keyframeSamples.Count -eq 3
        canonicalNormalizedRoundTrip =
            [bool]$core.keyframes.canonicalNormalizedRoundTripValidated
        startSourceCrop = Test-PixelRect `
            -Rect $keyframeSamples[0].sourceCropPixels `
            -X 0 -Y 0 -Width 540 -Height 960
        midpointSourceCrop = Test-PixelRect `
            -Rect $keyframeSamples[1].sourceCropPixels `
            -X 480 -Y 270 -Width 405 -Height 720
        endpointSourceCrop = Test-PixelRect `
            -Rect $keyframeSamples[2].sourceCropPixels `
            -X 960 -Y 540 -Width 270 -Height 480
        startDestination = Test-PixelRect `
            -Rect $keyframeSamples[0].destinationPixels `
            -X 0 -Y 0 -Width 540 -Height 960
        midpointDestination = Test-PixelRect `
            -Rect $keyframeSamples[1].destinationPixels `
            -X 405 -Y 720 -Width 405 -Height 720
        endpointDestination = Test-PixelRect `
            -Rect $keyframeSamples[2].destinationPixels `
            -X 810 -Y 1440 -Width 270 -Height 480
        keyframesNotBoundToCompiler = -not
            [bool]$core.keyframes.boundToCompiler
        noRenderedKeyframeClaim = -not
            [bool]$core.keyframes.renderedEvidenceClaimed
        oneSubtitleCandidate =
            [int]$core.subtitles.segmentationCandidateCount -eq 1
        noHumanTranscriptWorkClaim = -not
            [bool]$core.subtitles.humanTranscriptWorkClaimed
        emptyFaceRegionInput =
            [int]$core.subtitles.faceRegionInputCount -eq 0
        noActualFaceAbsenceClaim = -not
            [bool]$core.subtitles.actualFaceAbsenceClaimed
        noActualFaceAvoidanceClaim = -not
            [bool]$core.subtitles.actualFaceAvoidanceClaimed
        subtitleLayoutResolved =
            [string]$core.subtitles.layoutStatus -ceq 'Resolved'
        subtitleSafeZoneConstraint =
            [bool]$core.subtitles.safeZoneConstraintSatisfied
        oneDisplayedCue = [int]$core.subtitles.displayedCueCount -eq 1
        assFormat = [string]$core.subtitles.assFormat -ceq
            'AdvancedSubStationAlpha'
        oneAssCue = [int]$core.subtitles.assCueCount -eq 1
        nonemptyAss = [long]$core.subtitles.assByteSize -gt 0
        assDigest = [string]$core.subtitles.assContentDigest -cmatch
            '\A[0-9a-f]{64}\z'
        assTimestampRule =
            [string]$core.subtitles.timestampRuleVersion -ceq
                'r013.nearest-to-even-target-units.v1'
        noRawSubtitleText = -not [bool]$core.subtitles.rawTextExposed
        renderEvidenceContractOnly =
            [string]$core.render.evidenceLevel -ceq 'compiler-contract-only'
        graphValid = [bool]$core.render.graphValid
        graphIssueFree = [int]$core.render.graphIssueCount -eq 0
        compilerContract = [bool]$core.render.compilerContractValidated
        twoCompilerInputs = [int]$core.render.inputCount -eq 2
        verifierCoveredToolAndInputs =
            [int]$core.render.verifierCallCount -eq
                ([int]$core.render.inputCount + 1)
        processRequestOnly = [bool]$core.render.processRequestGenerated
        processNotStarted = -not [bool]$core.render.processStarted
        renderKeyframesNotBound = -not
            [bool]$core.render.keyframesBoundToCompiler
        tokenizedArguments = [bool]$core.render.tokenizedArguments
        metadataStripped = [bool]$core.render.metadataStripped
        nonOverwrite = [bool]$core.render.nonOverwrite
        ffmpegDigest = [string]$core.render.ffmpegGate.expectedSha256 -ceq
            [string]$ffmpegEntry.sha256
        ffmpegByteSize =
            [long]$core.render.ffmpegGate.expectedByteSize -eq
                [long]$ffmpegEntry.byteSize
        legalGateMatches =
            [bool]$core.render.ffmpegGate.legalApprovalGranted -eq
                $licenseLegalApproval
        releaseGateMatches =
            [bool]$core.render.ffmpegGate.releaseApprovalGranted -eq
                $releaseApproval
        binaryDiscoveryNotAttempted = -not
            [bool]$core.render.ffmpegGate.binaryDiscoveryAttempted
        environmentGated =
            [string]$core.render.ffmpegGate.executionStatus -ceq
                'environment-gated'
        runnerDidNotDiscoverFfmpeg = -not $ffmpegBinaryDiscoveryAttempted
        runnerDidNotExecuteFfmpeg = -not $ffmpegExecutionAttempted
        qcEvidenceStaticOnly =
            [string]$core.qualityControl.evidenceLevel -ceq
                'static-evidence-only'
        staticQcValidated =
            [bool]$core.qualityControl.staticEvidenceValidated
        actualMediaQcNotExecuted = -not
            [bool]$core.qualityControl.actualMediaQcExecuted
        syntheticQcLabeled =
            [bool]$core.qualityControl.syntheticDownstreamFixtureUsed
        humanReviewRequired =
            [bool]$core.qualityControl.humanReviewRequired
        numericThresholdsNotApproved = -not
            [bool]$core.qualityControl.approvedNumericThresholdsApplied
        cannotProceedToDelivery = -not
            [bool]$core.qualityControl.canProceedToDelivery
        deliveryApprovalSyntheticOnly =
            [string]$core.delivery.approvalEvidenceLevel -ceq
                'synthetic-contract-only'
        syntheticApprovalLabeled =
            [bool]$core.delivery.syntheticApprovalFixtureUsed
        noDeliveryHumanApprovalClaim = -not
            [bool]$core.delivery.humanApprovalClaimed
        noReleaseApprovalClaim = -not
            [bool]$core.delivery.releaseApprovalClaimed
        deliveryDigestsBound = [bool]$core.delivery.digestBindingsValidated
        burnedInVariant =
            [string]$core.delivery.subtitleVariant -ceq 'BurnedIn'
        burnedInSidecarAbsent =
            [bool]$core.delivery.burnedInSidecarAbsent
        approvalEvidenceDigest =
            [string]$core.delivery.approvalEvidenceDigest -cmatch
                '\A[0-9a-f]{64}\z'
        deliveryEvidenceDigest =
            [string]$core.delivery.deliveryEvidenceDigest -cmatch
                '\A[0-9a-f]{64}\z'
        brokenProvenanceDetected =
            [bool]$core.negativeControls.brokenProvenance.detected
        brokenProvenanceGraphIssue =
            [bool]$core.negativeControls.brokenProvenance.graphIssueDetected
        brokenProvenanceFailure =
            [string]$core.negativeControls.brokenProvenance.expectedDeliveryFailure -ceq
                'ProvenanceInvalid'
        brokenProvenanceSideEffectFree =
            [int]$core.negativeControls.brokenProvenance.dependencySideEffectCount -eq 0
        staleApprovalDetected =
            [bool]$core.negativeControls.staleApproval.detected
        staleApprovalFailure =
            [string]$core.negativeControls.staleApproval.expectedDeliveryFailure -ceq
                'StaleApproval'
        staleApprovalSideEffectFree =
            [int]$core.negativeControls.staleApproval.dependencySideEffectCount -eq 0
        sevenHumanChecks = [int]$core.humanChecklist.requiredCount -eq 7
        noHumanChecksComplete =
            [int]$core.humanChecklist.completedCount -eq 0
        allHumanChecksPresent = $humanItems.Count -eq 7
        allHumanChecksPending = $pendingHumanItems.Count -eq 7
        checklistDecisionPending =
            [string]$core.humanChecklist.decision -ceq 'PENDING'
        automatedPassed = [bool]$core.automatedPassed
        humanDecisionPending = [string]$core.humanDecision -ceq 'PENDING'
        q000Incomplete = -not [bool]$core.q000Complete
    }
    $failedChecks = @(
        $checks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedChecks.Count -ne 0) {
        throw "Q-000 evidence validation failed: $($failedChecks.Key -join ', ')."
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
    ) -Message 'The final Q-000 repository revision is invalid.'
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
        -Message 'The Q-000 evidence sources changed during the acceptance run.'
    $result = [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        requirement = 'Q-000'
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
            sourceFilesUnchangedDuringRun =
                $sourceFilesUnchangedDuringRun
        }
        ffmpegGateAuthority = [pscustomobject][ordered]@{
            expectedSha256 = [string]$ffmpegEntry.sha256
            expectedByteSize = [long]$ffmpegEntry.byteSize
            legalApprovalGranted = $licenseLegalApproval
            releaseApprovalGranted = $releaseApproval
            binaryDiscoveryAttempted = $ffmpegBinaryDiscoveryAttempted
            executionAttempted = $ffmpegExecutionAttempted
            executionStatus = 'environment-gated'
        }
        evidence = $core
        repositoryHeadCommit = $finalHeadCommit
        sourceFingerprints = $fingerprints
        automatedPassed = $true
        humanDecision = 'PENDING'
        q000Complete = $false
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
            -not $json.Contains(
                'synthetic vertical acceptance caption',
                [StringComparison]::Ordinal)
        sourceFileNameAbsent =
            -not $json.Contains(
                'synthetic-q000-source.mov',
                [StringComparison]::Ordinal)
        storageReferenceAbsent =
            -not $json.Contains(
                'projects/q000/renders/final.mp4',
                [StringComparison]::Ordinal)
        credentialAssignmentAbsent =
            $json -notmatch '(?i)(password|api[_-]?key|access[_-]?token|client[_-]?secret|connection[_-]?string|shared[_-]?access[_-]?signature)\s*[:=]'
    }
    $failedPrivacyChecks = @(
        $privacyChecks.GetEnumerator() |
            Where-Object { -not [bool]$_.Value }
    )
    if ($failedPrivacyChecks.Count -ne 0) {
        throw "Q-000 result privacy validation failed: $($failedPrivacyChecks.Key -join ', ')."
    }

    $json
}
finally {
    if ($null -eq $previousResultPath) {
        Remove-Item Env:HUBRADIO_Q000_RESULT_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:HUBRADIO_Q000_RESULT_PATH = $previousResultPath
    }

    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}