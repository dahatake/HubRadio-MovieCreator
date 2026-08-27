#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet("hardware", "ffmpeg-capabilities", "media-ingest", "proxy-frame-map", "encoder-matrix", "shot-detection", "face-detection", "audio-event-and-recording-quality", "process-and-package", "asr-benchmark")]
    [string]$Stage = "hardware",
    [string]$FixtureManifest = (Join-Path $PSScriptRoot "fixtures\fixture-manifest.json"),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "results\runs"),
    [ValidateRange(60, 86400)]
    [int]$ChildTimeoutSeconds = 10800
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-DescendantProcessIds {
    param([Parameter(Mandatory)][int]$RootProcessId)

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
    $descendants = [Collections.Generic.HashSet[int]]::new()
    $parents = [Collections.Generic.Queue[int]]::new()
    $parents.Enqueue($RootProcessId)
    while ($parents.Count -gt 0) {
        $parent = $parents.Dequeue()
        foreach ($child in $processes | Where-Object { [int]$_.ParentProcessId -eq $parent }) {
            $childId = [int]$child.ProcessId
            if ($descendants.Add($childId)) {
                $parents.Enqueue($childId)
            }
        }
    }
    return @($descendants)
}

function Stop-ProcessTreeAndWait {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [ValidateRange(1000, 60000)][int]$WaitMilliseconds = 10000
    )

    $observedIds = [Collections.Generic.HashSet[int]]::new()
    [void]$observedIds.Add($Process.Id)
    foreach ($processId in Get-DescendantProcessIds -RootProcessId $Process.Id) {
        [void]$observedIds.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    foreach ($processId in Get-DescendantProcessIds -RootProcessId $Process.Id) {
        [void]$observedIds.Add($processId)
    }

    $remaining = [Collections.Generic.List[int]]::new()
    foreach ($processId in $observedIds) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit($WaitMilliseconds)) {
                $remaining.Add($processId)
            }
        }
        finally {
            $observed.Dispose()
        }
    }
    if ($remaining.Count -gt 0) {
        throw "Timed-out benchmark descendants did not exit: $($remaining.Count)."
    }
}

function Get-RelativeSourcePath {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RelativePath
    )

    $manifestDirectory = Split-Path -Parent $ManifestPath
    return [IO.Path]::GetFullPath((Join-Path $manifestDirectory $RelativePath))
}

function Invoke-JsonChildProcess {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwsh
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-NoLogo", "-NoProfile", "-File", $ScriptPath, "-SamplePath", $SourcePath)) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start benchmark child process."
        }
        $started = $true

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTreeAndWait -Process $process
            throw "Benchmark child process exceeded the operational safety timeout of $TimeoutSeconds seconds."
        }
        $drainTask = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        try {
            $drainTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        }
        catch [TimeoutException] {
            Stop-ProcessTreeAndWait -Process $process
            try {
                $drainTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            }
            catch [TimeoutException] {
                throw "Benchmark child process left redirected streams open after process-tree cleanup."
            }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $safeError = Protect-SensitiveText -Text $stderr -SourcePath $SourcePath

        if ($process.ExitCode -ne 0) {
            if ($safeError.Length -gt 2000) { $safeError = $safeError.Substring(0, 2000) + " [truncated]" }
            throw "Benchmark child process exited with code $($process.ExitCode): $safeError"
        }
        if ([string]::IsNullOrWhiteSpace($stdout)) {
            throw "Benchmark child process returned no JSON."
        }

        return $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        if ($started -and -not $process.HasExited) {
            Stop-ProcessTreeAndWait -Process $process
        }
        throw
    }
    finally {
        $process.Dispose()
    }
}

function Assert-HardwarePayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "collection", "operatingSystem",
        "processor", "memory", "graphics", "npuDevices", "storage",
        "runtimesAndTools", "sampleAsset"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Hardware payload is missing required property: $name"
        }
    }

    foreach ($name in @("byteSize", "sha256")) {
        if ($Payload.sampleAsset.PSObject.Properties.Name -notcontains $name) {
            throw "Hardware payload sampleAsset is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported hardware payload schemaVersion: $($Payload.schemaVersion)"
    }
}

function Assert-FfmpegPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "package", "executables", "version",
        "licenseAssessment", "capabilities", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "FFmpeg payload is missing required property: $name"
        }
    }
    if ($Payload.package.archiveSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "FFmpeg package archive SHA-256 is invalid."
    }
    if ($Payload.executables.ffmpeg.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.executables.ffprobe.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "FFmpeg executable SHA-256 is invalid."
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported FFmpeg payload schemaVersion: $($Payload.schemaVersion)"
    }
}

function Assert-MediaPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "source", "format", "streams",
        "packetStatistics", "timingAssessment", "audioVideo", "fullDecode",
        "warnings", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Media payload is missing required property: $name"
        }
    }
    if ($Payload.fullDecode.PSObject.Properties.Name -notcontains "succeeded") {
        throw "Media payload fullDecode is missing succeeded."
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported media payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Media payload did not confirm fixture path consumption."
    }
}

function Assert-ProxyFrameMapPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "proxy", "sourceTiming", "proxyTiming",
        "mapping", "roundTrip", "visualCorrespondence", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Proxy frame map payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported proxy frame map payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.proxy.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Proxy frame map payload does not carry a valid proxy content hash."
    }
    if ([int64]$Payload.mapping.proxyFrameCount -le 0 -or [int64]$Payload.mapping.distinctSourceFramesReferenced -le 0) {
        throw "Proxy frame map payload does not describe a usable mapping."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Proxy frame map payload did not confirm fixture path consumption."
    }
}

function Assert-EncoderMatrixPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "segment", "build", "candidates", "summary", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Encoder matrix payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported encoder matrix payload schemaVersion: $($Payload.schemaVersion)"
    }
    if (@($Payload.candidates).Count -lt 1) {
        throw "Encoder matrix payload did not record any candidate."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Encoder matrix payload did not confirm fixture path consumption."
    }
}

function Assert-ShotDetectionPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "library", "detectors", "agreement",
        "controlClip", "note", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Shot detection payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported shot detection payload schemaVersion: $($Payload.schemaVersion)"
    }
    if (@($Payload.detectors).Count -lt 1) {
        throw "Shot detection payload did not record any detector configuration."
    }
    $detectorNames = @($Payload.detectors | ForEach-Object { $_.detector })
    foreach ($requiredDetector in @("content", "adaptive")) {
        if ($detectorNames -notcontains $requiredDetector) {
            throw "Shot detection payload did not compare the required detector: $requiredDetector"
        }
    }
    if ($Payload.library.proxySha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Shot detection payload does not identify the analysed proxy by content hash."
    }
    if ($Payload.controlClip.PSObject.Properties.Name -notcontains "allConfigurationsDetectKnownCuts" -or
        $Payload.controlClip.allConfigurationsDetectKnownCuts -ne $true) {
        throw "Shot detection control clip did not confirm all detector configurations against known cuts."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Shot detection payload did not confirm fixture path consumption."
    }
}

function Assert-FaceDetectionPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "library", "model", "control", "proxy",
        "sampling", "detection", "observations", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Face detection payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported face detection payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.library.proxySha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Face detection payload does not identify the analysed proxy by content hash."
    }
    if ($Payload.model.sha256 -notmatch '^[0-9a-f]{64}$' -or [int64]$Payload.model.byteSize -le 0) {
        throw "Face detection payload does not identify a usable model by content hash and byte size."
    }
    if ($Payload.sampling.runningMode -ne "VIDEO") {
        throw "Face detection payload did not use MediaPipe VIDEO mode."
    }
    if ($Payload.control.runningMode -ne "VIDEO" -or
        $Payload.control.passed -ne $true -or
        [int]$Payload.control.detectedFaceCount -lt 1 -or
        $Payload.control.asset.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Face detection positive control did not validate the pinned VIDEO-mode pipeline."
    }
    if ([int]$Payload.sampling.requestedSampleCount -ne 300 -or
        [int]$Payload.sampling.attemptedSampleCount -ne 300 -or
        [int]$Payload.sampling.decodedSampleCount -ne 300 -or
        [int]$Payload.sampling.decodeFailureCount -ne 0) {
        throw "Face detection payload did not decode all 300 required stratified samples."
    }
    if (@($Payload.observations).Count -ne [int]$Payload.sampling.decodedSampleCount) {
        throw "Face detection observation count does not match the decoded sample count."
    }
    if ($Payload.privacy.imagePixelsPersisted -ne $false) {
        throw "Face detection payload unexpectedly persisted sampled image pixels."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Face detection payload did not confirm fixture path consumption."
    }
}

function Assert-AudioEventPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "tooling", "analysisInput", "decodeEvidence",
        "models", "controls", "methods", "agreement", "performance", "privacy",
        "annotations", "automaticDeletionPerformed",
        "candidateThresholdsAreStage0ConfigurationsNotProductionValues", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Audio event payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported audio event payload schemaVersion: $($Payload.schemaVersion)"
    }
    foreach ($controlName in @("ffmpegSilencedetect", "waveformRules", "sileroVad", "yamnet")) {
        if ($Payload.controls.PSObject.Properties.Name -notcontains $controlName -or
            $Payload.controls.$controlName.passed -ne $true) {
            throw "Audio event positive control did not pass: $controlName"
        }
    }
    if ($Payload.models.yamnet.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.models.sileroVad.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Audio event payload does not identify both local models by content hash."
    }
    if ([int64]$Payload.analysisInput.decodedAnalysis.decodedSampleCountPerChannel -le 0 -or
        [double]$Payload.analysisInput.decodedAnalysis.decodedDurationSeconds -le 0) {
        throw "Audio event payload does not describe a usable decoded analysis input."
    }
    if ([string]::IsNullOrWhiteSpace([string]$Payload.analysisInput.sourceAudio.timelineAuthority) -or
        $Payload.analysisInput.decodedAnalysis.PSObject.Properties.Name -notcontains "decodedTimelineEvidence" -or
        $Payload.analysisInput.decodedAnalysis.decodedTimelineEvidence.PSObject.Properties.Name -notcontains "firstDecodedFramePts" -or
        $Payload.analysisInput.decodedAnalysis.decodedTimelineEvidence.PSObject.Properties.Name -notcontains "firstDecodedFrameSeconds") {
        throw "Audio event payload does not preserve decoded-frame timeline evidence."
    }
    if ($Payload.methods.ffmpegSilencedetect.PSObject.Properties.Name -notcontains "configuration" -or
        $Payload.methods.ffmpegSilencedetect.configuration.PSObject.Properties.Name -notcontains "noiseDb" -or
        $Payload.methods.ffmpegSilencedetect.configuration.PSObject.Properties.Name -notcontains "minimumDurationSeconds" -or
        $Payload.methods.ffmpegSilencedetect.configuration.thresholdIsProductionApproved -ne $false) {
        throw "Audio event payload does not preserve the unapproved FFmpeg silencedetect configuration."
    }
    if ($Payload.decodeEvidence.fullDecodeSucceeded -ne $true -or
        [int]$Payload.decodeEvidence.decoderReportedErrorLineCount -ne 0) {
        throw "ST-003 decode evidence does not support a clean decoder result."
    }
    if ($Payload.annotations.removalReferenceAvailable -ne $false -or
        $Payload.annotations.precisionRecallMeasured -ne $false) {
        throw "Audio event payload unexpectedly claims removal annotations or precision/recall."
    }
    if ($Payload.automaticDeletionPerformed -ne $false) {
        throw "Audio event stage must not automatically delete candidate intervals."
    }
    if ($Payload.candidateThresholdsAreStage0ConfigurationsNotProductionValues -ne $true) {
        throw "Audio event payload did not mark thresholds as unapproved Stage 0 configurations."
    }
    if ($Payload.privacy.sourceAudioPersisted -ne $false -or
        $Payload.privacy.sourceAudioSamplesIncludedInReport -ne $false -or
        $Payload.privacy.sourceTranscriptIncludedInReport -ne $false) {
        throw "Audio event payload persisted or reported source audio content."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Audio event payload did not confirm fixture path consumption."
    }
}

function Assert-ProcessAndPackagePayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "host", "tooling", "jobObject", "model",
        "package", "unpackedVerification", "appContainer", "distributionValidation",
        "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Process and package payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported process and package payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.host.hardwareBenchmarkRunSha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$Payload.host.currentOperatingSystemVersion) -or
        $Payload.host.currentOperatingSystemVersion -ne $Payload.host.recordedOperatingSystemVersion) {
        throw "Process and package payload does not link the current host to immutable hardware evidence."
    }
    if ($Payload.tooling.dotnetSdkVersion -ne "10.0.400" -or
        $Payload.tooling.dotnet.sdkVersion -ne $Payload.tooling.dotnetSdkVersion -or
        $Payload.tooling.dotnet.globalJsonSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.tooling.dotnet.processProbeLockSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.tooling.dotnet.workerProbeLockSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.tooling.dotnet.selfContained -ne $true -or
        $Payload.tooling.dotnet.publishSingleFile -ne $true) {
        throw "Process and package payload does not identify the fixed .NET probe toolchain."
    }
    if ($Payload.tooling.winAppCli.usedForPackaging -ne $false) {
        throw "Process and package payload unexpectedly used the optional WinApp CLI bootstrap."
    }
    if ($Payload.tooling.makeAppx.sha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$Payload.tooling.makeAppx.buildToolsPackageContentHash) -or
        $Payload.tooling.makeAppx.lockedRestoreSucceeded -ne $true -or
        $Payload.tooling.makeAppx.invokedThroughWinAppCli -ne $false) {
        throw "Process and package payload does not identify a locked direct MakeAppx toolchain."
    }
    $killOnClose = $Payload.jobObject.selfTest.killOnClose
    if ($Payload.jobObject.timeoutCleanup.passed -ne $true -or
        $Payload.jobObject.timeoutCleanup.childExitedAfterTimeout -ne $true -or
        $Payload.jobObject.timeoutCleanup.grandchildExitedAfterTimeout -ne $true -or
        $killOnClose.childInJobBeforeClose -ne $true -or
        $killOnClose.grandchildInJobBeforeClose -ne $true -or
        $killOnClose.childExitedAfterClose -ne $true -or
        $killOnClose.grandchildExitedAfterClose -ne $true -or
        $killOnClose.allObservedProcessIdsExitedAfterClose -ne $true -or
        [int]$killOnClose.observedProcessIdLeakCount -ne 0 -or
        $killOnClose.passed -ne $true -or
        $Payload.jobObject.selfTest.processMemoryLimit.passed -ne $true -or
        $Payload.jobObject.hostCrash.passed -ne $true -or
        [int]$Payload.jobObject.orphanProcessCountAfterTests -ne 0) {
        throw "Job Object kill-on-close, crash, memory, or orphan-process evidence did not pass."
    }
    $activeLimit = $Payload.jobObject.selfTest.activeProcessLimit
    foreach ($name in @(
        "secondAssignmentSucceeded", "secondExitedAutomatically", "callerCleanupRequired",
        "secondExitedAfterCleanup", "secondProcessAbsentAfterCleanup",
        "processIdListCountBeforeCleanup", "accountingActiveProcessCountBeforeCleanup",
        "processIdListCountAfterCleanup", "accountingActiveProcessCountAfterCleanup",
        "cleanupAccountingConverged", "documentedAutomaticTerminationObserved",
        "safeCallerCleanupContractPassed", "passed"
    )) {
        if ($activeLimit.PSObject.Properties.Name -notcontains $name) {
            throw "Active-process-limit evidence is missing required property: $name"
        }
    }
    if ($activeLimit.documentedAutomaticTerminationObserved -ne $activeLimit.secondExitedAutomatically -or
        $activeLimit.passed -ne ($activeLimit.secondAssignmentSucceeded -eq $false -and
            $activeLimit.secondExitedAutomatically -eq $true -and
            $activeLimit.firstStillRunningAfterRejectedAssignment -eq $true -and
            $activeLimit.cleanupAccountingConverged -eq $true -and
            [int]$activeLimit.processIdListCountAfterCleanup -eq 1 -and
            [int]$activeLimit.accountingActiveProcessCountAfterCleanup -eq 1)) {
        throw "Active-process-limit aggregate flags contradict the measured fields."
    }
    if ($activeLimit.secondAssignmentSucceeded -ne $false -or
        ($activeLimit.secondExitedAutomatically -ne $true -and
            ($activeLimit.callerCleanupRequired -ne $true -or
                $activeLimit.secondExitedAfterCleanup -ne $true -or
                $activeLimit.secondProcessAbsentAfterCleanup -ne $true -or
                $activeLimit.safeCallerCleanupContractPassed -ne $true)) -or
        ($activeLimit.cleanupAccountingConverged -eq $true -and
            ([int]$activeLimit.processIdListCountAfterCleanup -ne 1 -or
                [int]$activeLimit.accountingActiveProcessCountAfterCleanup -ne 1))) {
        throw "Active-process-limit rejection or caller cleanup evidence is incomplete."
    }
    if ($Payload.jobObject.selfTest.processMemoryLimit.allocationStartedAfterJobAssignment -ne $true -or
        $null -eq $Payload.jobObject.selfTest.processMemoryLimit.allocatedBytesBeforeFailure) {
        throw "Process memory limit evidence does not prove allocation started after Job assignment."
    }
    if ($Payload.jobObject.candidateLimits.processMemoryLimitIsProductionApproved -ne $false) {
        throw "Stage 0 process memory limit must not be marked production-approved."
    }
    if ($Payload.package.packageBuildAndUnpackPassed -ne $true -or
        [int]$Payload.package.firstPartyExecutableCount -lt 2 -or
        [int]$Payload.package.missingExpectedFileCount -ne 0 -or
        [int]$Payload.package.selectedHashMismatchCount -ne 0 -or
        @($Payload.package.firstPartyExecutables).Count -ne 2 -or
        @($Payload.package.firstPartyExecutables | Where-Object { $_.sha256 -notmatch '^[0-9a-f]{64}$' }).Count -ne 0 -or
        $Payload.package.signatureFilePresent -ne $false -or
        $Payload.package.unsignedByDesignForStage0 -ne $true) {
        throw "Unsigned multi-executable package build, unpack, or hash verification did not pass."
    }
    if ($Payload.unpackedVerification.processProbe.passed -ne $true -or
        $Payload.unpackedVerification.processProbe.expectedCodecsIdentified -ne $true -or
        $Payload.unpackedVerification.processProbe.codecsDecoded -ne $true -or
        $Payload.unpackedVerification.workerProbe.passed -ne $true -or
        $Payload.unpackedVerification.workerProbe.modelSha256 -ne $Payload.unpackedVerification.workerProbe.expectedModelSha256 -or
        $Payload.unpackedVerification.executedFromUnpackedLayout -ne $true) {
        throw "Unpacked codec/model verification did not pass."
    }
    if ($Payload.appContainer.appContainerExecutionTested -ne $false -or
        $Payload.distributionValidation.packageInstalled -ne $false -or
        $Payload.distributionValidation.signingTested -ne $false -or
        $Payload.distributionValidation.cleanProductionWindowsTested -ne $false) {
        throw "Stage 0 payload unexpectedly claims AppContainer, installation, signing, or clean-machine validation."
    }
    if ($Payload.privacy.sourceMediaIncludedInPackage -ne $true -or
        $Payload.privacy.originalSourceFileIncludedInPackage -ne $false -or
        $Payload.privacy.sourceDerivedMediaIncludedInPackage -ne $true -or
        $Payload.privacy.fixtureRightsStatus -ne "unconfirmed" -or
        $Payload.privacy.fixtureExternalPublicationAllowed -ne $false -or
        $Payload.privacy.packageRestrictedToLocalTechnicalEvaluation -ne $true -or
        $Payload.privacy.sourcePathIncludedInReport -ne $false -or
        $Payload.privacy.packagePathsIncludedInReport -ne $false) {
        throw "Process and package payload included source media or local paths."
    }
    $rightsBlocker = "The fixture rights status is unconfirmed and external publication is not allowed."
    if (@($Payload.distributionValidation.blockingInputs) -notcontains $rightsBlocker) {
        throw "Process and package payload does not preserve the fixture-rights distribution blocker."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Process and package payload did not confirm fixture path consumption."
    }
}

function Assert-NoUnexpectedAsrSensitiveProperties {
    param([AllowNull()][object]$Value)

    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        "textSha256", "referenceTextSha256", "recognizedTextSha256",
        "referenceCharacterCount", "recognizedCharacterCount",
        "transcriptTextIncludedInPayload", "sourceTranscriptTextIncludedInPayload",
        "sourceTranscriptTextIncludedInCanonicalSummary", "localTranscriptArtifactsContainRightsUnconfirmedText",
        "fixtureTranscriptHashes", "fixtureReferenceTranscriptAvailable", "transcriptReferenceAvailable",
        "missingReferenceRelativePath", "controlCerIsSyntheticMechanismEvidenceOnly",
        "fixtureProperNounErrorRateSubstitute", "pcmContentMatchesSource", "conditionOnPreviousText",
        "controlTranscriptionSeconds", "fixtureTranscriptionSeconds",
        "controlTranscriptionMilliseconds", "chunkTranscriptionMilliseconds"
    )) {
        [void]$allowed.Add($name)
    }

    function Test-Value {
        param([AllowNull()][object]$Current)

        if ($null -eq $Current -or $Current -is [string] -or $Current -is [ValueType]) {
            return
        }
        if ($Current -is [Collections.IDictionary]) {
            foreach ($key in $Current.Keys) {
                $propertyName = [string]$key
                if ($propertyName -match '(?i)(raw|text|transcript|utterance|content)' -and -not $allowed.Contains($propertyName)) {
                    throw "ASR payload contains an unapproved sensitive property name: $propertyName"
                }
                Test-Value -Current $Current[$key]
            }
            return
        }
        if ($Current -is [Collections.IEnumerable]) {
            foreach ($item in $Current) { Test-Value -Current $item }
            return
        }
        foreach ($property in $Current.PSObject.Properties) {
            if ($property.Name -match '(?i)(raw|text|transcript|utterance|content)' -and -not $allowed.Contains($property.Name)) {
                throw "ASR payload contains an unapproved sensitive property name: $($property.Name)"
            }
            Test-Value -Current $property.Value
        }
    }

    Test-Value -Current $Value
}

function Assert-AsrPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "host", "implementation", "tooling", "analysisInput",
        "models", "control", "candidates", "comparison", "annotations", "privacy",
        "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "ASR payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported ASR payload schemaVersion: $($Payload.schemaVersion)"
    }
    Assert-NoUnexpectedAsrSensitiveProperties -Value $Payload
    if ($Payload.host.hardwareBenchmarkRunSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.host.currentOperatingSystemVersion -ne $Payload.host.operatingSystemVersion -or
        $Payload.analysisInput.sourceSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.analysisInput.normalizedFixtureAudio.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.analysisInput.mediaIngestBenchmarkRunSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "ASR payload does not identify its host and input by content hash."
    }
    if ($Payload.implementation.gitHead -notmatch '^[0-9a-f]{40}$' -or
        @($Payload.implementation.files).Count -ne 6 -or
        @($Payload.implementation.files | Where-Object { $_.sha256 -notmatch '^[0-9a-f]{64}$' }).Count -ne 0) {
        throw "ASR payload does not identify the executed implementation and schema files."
    }
    $expectedImplementationFiles = @(
        @{ Id = "benchmark-harness"; FileName = "run-benchmarks.ps1"; Path = (Join-Path $PSScriptRoot "run-benchmarks.ps1") },
        @{ Id = "asr-wrapper"; FileName = "measure-asr.ps1"; Path = (Join-Path $PSScriptRoot "stages\measure-asr.ps1") },
        @{ Id = "asr-driver"; FileName = "asr_benchmark.py"; Path = (Join-Path $PSScriptRoot "stages\asr_benchmark.py") },
        @{ Id = "benchmark-schema"; FileName = "schema.json"; Path = (Join-Path $PSScriptRoot "results\schema.json") },
        @{ Id = "python-environment-setup"; FileName = "setup-python-env.ps1"; Path = (Join-Path $PSScriptRoot "setup-python-env.ps1") },
        @{ Id = "asr-control-config"; FileName = "asr-control.json"; Path = (Join-Path $PSScriptRoot "fixtures\asr-control.json") }
    )
    foreach ($expectedFile in $expectedImplementationFiles) {
        $records = @($Payload.implementation.files | Where-Object { $_.id -eq $expectedFile.Id })
        if ($records.Count -ne 1 -or $records[0].fileName -ne $expectedFile.FileName -or
            [int64]$records[0].byteSize -ne [int64](Get-Item -LiteralPath $expectedFile.Path).Length -or
            $records[0].sha256 -ne (Get-FileHash -LiteralPath $expectedFile.Path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "ASR implementation evidence does not match the executed file: $($expectedFile.Id)"
        }
    }
    if ($Payload.tooling.pythonPackages.'faster-whisper' -ne "1.2.1" -or
        $Payload.tooling.pythonPackages.ctranslate2 -ne "4.8.1" -or
        $Payload.tooling.pythonPackages.huggingface_hub -ne "1.28.0" -or
        $Payload.tooling.pythonPackages.tokenizers -ne "0.23.1") {
        throw "ASR payload does not carry the pinned direct Python runtime versions."
    }
    if ($Payload.models.fasterWhisperSmall.revision -ne "536b0662742c02347bc0e980a01041f333bce120" -or
        $Payload.models.fasterWhisperSmall.manifestSha256 -ne "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f") {
        throw "ASR payload does not identify the pinned faster-whisper model manifest."
    }
    $foundryAvailable = $Payload.tooling.foundryLocal.available -eq $true
    if ($foundryAvailable) {
        if ($Payload.models.foundryLocalWhisperSmall.available -ne $true -or
            $Payload.models.foundryLocalWhisperSmall.modelId -ne "openai-whisper-small-cuda-gpu:3" -or
            $Payload.models.foundryLocalWhisperSmall.manifestSha256 -ne "003489877f5a82eccbaeb435e520fb1656780331f26ae7cb4f03bbb15f8f64f8" -or
            $Payload.models.foundryLocalWhisperSmall.executionProviderManifest.manifestSha256 -ne "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021" -or
            $Payload.models.foundryLocalWhisperSmall.executionProviderVersionFile.sha256 -ne "28236ce308e5232254f4f09ca8e6df0805b9a3608a79d9e4d09a4b459fb99640" -or
            $Payload.tooling.foundryLocal.cliVersion -ne "0.10.3" -or
            $Payload.tooling.foundryLocal.catalog.modelId -ne "openai-whisper-small-cuda-gpu:3" -or
            $Payload.tooling.foundryLocal.catalog.licenseDeclaredByCatalog -ne "apache-2.0" -or
            $Payload.tooling.foundryLocal.catalog.executionProvider -ne "CUDAExecutionProvider") {
            throw "ASR payload does not identify the pinned Foundry Local model, catalog, and execution provider."
        }
        $foundryState = $Payload.tooling.foundryLocal.stateRestoration
        if (($foundryState.modelWasLoadedBefore -ne $true -and
                ($foundryState.modelUnloadAttempted -ne $true -or $foundryState.modelUnloadSucceeded -ne $true)) -or
            ($foundryState.serverWasReadyBefore -ne $true -and
                ($foundryState.serverStopAttempted -ne $true -or $foundryState.serverStopSucceeded -ne $true)) -or
            $foundryState.preAndPostStateMatch -ne $true -or
            $foundryState.modelLoadedAfter -ne $foundryState.modelWasLoadedBefore -or
            $foundryState.serverReadyAfter -ne $foundryState.serverWasReadyBefore) {
            throw "Foundry Local model or server state was not restored after the benchmark."
        }
    }
    elseif ($Payload.models.foundryLocalWhisperSmall.available -ne $false) {
        throw "ASR payload has inconsistent Foundry Local availability evidence."
    }
    if ($Payload.control.accuracyThresholdApproved -ne $false -or
        $Payload.control.audio.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.control.PSObject.Properties.Name -contains "referenceText") {
        throw "ASR control does not preserve its unapproved threshold status and content hash."
    }
    $candidates = @($Payload.candidates)
    $candidateIds = @($candidates | ForEach-Object { $_.candidateId })
    foreach ($candidateId in @(
        "faster-whisper-small-cpu-int8",
        "faster-whisper-small-cuda-float16",
        "foundry-local-whisper-small-cuda-30s-windows"
    )) {
        if ($candidateIds -notcontains $candidateId) {
            throw "ASR payload is missing candidate: $candidateId"
        }
    }
    $cpu = $candidates | Where-Object { $_.candidateId -eq "faster-whisper-small-cpu-int8" } | Select-Object -First 1
    if ($cpu.status -ne "succeeded" -or $cpu.control.passed -ne $true -or
        $cpu.control.evaluation.properNouns.allMatched -ne $true -or
        [int]$cpu.control.evaluation.properNouns.totalCount -lt 1 -or
        $cpu.fixture.segmentTimestampsAvailable -ne $true -or
        $cpu.fixture.wordTimestampsAvailable -ne $true -or
        $cpu.fixture.wholeInputDurationCoverageVerified -ne $true -or
        $cpu.fixture.timestampValidation.passed -ne $true -or
        [int]$cpu.fixture.timestampValidation.structuralViolationCount -ne 0 -or
        [int]$cpu.fixture.segmentCount -le 0 -or [int]$cpu.fixture.wordCount -le 0 -or
        [double]$cpu.comparableTiming.fixtureCoverageMilliseconds -le 0 -or
        $cpu.comparableTiming.singleObservation -ne $true -or
        [int]$cpu.processMeasurement.observedProcessIdCount -lt 1 -or
        $cpu.processMeasurement.peakAggregateWorkingSetBytes -le $cpu.processMeasurement.baselineAggregateWorkingSetBytes -or
        [string]::IsNullOrWhiteSpace([string]$cpu.processMeasurement.workingSetScope) -or
        $cpu.artifact.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "faster-whisper CPU did not produce the required Japanese control, fixture, and timestamps."
    }
    $cuda = $candidates | Where-Object { $_.candidateId -eq "faster-whisper-small-cuda-float16" } | Select-Object -First 1
    if ($cuda.status -ne "succeeded" -or $cuda.control.passed -ne $true -or
        $cuda.control.evaluation.properNouns.allMatched -ne $true -or
        $cuda.fixture.wholeInputDurationCoverageVerified -ne $true -or
        $cuda.fixture.timestampValidation.passed -ne $true -or
        [int]$cuda.fixture.timestampValidation.structuralViolationCount -ne 0 -or
        $cuda.cudaRuntime.pathInjectedOnlyForChildProcess -ne $true -or
        $cuda.cudaRuntime.manifestSha256 -ne "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021" -or
        $cuda.cudaRuntime.releaseRedistributionApproval -ne $false -or
        [double]$cuda.comparableTiming.fixtureCoverageMilliseconds -le 0 -or
        $cuda.comparableTiming.singleObservation -ne $true -or
        [int]$cuda.processMeasurement.observedProcessIdCount -lt 1 -or
        $cuda.artifact.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "faster-whisper CUDA did not produce the required Japanese control, fixture, timestamps, and runtime evidence."
    }
    $foundry = $candidates | Where-Object { $_.candidateId -eq "foundry-local-whisper-small-cuda-30s-windows" } | Select-Object -First 1
    if (-not $foundryAvailable) {
        if ($foundry.status -ne "failed" -or $foundry.error.code -ne "FOUNDRY_LOCAL_NOT_AVAILABLE") {
            throw "Unavailable Foundry Local was not recorded as an optional candidate failure."
        }
    }
    elseif ($foundry.status -eq "succeeded") {
        if ($foundry.control.passed -ne $true -or
            $foundry.control.evaluation.properNouns.allMatched -ne $true -or
            $foundry.fixture.inputWindowSubmissionCoverageVerified -ne $true -or
            $foundry.fixture.engineWholeInputCoverageVerified -ne $false -or
            $foundry.fixture.engineCoverageStatus -ne "unknown-no-engine-duration-or-timestamps" -or
            $foundry.fixture.pcmContentMatchesSource -ne $true -or
            [int]$foundry.fixture.windowSequenceViolationCount -ne 0 -or
            $foundry.fixture.engineSegmentTimestampsAvailable -ne $false -or
            $foundry.fixture.engineWordTimestampsAvailable -ne $false -or
            $foundry.fixture.derivedChunkWindowsAvailable -ne $true -or
            $foundry.fixture.derivedChunkWindowsAreNotSpeechBoundaries -ne $true -or
            $foundry.directWholeFileDiagnostic.wholeInputCoverageVerified -ne $false -or
            [double]$foundry.comparableTiming.fixtureCoverageMilliseconds -le 0 -or
            [double]$foundry.comparableTiming.adapterPreparationMilliseconds -le 0 -or
            [double]$foundry.comparableTiming.engineInvocationMilliseconds -le 0 -or
            $foundry.comparableTiming.singleObservation -ne $true -or
            $foundry.diagnosticWholeFileMeasurement.excludedFromComparableTimingAndPrimaryResourceMeasurement -ne $true -or
            [int]$foundry.processMeasurement.observedProcessIdCount -lt 2 -or
            $foundry.processMeasurement.peakAggregateWorkingSetBytes -le $foundry.processMeasurement.baselineAggregateWorkingSetBytes -or
            [string]::IsNullOrWhiteSpace([string]$foundry.processMeasurement.workingSetScope) -or
            $foundry.artifact.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Foundry Local ASR succeeded without complete chunk coverage and timestamp limitations."
        }
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$foundry.error.code)) {
        throw "Foundry Local ASR failure is missing bounded evidence."
    }
    if ($Payload.comparison.fixtureReferenceTranscriptAvailable -ne $false -or
        $Payload.comparison.fixtureCerMeasured -ne $false -or
        $Payload.comparison.fixtureWerMeasured -ne $false -or
        $Payload.comparison.fixtureProperNounErrorRateMeasured -ne $false -or
        [int]$Payload.comparison.fixturePerformanceTrialCountPerCandidate -ne 1 -or
        $Payload.comparison.performanceDistributionMeasured -ne $false -or
        $Payload.comparison.productionThresholdsApproved -ne $false -or
        $Payload.annotations.precisionOrAccuracyClaimedForFixture -ne $false) {
        throw "ASR payload unexpectedly claims fixture accuracy or production thresholds."
    }
    if ($Payload.privacy.sourceTranscriptTextIncludedInPayload -ne $false -or
        $Payload.privacy.sourceTranscriptTextIncludedInCanonicalSummary -ne $false -or
        $Payload.privacy.localTranscriptArtifactsContainRightsUnconfirmedText -ne $true -or
        $Payload.privacy.absoluteHostPathsIncludedInPayload -ne $false -or
        $Payload.privacy.localEvidenceAcl.inheritanceProtected -ne $true -or
        $Payload.privacy.localEvidenceAcl.ownerIsCurrentPrincipal -ne $true -or
        $Payload.privacy.localEvidenceAcl.approvedRulesGrantFullControl -ne $true -or
        [int]$Payload.privacy.localEvidenceAcl.unexpectedAccessRuleCount -ne 0 -or
        $Payload.privacy.localEvidenceAcl.principalIdentifiersIncludedInPayload -ne $false) {
        throw "ASR payload violated the local transcript privacy boundary."
    }
    $payloadJson = $Payload | ConvertTo-Json -Depth 20 -Compress
    if ($payloadJson -match '"(?:text|content|transcript|referenceText|recognizedText)"\s*:') {
        throw "ASR payload unexpectedly contains transcript text."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "ASR payload did not confirm fixture path consumption."
    }
}

function Protect-SensitiveText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$SourcePath
    )

    $safe = [regex]::Replace($Text, '\x1B\[[0-9;?]*[ -/]*[@-~]', '')
    $workspacePath = Split-Path -Parent $PSScriptRoot
    $replacements = [ordered]@{
        $SourcePath = "<SOURCE>"
        $workspacePath = "<WORKSPACE>"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $replacements[$env:USERPROFILE] = "<USERPROFILE>"
    }
    foreach ($path in @($replacements.Keys | Sort-Object { ([string]$_).Length } -Descending)) {
        foreach ($variant in @([string]$path, ([string]$path).Replace('\', '/')) | Sort-Object -Unique) {
            $replacement = [string]$replacements[$path]
            $safe = [regex]::Replace(
                $safe,
                [regex]::Escape($variant),
                [Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement },
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        }
    }
    return $safe
}

function Get-SafeFailurePayload {
    param(
        [AllowNull()][object]$Payload,
        [Parameter(Mandatory)][string]$Stage
    )

    $safePayload = [ordered]@{}
    if ($null -eq $Payload) {
        return [pscustomobject]$safePayload
    }

    if ($Stage -eq "asr-benchmark") {
        return [pscustomobject]$safePayload
    }

    foreach ($name in @(
        "schemaVersion", "library", "model", "control", "proxy",
        "sampling", "detection", "tooling", "decodeEvidence", "models", "controls",
        "performance", "privacy", "annotations", "automaticDeletionPerformed", "host",
        "candidateThresholdsAreStage0ConfigurationsNotProductionValues", "jobObject", "package",
        "unpackedVerification", "appContainer", "distributionValidation", "analysisInput",
        "candidates", "comparison", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -contains $name) {
            $safePayload[$name] = $Payload.$name
        }
    }
    return [pscustomobject]$safePayload
}

function Write-BenchmarkResult {
    param(
        [Parameter(Mandatory)][object]$Result,
        [Parameter(Mandatory)][string]$FinalPath
    )

    $schemaPath = Join-Path $PSScriptRoot "results\schema.json"
    $json = $Result | ConvertTo-Json -Depth 12
    if (-not ($json | Test-Json -SchemaFile $schemaPath -ErrorAction Stop)) {
        throw "Benchmark result failed JSON Schema validation."
    }

    [IO.Directory]::CreateDirectory((Split-Path -Parent $FinalPath)) | Out-Null
    $stagingPath = Join-Path (Split-Path -Parent $FinalPath) ("." + [IO.Path]::GetFileName($FinalPath) + ".tmp")
    try {
        [IO.File]::WriteAllText($stagingPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $stagingPath -Destination $FinalPath
    }
    finally {
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Force
        }
    }
}

$manifestPath = (Resolve-Path $FixtureManifest).Path
$manifestText = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false, $true))
$manifest = $manifestText | ConvertFrom-Json

if ($manifest.sourceAssets.Count -ne 1) {
    throw "Stage 0 harness currently requires exactly one source asset."
}

$sourceSpec = $manifest.sourceAssets[0]
$sourcePath = Get-RelativeSourcePath -ManifestPath $manifestPath -RelativePath $sourceSpec.relativePath
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Fixture source does not exist: $sourcePath"
}

$sourceBefore = Get-Item -LiteralPath $sourcePath
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceHash -ne $sourceSpec.sha256) {
    throw "Fixture SHA-256 mismatch. Expected $($sourceSpec.sha256), got $sourceHash."
}
if ([int64]$sourceBefore.Length -ne [int64]$sourceSpec.byteSize) {
    throw "Fixture byte-size mismatch. Expected $($sourceSpec.byteSize), got $($sourceBefore.Length)."
}

$startedAt = (Get-Date).ToUniversalTime()
$runId = "{0}-{1}-{2}" -f $startedAt.ToString("yyyyMMddTHHmmssfffffffZ"), $Stage, ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$sourcePostRunIntegrityChecked = $false
$sourcePostRunSha256 = $null
$sourcePostRunByteSize = $null
$sourceUnchanged = $false

try {
    switch ($Stage) {
        "hardware" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "collect-hardware.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-HardwarePayload -Payload $payload
        }
        "ffmpeg-capabilities" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\inspect-ffmpeg.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-FfmpegPayload -Payload $payload
        }
        "media-ingest" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\inspect-media.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-MediaPayload -Payload $payload
        }
        "proxy-frame-map" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\build-proxy-frame-map.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ProxyFrameMapPayload -Payload $payload
        }
        "encoder-matrix" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-encoders.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-EncoderMatrixPayload -Payload $payload
        }
        "shot-detection" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-shots.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ShotDetectionPayload -Payload $payload
        }
        "face-detection" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-faces.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-FaceDetectionPayload -Payload $payload
        }
        "audio-event-and-recording-quality" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-audio-events.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-AudioEventPayload -Payload $payload
        }
        "process-and-package" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-process-and-package.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ProcessAndPackagePayload -Payload $payload
        }
        "asr-benchmark" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-asr.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-AsrPayload -Payload $payload
        }
        default {
            throw "Unsupported benchmark stage: $Stage"
        }
    }
    if ($Stage -eq "hardware" -and
        ([int64]$payload.sampleAsset.byteSize -ne [int64]$sourceSpec.byteSize -or $payload.sampleAsset.sha256 -ne $sourceSpec.sha256)) {
        throw "Benchmark payload does not describe the validated fixture source."
    }
    if ($Stage -eq "media-ingest" -and [int64]$payload.source.byteSize -ne [int64]$sourceSpec.byteSize) {
        throw "Media payload does not describe the validated fixture source."
    }
    $sourceAfter = Get-Item -LiteralPath $sourcePath
    $sourcePostRunByteSize = [int64]$sourceAfter.Length
    if ($sourceAfter.Length -ne $sourceBefore.Length -or $sourceAfter.LastWriteTimeUtc -ne $sourceBefore.LastWriteTimeUtc) {
        throw "The source fixture changed while the benchmark was running."
    }
    $sourcePostRunSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourcePostRunIntegrityChecked = $true
    $sourceUnchanged = $sourcePostRunSha256 -eq $sourceHash
    if (-not $sourceUnchanged) {
        throw "The source fixture content changed while the benchmark was running."
    }
}
catch {
    $stageError = $_
    try {
        $sourceAfterFailure = Get-Item -LiteralPath $sourcePath -ErrorAction Stop
        $sourcePostRunByteSize = [int64]$sourceAfterFailure.Length
        $sourcePostRunSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
        $sourcePostRunIntegrityChecked = $true
        $sourceUnchanged = $sourcePostRunByteSize -eq [int64]$sourceBefore.Length -and
            $sourceAfterFailure.LastWriteTimeUtc -eq $sourceBefore.LastWriteTimeUtc -and
            $sourcePostRunSha256 -eq $sourceHash
    }
    catch {
        $sourcePostRunIntegrityChecked = $false
        $sourceUnchanged = $false
    }
    $finishedAt = (Get-Date).ToUniversalTime()
    $safeMessage = Protect-SensitiveText -Text $stageError.Exception.Message -SourcePath $sourcePath
    $safeFailurePayload = Get-SafeFailurePayload -Payload $payload -Stage $Stage
    $failureResult = [ordered]@{
        schemaVersion = "1.0.0"
        runId = $runId
        stage = $Stage
        status = "failed"
        startedAtUtc = $startedAt.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        elapsedMilliseconds = [math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 3)
        fixture = [ordered]@{
            manifestPath = [IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $manifestPath).Replace('\', '/')
            manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            sourceAssetId = $sourceSpec.id
            sourceSha256 = $sourceHash
            sourceByteSize = [int64]$sourceBefore.Length
            sourcePostRunIntegrityChecked = $sourcePostRunIntegrityChecked
            sourcePostRunSha256 = $sourcePostRunSha256
            sourcePostRunByteSize = $sourcePostRunByteSize
            sourceUnchanged = $sourceUnchanged
        }
        payload = $safeFailurePayload
        error = [ordered]@{
            code = "BENCHMARK_STAGE_FAILED"
            message = $safeMessage
        }
    }
    $failurePath = Join-Path $OutputDirectory "$runId.json"
    Write-BenchmarkResult -Result $failureResult -FinalPath $failurePath
    throw "Benchmark stage failed. Machine-readable failure: $([IO.Path]::GetFileName($failurePath))"
}

$finishedAt = (Get-Date).ToUniversalTime()
$result = [ordered]@{
    schemaVersion = "1.0.0"
    runId = $runId
    stage = $Stage
    status = "succeeded"
    startedAtUtc = $startedAt.ToString("O")
    finishedAtUtc = $finishedAt.ToString("O")
    elapsedMilliseconds = [math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 3)
    fixture = [ordered]@{
        manifestPath = [IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $manifestPath).Replace('\', '/')
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceAssetId = $sourceSpec.id
        sourceSha256 = $sourceHash
        sourceByteSize = [int64]$sourceBefore.Length
        sourcePostRunIntegrityChecked = $sourcePostRunIntegrityChecked
        sourcePostRunSha256 = $sourcePostRunSha256
        sourcePostRunByteSize = $sourcePostRunByteSize
        sourceUnchanged = $sourceUnchanged
    }
    payload = $payload
    error = $null
}

$finalPath = Join-Path $OutputDirectory "$runId.json"
Write-BenchmarkResult -Result $result -FinalPath $finalPath

[ordered]@{
    resultPath = (Resolve-Path $finalPath).Path
    resultSha256 = (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash.ToLowerInvariant()
    runId = $runId
    status = "succeeded"
} | ConvertTo-Json
