#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet("hardware", "ffmpeg-capabilities", "media-ingest", "proxy-frame-map", "encoder-matrix", "shot-detection", "face-detection", "audio-event-and-recording-quality", "process-and-package", "asr-benchmark", "alignment-benchmark", "local-text-model", "manual-render")]
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

function Assert-NoUnexpectedSensitiveProperties {
    param([AllowNull()][object]$Value)

    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        "textSha256", "fixtureTextSha256", "referenceTextSha256", "recognizedTextSha256",
        "referenceCharacterCount", "recognizedCharacterCount",
        "transcriptTextIncludedInPayload", "sourceTranscriptTextIncludedInPayload",
        "sourceTranscriptTextIncludedInCanonicalSummary", "localTranscriptArtifactsContainRightsUnconfirmedText",
        "fixtureTranscriptHashes", "fixtureReferenceTranscriptAvailable", "transcriptReferenceAvailable",
        "missingReferenceRelativePath", "controlCerIsSyntheticMechanismEvidenceOnly",
        "fixtureProperNounErrorRateSubstitute", "pcmContentMatchesSource", "conditionOnPreviousText",
        "controlTranscriptionSeconds", "fixtureTranscriptionSeconds",
        "controlTranscriptionMilliseconds", "chunkTranscriptionMilliseconds",
        "recognizedTextSetSha256", "transcriptTextIncludedInSummary",
        "localArtifactsContainTranscriptText", "textHashesIdentical", "transcriptionMilliseconds",
        "transcriptionInputMode", "exactUtteranceBoundaryAccuracyMeasured",
        "utteranceTimingAndManualAdjustmentFallbackRequired", "contextLength"
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
                    throw "Benchmark payload contains an unapproved sensitive property name: $propertyName"
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
                throw "Benchmark payload contains an unapproved sensitive property name: $($property.Name)"
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
    Assert-NoUnexpectedSensitiveProperties -Value $Payload
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

function Assert-AlignmentPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "host", "implementation", "scope", "upstream", "tooling",
        "separatedSlotControl", "fixtureAnalysis", "timelineMapping", "annotations",
        "disposition", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Alignment payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported alignment payload schemaVersion: $($Payload.schemaVersion)"
    }
    Assert-NoUnexpectedSensitiveProperties -Value $Payload
    if ($Payload.host.hardwareBenchmarkRunSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.host.currentOperatingSystemVersion -ne $Payload.host.recordedOperatingSystemVersion) {
        throw "Alignment payload does not identify the current host."
    }
    $expectedImplementationFiles = @(
        @{ Id = "benchmark-harness"; FileName = "run-benchmarks.ps1"; Path = (Join-Path $PSScriptRoot "run-benchmarks.ps1") },
        @{ Id = "alignment-wrapper"; FileName = "measure-alignment.ps1"; Path = (Join-Path $PSScriptRoot "stages\measure-alignment.ps1") },
        @{ Id = "alignment-driver"; FileName = "alignment_benchmark.py"; Path = (Join-Path $PSScriptRoot "stages\alignment_benchmark.py") },
        @{ Id = "asr-driver"; FileName = "asr_benchmark.py"; Path = (Join-Path $PSScriptRoot "stages\asr_benchmark.py") },
        @{ Id = "benchmark-schema"; FileName = "schema.json"; Path = (Join-Path $PSScriptRoot "results\schema.json") },
        @{ Id = "alignment-control-config"; FileName = "alignment-control.json"; Path = (Join-Path $PSScriptRoot "fixtures\alignment-control.json") }
    )
    if (@($Payload.implementation.files).Count -ne $expectedImplementationFiles.Count) {
        throw "Alignment payload does not contain the exact implementation file set."
    }
    foreach ($expectedFile in $expectedImplementationFiles) {
        $records = @($Payload.implementation.files | Where-Object id -eq $expectedFile.Id)
        if ($records.Count -ne 1 -or $records[0].fileName -ne $expectedFile.FileName -or
            [int64]$records[0].byteSize -ne [int64](Get-Item -LiteralPath $expectedFile.Path).Length -or
            $records[0].sha256 -ne (Get-FileHash -LiteralPath $expectedFile.Path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Alignment implementation evidence does not match: $($expectedFile.Id)"
        }
    }
    if ($Payload.scope.benchmarkIsFullRequirementAcceptance -ne $false -or
        $Payload.scope.representativeGroundedJsonContractOnly -ne $true -or
        @($Payload.scope.fr006FieldsEvaluated).Count -lt 5 -or
        @($Payload.scope.fr006FieldsNotEvaluated).Count -lt 4 -or
        $Payload.scope.fr011MeaningPreservationMeasured -ne $false -or
        $Payload.scope.fr022AnswerSupportMeasured -ne $false -or
        $Payload.scope.fr025FullArtifactSetMeasured -ne $false -or
        $Payload.scope.fullFixtureSemanticCoverageMeasured -ne $false) {
        throw "Local text-model payload overstates its representative requirement coverage."
    }
    $asrCanonicalPath = Join-Path $PSScriptRoot "results\asr-benchmark.json"
    $asrRawPath = Join-Path $PSScriptRoot "results\runs\$($Payload.upstream.asrBenchmarkRunId).json"
    if (-not (Test-Path -LiteralPath $asrRawPath -PathType Leaf) -or
        $Payload.upstream.asrCanonical.sha256 -ne (Get-FileHash -LiteralPath $asrCanonicalPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.upstream.asrBenchmarkRunSha256 -ne (Get-FileHash -LiteralPath $asrRawPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "Alignment upstream canonical or raw file hash does not match the local evidence."
    }
    if ($Payload.upstream.asrBenchmarkRunId -ne "20260827T0718012939475Z-asr-benchmark-1e66cc2c" -or
        $Payload.upstream.asrBenchmarkRunSha256 -ne "a92491fd5c956151b335171413289b59c0d9d4098cf765501c463728786faf3c" -or
        $Payload.upstream.normalizedFixtureAudioSha256 -ne "25f0f5eb86b07afc1e875e8c9cb157c6c5008e3386d01f839b3cfc4696f09b9c" -or
        $Payload.upstream.cpuArtifact.sha256 -ne "715881eb1adc117659a2d2a41191ca57bcdd06f4dafb54b332646ff4119a8262" -or
        $Payload.upstream.cudaArtifact.sha256 -ne "8636826f918a42b4a9fc12e7b684bd18a021900b2ebc19189f74dfb4bd69776d" -or
        $Payload.upstream.privateEvidenceCompletionMarkerPresent -ne $true -or
        $Payload.upstream.privateEvidenceAcl.inheritanceProtected -ne $true -or
        [int]$Payload.upstream.privateEvidenceAcl.unexpectedAccessRuleCount -ne 0) {
        throw "Alignment payload does not preserve the immutable ST-006 evidence chain."
    }
    if ($Payload.tooling.pythonPackages.'faster-whisper' -ne "1.2.1" -or
        $Payload.tooling.pythonPackages.ctranslate2 -ne "4.8.1" -or
        [int]$Payload.tooling.pythonPackages.cudaDeviceCount -lt 1 -or
        $Payload.tooling.fasterWhisperModelRevision -ne "536b0662742c02347bc0e980a01041f333bce120" -or
        $Payload.tooling.fasterWhisperModelManifestSha256 -ne "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f" -or
        $Payload.tooling.cudaRuntimeManifestSha256 -ne "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021" -or
        $Payload.tooling.cudaRuntimePathInjectedOnlyForControlChild -ne $true -or
        $Payload.tooling.cudaRuntimeReleaseRedistributionApproved -ne $false) {
        throw "Alignment payload does not preserve the pinned runtime and distribution boundary."
    }
    $control = $Payload.separatedSlotControl
    if ([int]$control.slotCount -ne 4 -or
        $control.transcriptionInputMode -ne "independent-slot-clips-mapped-through-manifest-offsets" -or
        $control.constructedControlAudioWasTranscribed -ne $false -or
        $control.cpu.summary.status -ne "succeeded" -or
        $control.cuda.summary.status -ne "succeeded" -or
        $control.cpu.summary.transcriptionInputMode -ne $control.transcriptionInputMode -or
        $control.cuda.summary.transcriptionInputMode -ne $control.transcriptionInputMode -or
        $control.cpu.summary.constructedControlAudioWasTranscribed -ne $false -or
        $control.cuda.summary.constructedControlAudioWasTranscribed -ne $false -or
        $control.cpu.summary.allAssignedSlotsContainExactlyOneMappedTerm -ne $true -or
        $control.cuda.summary.allAssignedSlotsContainExactlyOneMappedTerm -ne $true -or
        $control.cpu.summary.allMappedTermsWithinAssignedSlots -ne $true -or
        $control.cuda.summary.allMappedTermsWithinAssignedSlots -ne $true -or
        $control.cpu.summary.allMappedTermsWithinSpeechClipRanges -ne $true -or
        $control.cuda.summary.allMappedTermsWithinSpeechClipRanges -ne $true -or
        $control.cpu.summary.timestampValidation.passed -ne $true -or
        $control.cuda.summary.timestampValidation.passed -ne $true -or
        @($control.cpu.summary.termWindowChecks).Count -ne 4 -or
        @($control.cuda.summary.termWindowChecks).Count -ne 4 -or
        @($control.cpu.summary.termWindowChecks | Where-Object { $_.assignedSlotOccurrenceCount -ne 1 -or $_.withinAssignedSlot -ne $true -or $_.withinSpeechClipRange -ne $true }).Count -ne 0 -or
        @($control.cuda.summary.termWindowChecks | Where-Object { $_.assignedSlotOccurrenceCount -ne 1 -or $_.withinAssignedSlot -ne $true -or $_.withinSpeechClipRange -ne $true }).Count -ne 0 -or
        $control.exactBoundaryAccuracyMeasured -ne $false -or
        $control.accuracyThresholdApproved -ne $false) {
        throw "Alignment separated-slot mechanism control did not pass without overclaiming accuracy."
    }
    $fixtureCandidates = @($Payload.fixtureAnalysis.candidates)
    if ($fixtureCandidates.Count -ne 2) {
        throw "Alignment fixture analysis must contain CPU and CUDA candidates."
    }
    foreach ($candidateId in @("faster-whisper-small-cpu-int8", "faster-whisper-small-cuda-float16")) {
        $candidate = $fixtureCandidates | Where-Object candidateId -eq $candidateId | Select-Object -First 1
        if ($null -eq $candidate -or
            $candidate.inputAudioSha256 -ne $Payload.upstream.normalizedFixtureAudioSha256 -or
            [double]$candidate.emittedWordTimestampCoverage -ne 1.0 -or
            [int]$candidate.segmentStructuralViolationCount -ne 0 -or
            [int]$candidate.wordStructuralViolationCount -ne 0 -or
            [double]$candidate.parentContainmentObservationThresholdSeconds -ne 0.001 -or
            $candidate.exactBoundaryAccuracyMeasured -ne $false -or
            $candidate.humanTimingReferenceAvailable -ne $false) {
            throw "Alignment fixture candidate lacks complete structural evidence: $candidateId"
        }
    }
    $agreement = $Payload.fixtureAnalysis.crossDeviceAgreement
    if ([int]$agreement.matchedExactTokenCount -le 0 -or
        [int]$agreement.startAbsoluteDifferenceSeconds.count -ne [int]$agreement.matchedExactTokenCount -or
        [int]$agreement.endAbsoluteDifferenceSeconds.count -ne [int]$agreement.matchedExactTokenCount -or
        [int]$agreement.startDifferenceDiagnosticCounts.'over0.1Seconds' -lt [int]$agreement.startDifferenceDiagnosticCounts.'over0.5Seconds' -or
        [int]$agreement.startDifferenceDiagnosticCounts.'over0.5Seconds' -lt [int]$agreement.startDifferenceDiagnosticCounts.'over1.0Seconds' -or
        [int]$agreement.startDifferenceDiagnosticCounts.'over0.1Seconds' -gt [int]$agreement.matchedExactTokenCount -or
        $agreement.startDifferenceDiagnosticCounts.thresholdsAreDescriptiveNotAcceptanceLimits -ne $true -or
        $agreement.duplicateTokenPairingAmbiguityPossible -ne $true -or
        $agreement.agreementIsAccuracyEvidence -ne $false) {
        throw "Alignment cross-device agreement evidence is incomplete or overclaimed."
    }
    if ($Payload.timelineMapping.firstDecodedSourceAudioFramePts -ne 0 -or
        [double]$Payload.timelineMapping.firstDecodedSourceAudioFrameSeconds -ne 0 -or
        [double]$Payload.timelineMapping.observedMinimumPacketPresentationSeconds -ne -0.044 -or
        $Payload.timelineMapping.exactSourceBoundaryErrorMeasured -ne $false) {
        throw "Alignment decoded-audio timeline mapping is inconsistent with ST-016 evidence."
    }
    if ($Payload.annotations.transcriptReferenceAvailable -ne $false -or
        $Payload.annotations.exactWordBoundaryAccuracyMeasured -ne $false -or
        $Payload.annotations.exactUtteranceBoundaryAccuracyMeasured -ne $false -or
        $Payload.annotations.crossDeviceAgreementIsAccuracyEvidence -ne $false -or
        $Payload.disposition.productionWordTimingApproved -ne $false -or
        $Payload.disposition.utteranceTimingAndManualAdjustmentFallbackRequired -ne $true -or
        $Payload.disposition.clampWordTimesToParentSegments -ne $false -or
        $Payload.disposition.crossDeviceOutputDivergence.selectionUsesOutputAccuracy -ne $false -or
        $Payload.disposition.crossDeviceOutputDivergence.timingAccuracyImplication -ne "unknown-without-independent-reference" -or
        $Payload.disposition.finalStage0DecisionPending -ne $true) {
        throw "Alignment payload unexpectedly claims timing accuracy or removes the manual fallback."
    }
    if ($Payload.privacy.sourceTranscriptTextIncludedInPayload -ne $false -or
        $Payload.privacy.sourceTranscriptTextIncludedInCanonicalSummary -ne $false -or
        $Payload.privacy.localArtifactsContainTranscriptText -ne $true -or
        $Payload.privacy.absoluteHostPathsIncludedInPayload -ne $false -or
        $Payload.privacy.privateUpstreamEvidenceRemainsIgnored -ne $true -or
        $Payload.privacy.workDirectoryWasEmptyAfterAclProtection -ne $true -or
        $Payload.privacy.localEvidenceAcl.inheritanceProtected -ne $true -or
        [int]$Payload.privacy.localEvidenceAcl.unexpectedAccessRuleCount -ne 0) {
        throw "Alignment payload violated the transcript privacy boundary."
    }
    $payloadJson = $Payload | ConvertTo-Json -Depth 30 -Compress
    if ($payloadJson -match '"(?:text|content|transcript|referenceText|spokenPhrase)"\s*:') {
        throw "Alignment payload contains transcript content."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Alignment payload did not confirm fixture path consumption."
    }
}

function Assert-LocalTextModelPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "host", "implementation", "upstream", "tooling",
        "analysisInput", "models", "evaluations", "comparison", "stateRestoration",
        "manualFallback", "disposition", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Local text-model payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported local text-model payload schemaVersion: $($Payload.schemaVersion)"
    }
    Assert-NoUnexpectedSensitiveProperties -Value $Payload
    if ($Payload.host.hardwareBenchmarkRunSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.host.currentOperatingSystemVersion -ne $Payload.host.recordedOperatingSystemVersion -or
        @($Payload.host.graphics).Count -lt 1) {
        throw "Local text-model payload does not identify the current measured host."
    }

    $expectedImplementationFiles = @(
        @{ Id = "benchmark-harness"; FileName = "run-benchmarks.ps1"; Path = (Join-Path $PSScriptRoot "run-benchmarks.ps1") },
        @{ Id = "local-text-wrapper"; FileName = "measure-text-model.ps1"; Path = (Join-Path $PSScriptRoot "stages\measure-text-model.ps1") },
        @{ Id = "local-text-driver"; FileName = "text_model_benchmark.py"; Path = (Join-Path $PSScriptRoot "stages\text_model_benchmark.py") },
        @{ Id = "benchmark-schema"; FileName = "schema.json"; Path = (Join-Path $PSScriptRoot "results\schema.json") },
        @{ Id = "local-text-output-schema"; FileName = "local-text-model-output.schema.json"; Path = (Join-Path $PSScriptRoot "fixtures\local-text-model-output.schema.json") },
        @{ Id = "local-text-control-config"; FileName = "local-text-model-control.json"; Path = (Join-Path $PSScriptRoot "fixtures\local-text-model-control.json") }
    )
    if ($Payload.implementation.gitHead -notmatch '^[0-9a-f]{40}$' -or
        @($Payload.implementation.files).Count -ne $expectedImplementationFiles.Count) {
        throw "Local text-model payload does not identify the exact implementation file set."
    }
    foreach ($expectedFile in $expectedImplementationFiles) {
        $records = @($Payload.implementation.files | Where-Object id -eq $expectedFile.Id)
        if ($records.Count -ne 1 -or $records[0].fileName -ne $expectedFile.FileName -or
            [int64]$records[0].byteSize -ne [int64](Get-Item -LiteralPath $expectedFile.Path).Length -or
            $records[0].sha256 -ne (Get-FileHash -LiteralPath $expectedFile.Path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Local text-model implementation evidence does not match: $($expectedFile.Id)"
        }
    }

    $asrCanonicalPath = Join-Path $PSScriptRoot "results\asr-benchmark.json"
    $asrRawPath = Join-Path $PSScriptRoot "results\runs\$($Payload.upstream.asrBenchmarkRunId).json"
    $alignmentCanonicalPath = Join-Path $PSScriptRoot "results\alignment-benchmark.json"
    if (-not (Test-Path -LiteralPath $asrRawPath -PathType Leaf) -or
        $Payload.upstream.asrCanonical.sha256 -ne (Get-FileHash -LiteralPath $asrCanonicalPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.upstream.asrBenchmarkRunSha256 -ne (Get-FileHash -LiteralPath $asrRawPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.upstream.alignmentCanonical.sha256 -ne (Get-FileHash -LiteralPath $alignmentCanonicalPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.upstream.asrBenchmarkRunId -ne "20260827T0718012939475Z-asr-benchmark-1e66cc2c" -or
        $Payload.upstream.asrBenchmarkRunSha256 -ne "a92491fd5c956151b335171413289b59c0d9d4098cf765501c463728786faf3c" -or
        $Payload.upstream.asrArtifact.sha256 -ne "8636826f918a42b4a9fc12e7b684bd18a021900b2ebc19189f74dfb4bd69776d" -or
        $Payload.upstream.asrPrivateEvidenceAcl.inheritanceProtected -ne $true -or
        [int]$Payload.upstream.asrPrivateEvidenceAcl.unexpectedAccessRuleCount -ne 0 -or
        $Payload.upstream.asrPrivateEvidenceCompletionMarkerPresent -ne $true -or
        $Payload.upstream.alignmentCanonical.benchmarkRunId -ne "20260827T0947132960205Z-alignment-benchmark-cf2b2ab6" -or
        $Payload.upstream.alignmentCanonical.benchmarkRunSha256 -ne "521c1de8a8a371bc17cec2ca2a84df5a1d1e6368dcb3eef955f753c97290ffa0") {
        throw "Local text-model payload does not preserve the immutable ASR/alignment evidence chain."
    }

    if ($Payload.tooling.pythonVersion -ne "Python 3.12.10" -or
        $Payload.tooling.pythonExternalPackagesRequired -ne $false -or
        $Payload.tooling.foundryLocal.available -ne $true -or
        $Payload.tooling.foundryLocal.cliVersion -ne "0.10.3" -or
        $Payload.tooling.foundryLocal.packageVersion -ne "0.10.3.0" -or
        $Payload.tooling.foundryLocal.cliExecutable.sha256 -ne "53a805895debd297bc5fe424bdbf64f551efbe0c53ddae397ecae9f5bcc7db98" -or
        $Payload.tooling.foundryLocal.daemonExecutable.sha256 -ne "a49c23d4af9f470da7128c29eef3fb1c0a64cf8555c2a3500e8683855886f3d1" -or
        $Payload.tooling.foundryLocal.preview -ne $true -or
        $Payload.tooling.foundryLocal.dynamicLoopbackEndpointUsed -ne $true -or
        $Payload.tooling.foundryLocal.endpointAddressIncludedInPayload -ne $false -or
        $Payload.tooling.foundryLocal.proxyBypassEnforcedByDriver -ne $true -or
        $Payload.tooling.cudaExecutionProvider.manifestSha256 -ne "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021" -or
        $Payload.tooling.cudaExecutionProvider.versionFileSha256 -ne "28236ce308e5232254f4f09ca8e6df0805b9a3608a79d9e4d09a4b459fb99640" -or
        $Payload.tooling.cudaExecutionProvider.releaseRedistributionApproved -ne $false) {
        throw "Local text-model payload does not identify the pinned preview runtime and redistribution boundary."
    }

    $controlConfigPath = Join-Path $PSScriptRoot "fixtures\local-text-model-control.json"
    $outputSchemaPath = Join-Path $PSScriptRoot "fixtures\local-text-model-output.schema.json"
    $fixtureInput = $Payload.analysisInput.fixture
    if ($Payload.analysisInput.sourceSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.analysisInput.samplePathReadOnlyHashValidated -ne $true -or
        $Payload.analysisInput.modelInputDerivedFromFixtureArtifact -ne $true -or
        $Payload.analysisInput.control.configSha256 -ne (Get-FileHash -LiteralPath $controlConfigPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.analysisInput.control.outputSchemaSha256 -ne (Get-FileHash -LiteralPath $outputSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        [int]$Payload.analysisInput.control.tokenCount -ne 19 -or
        $Payload.analysisInput.control.promptInjectionControlIncluded -ne $true -or
        $Payload.analysisInput.control.controlIsAccuracyEvidence -ne $false -or
        $fixtureInput.sourceCandidateId -ne "faster-whisper-small-cuda-float16" -or
        $fixtureInput.sourceArtifactSha256 -ne $Payload.upstream.asrArtifact.sha256 -or
        [int]$fixtureInput.fullFixtureTokenCount -ne 5266 -or
        [int]$fixtureInput.fullFixtureSegmentCount -le 0 -or
        @($fixtureInput.windows).Count -ne 3 -or
        [int]$fixtureInput.pairwiseWindowTokenOverlapCount -ne 0 -or
        $fixtureInput.windowsArePairwiseDisjoint -ne $true -or
        $fixtureInput.fullFixtureSemanticCoverageClaimed -ne $false) {
        throw "Local text-model input evidence is incomplete or overclaims fixture coverage."
    }

    $models = @($Payload.models)
    $expectedModels = @{
        "foundry-local-qwen3-0.6b-cuda" = @{
            ModelId = "qwen3-0.6b-cuda-gpu:2"; FileCount = 8; TotalBytes = 516135598
            Manifest = "38dc1ac88c6af4a684f96e8a9b4c30a6bca2671115a84e15189d93d57a10ebbe"
        }
        "foundry-local-qwen3-4b-cuda" = @{
            ModelId = "qwen3-4b-cuda-gpu:2"; FileCount = 9; TotalBytes = 2823706996
            Manifest = "89d232e91d2c130e5ffd38f03776ad47c3b9779c8689e565fd4b2ea483f6e657"
        }
    }
    if ($models.Count -ne 2) {
        throw "Local text-model payload must identify exactly two cached model variants."
    }
    foreach ($model in $models) {
        $expected = $expectedModels[[string]$model.candidateId]
        if ($null -eq $expected -or $model.modelId -ne $expected.ModelId -or
            $model.publisher -ne "Microsoft" -or $model.licenseDeclaredByCatalog -ne "apache-2.0" -or
            $model.independentDistributionLicenseReviewCompleted -ne $false -or
            [int]$model.contextLength -ne 40960 -or
            $model.supportsToolCallingDeclaredByCatalog -ne $true -or
            $model.catalogCapabilityIsNotPerRequestSuccessEvidence -ne $true -or
            $model.executionProvider -ne "CUDAExecutionProvider" -or $model.cachedBeforeRun -ne $true -or
            [int]$model.fileCount -ne $expected.FileCount -or [int64]$model.totalBytes -ne $expected.TotalBytes -or
            $model.manifestSha256 -ne $expected.Manifest -or @($model.files).Count -ne $expected.FileCount) {
            throw "Local text-model payload contains an unexpected model manifest or catalog claim."
        }
    }

    $evaluations = @($Payload.evaluations)
    if ($evaluations.Count -ne 2) {
        throw "Local text-model payload must contain two candidate evaluations."
    }
    $expectedDatasetIds = @("synthetic-control", "fixture-start", "fixture-middle", "fixture-end")
    foreach ($evaluation in $evaluations) {
        if ($expectedModels.Keys -notcontains [string]$evaluation.candidateId -or
            $evaluation.semanticGroundednessMeasured -ne $false -or
            $evaluation.generatedWordingAccuracyMeasured -ne $false -or
            $evaluation.productionThresholdApproved -ne $false) {
            throw "Local text-model candidate overstates semantic or production approval."
        }
        if ($evaluation.status -eq "model-load-failed") {
            if ($evaluation.load.attempted -ne $true -or $evaluation.load.succeeded -ne $false -or
                @($evaluation.datasets).Count -ne 0 -or $evaluation.structurallyEligibleForHumanReview -ne $false) {
                throw "Local text-model load failure evidence is internally inconsistent."
            }
            continue
        }
        if ($evaluation.status -ne "evaluated" -or $evaluation.load.succeeded -ne $true -or
            $evaluation.load.isolatedFromPreloadedModels -ne $true -or
            [int]$evaluation.load.processMeasurement.observedProcessIdCount -lt 2 -or
            @($evaluation.datasets).Count -ne 4) {
            throw "Local text-model candidate did not record an isolated load and four datasets."
        }
        $datasetIds = @($evaluation.datasets | ForEach-Object { $_.datasetId })
        if (@($datasetIds | Sort-Object -Unique).Count -ne 4 -or
            @($expectedDatasetIds | Where-Object { $_ -notin $datasetIds }).Count -ne 0) {
            throw "Local text-model candidate dataset set is incomplete."
        }
        foreach ($dataset in @($evaluation.datasets)) {
            if ($dataset.invocation.generatedOutputIncludedInSummary -ne $false -or
                [int]$dataset.processMeasurement.observedProcessIdCount -lt 2 -or
                [int]$dataset.processMeasurement.gpuMemorySamplingIntervalMilliseconds -ne 1000 -or
                [int64]$dataset.processMeasurement.peakAggregateWorkingSetBytes -lt [int64]$dataset.processMeasurement.baselineAggregateWorkingSetBytes) {
                throw "Local text-model invocation leaks output or lacks process evidence."
            }
            if ($dataset.invocation.status -eq "succeeded") {
                if ($dataset.invocation.responseModelMatches -ne $true -or
                    $dataset.invocation.generatedOutputArtifact.sha256 -notmatch '^[0-9a-f]{64}$' -or
                    ($dataset.invocation.tokenCountEndpointAvailable -eq $true -and [int]$dataset.invocation.tokenCountEndpointPromptTokens -lt 1) -or
                    ($dataset.invocation.tokenCountEndpointAvailable -eq $false -and $null -ne $dataset.invocation.tokenCountEndpointPromptTokens) -or
                    $null -eq $dataset.invocation.responseShape -or
                    $null -eq $dataset.validation -or
                    $dataset.validation.generatedOutputIncludedInSummary -ne $false -or
                    $dataset.validation.humanSemanticReferenceAvailable -ne $false -or
                    $dataset.validation.semanticGroundednessMeasured -ne $false -or
                    $dataset.validation.generatedWordingAccuracyMeasured -ne $false -or
                    $dataset.validation.productionThresholdApproved -ne $false) {
                    throw "A successful local text-model invocation lacks bounded structural validation."
                }
            }
            else {
                if ($dataset.invocation.error.code -notmatch '^[A-Z0-9_]+$' -or $null -ne $dataset.validation) {
                    throw "A failed local text-model invocation lacks a bounded error record."
                }
                if ($dataset.invocation.error.code -match '^(?:GENERATED_|BOUNDED_|UNTERMINATED_)') {
                    if ($dataset.invocation.responseModelMatches -ne $true -or
                        [string]::IsNullOrWhiteSpace([string]$dataset.invocation.finishReason) -or
                        $dataset.invocation.responseArtifact.sha256 -notmatch '^[0-9a-f]{64}$' -or
                        $null -eq $dataset.invocation.usage -or
                        $null -eq $dataset.invocation.responseShape) {
                        throw "A generated-output failure omitted its safe response shape and usage evidence."
                    }
                }
            }
        }
        $control = $evaluation.datasets | Where-Object datasetId -eq "synthetic-control" | Select-Object -First 1
        $fixtureDatasets = @($evaluation.datasets | Where-Object inputKind -eq "rights-unconfirmed-fixture-window")
        $expectedEligibility = $null -ne $control.validation -and
            $control.validation.controlMechanismPassed -eq $true -and
            $fixtureDatasets.Count -eq 3 -and
            @($fixtureDatasets | Where-Object { $null -ne $_.validation -and $_.validation.structuralValidationPassed -eq $true }).Count -eq 3
        if ($evaluation.controlMechanismPassed -ne ($null -ne $control.validation -and $control.validation.controlMechanismPassed -eq $true) -or
            $evaluation.allFixtureWindowsStructurallyValid -ne ($fixtureDatasets.Count -eq 3 -and @($fixtureDatasets | Where-Object { $null -ne $_.validation -and $_.validation.structuralValidationPassed -eq $true }).Count -eq 3) -or
            $evaluation.structurallyEligibleForHumanReview -ne $expectedEligibility) {
            throw "Local text-model candidate aggregate flags contradict dataset evidence."
        }
        if ($null -ne $control.validation -and
            (@($control.validation.controlExpectedTopicRanges).Count -ne 3 -or
                @($control.validation.controlObservedTopicRanges).Count -ne [int]$control.validation.counts.topics -or
                $control.validation.controlInjectionMarkerAbsenceIsRobustnessEvidence -ne $false)) {
            throw "Local text-model control omitted safe observed-ID diagnostics or overstated injection robustness."
        }
    }

    $eligible = @($evaluations | Where-Object { $_.structurallyEligibleForHumanReview -eq $true })
    $successfulInvocations = @($evaluations | ForEach-Object { @($_.datasets) } | Where-Object { $_.invocation.status -eq "succeeded" })
    if ([int]$Payload.comparison.candidateCount -ne 2 -or
        [int]$Payload.comparison.datasetCountPerEvaluatedCandidate -ne 4 -or
        [int]$Payload.comparison.successfulInvocationCount -ne $successfulInvocations.Count -or
        [int]$Payload.comparison.structurallyEligibleCandidateCount -ne $eligible.Count -or
        @($Payload.comparison.structurallyEligibleCandidateIds).Count -ne $eligible.Count -or
        [int]$Payload.comparison.performanceTrialCountPerModelAndDataset -ne 1 -or
        $Payload.comparison.performanceDistributionMeasured -ne $false -or
        $Payload.comparison.toolCallingDeclaredByCatalogButNotUsedAsSchemaGuarantee -ne $true -or
        $Payload.comparison.controlInjectionMarkerAbsenceIsRobustnessEvidence -ne $false -or
        $Payload.comparison.generatedOutputSchemaValidationIsSemanticGroundednessEvidence -ne $false -or
        $Payload.comparison.groundedIdExistenceAndRangeValidationIsSemanticGroundednessEvidence -ne $false -or
        $Payload.comparison.fixtureHumanSemanticReferenceAvailable -ne $false -or
        $Payload.comparison.semanticGroundednessMeasured -ne $false -or
        $Payload.comparison.generatedWordingAccuracyMeasured -ne $false -or
        $Payload.comparison.productionThresholdsApproved -ne $false) {
        throw "Local text-model comparison overstates or contradicts the measured evidence."
    }
    if ($Payload.stateRestoration.preloadedModelCount -ne 0 -or
        $Payload.stateRestoration.loadedModelCountAfter -ne 0 -or
        $Payload.stateRestoration.loadedModelSetHashBefore -ne $Payload.stateRestoration.loadedModelSetHashAfter -or
        $Payload.stateRestoration.preAndPostStateMatch -ne $true -or
        $Payload.stateRestoration.eachCandidateUnloadedBeforeNextCandidate -ne $true) {
        throw "Foundry Local state was not restored after ST-011."
    }
    if ($Payload.manualFallback.required -ne $true -or
        $Payload.manualFallback.automaticFallbackExecutedByBenchmark -ne $false -or
        $Payload.manualFallback.manualEntryPathSpecifiedInImplementationPlan -ne $true -or
        $Payload.manualFallback.manualEntryPathImplementedByBenchmark -ne $false -or
        $Payload.manualFallback.manualEntryPathExecutedByBenchmark -ne $false -or
        @($Payload.manualFallback.triggerConditions).Count -lt 5 -or
        $Payload.manualFallback.generatedSuggestionsAutomaticallyAdopted -ne $false -or
        $Payload.disposition.localModelAdoptedForProduction -ne $false -or
        $Payload.disposition.candidateOutputsAreUnapprovedSuggestions -ne $true -or
        $Payload.disposition.generatedSuggestionsAutomaticallyAdopted -ne $false -or
        $Payload.disposition.semanticGroundednessMeasured -ne $false -or
        $Payload.disposition.finalStage0DecisionPending -ne $true) {
        throw "Local text-model payload removed the required human fallback or review boundary."
    }
    if ($Payload.privacy.sourceTranscriptTextIncludedInPayload -ne $false -or
        $Payload.privacy.sourceTranscriptTextIncludedInCanonicalSummary -ne $false -or
        $Payload.privacy.localArtifactsContainTranscriptText -ne $true -or
        $Payload.privacy.localArtifactsContainGeneratedOutput -ne $true -or
        $Payload.privacy.modelInputPlacedOnProcessCommandLine -ne $false -or
        $Payload.privacy.restEndpointWasLoopbackOnly -ne $true -or
        $Payload.privacy.proxyBypassEnforced -ne $true -or
        $Payload.privacy.daemonLogScan.scanned -ne $true -or
        $Payload.privacy.daemonLogScan.exactInputOrGeneratedProbeLeakObserved -ne $false -or
        [int]$Payload.privacy.daemonLogScan.requestCanaryMatchCount -ne 0 -or
        [int]$Payload.privacy.daemonLogScan.privateProbeMatchCount -ne 0 -or
        $Payload.privacy.daemonLogAclControlledByBenchmark -ne $false -or
        $Payload.privacy.absoluteHostPathsIncludedInPayload -ne $false -or
        $Payload.privacy.privateEvidenceRemainsIgnored -ne $true -or
        $Payload.privacy.workDirectoryWasEmptyAfterAclProtection -ne $true -or
        $Payload.privacy.localEvidenceAcl.inheritanceProtected -ne $true -or
        [int]$Payload.privacy.localEvidenceAcl.unexpectedAccessRuleCount -ne 0) {
        throw "Local text-model payload violated the source/generated wording privacy boundary."
    }
    $payloadJson = $Payload | ConvertTo-Json -Depth 30 -Compress
    if ($payloadJson -match '"(?:text|content|transcript|utterance|rawResponse|prompt)"\s*:') {
        throw "Local text-model payload contains a forbidden content-bearing property."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Local text-model payload did not confirm fixture path consumption."
    }
}

function Assert-ManualRenderPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "host", "implementation", "upstream", "manifest",
        "tooling", "sourceTiming", "renders", "reproducibility", "visualAndReverseMapping",
        "audioMechanism", "assessment", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Manual-render payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported manual-render payload schemaVersion: $($Payload.schemaVersion)"
    }
    $expectedImplementationFiles = @(
        @{ Id = "benchmark-harness"; FileName = "run-benchmarks.ps1"; Path = (Join-Path $PSScriptRoot "run-benchmarks.ps1") },
        @{ Id = "manual-render-wrapper"; FileName = "measure-manual-render.ps1"; Path = (Join-Path $PSScriptRoot "stages\measure-manual-render.ps1") },
        @{ Id = "manual-render-driver"; FileName = "manual_render_benchmark.py"; Path = (Join-Path $PSScriptRoot "stages\manual_render_benchmark.py") },
        @{ Id = "benchmark-schema"; FileName = "schema.json"; Path = (Join-Path $PSScriptRoot "results\schema.json") },
        @{ Id = "manual-render-manifest"; FileName = "manual-render-manifest.json"; Path = (Join-Path $PSScriptRoot "fixtures\manual-render-manifest.json") },
        @{ Id = "manual-render-subtitle"; FileName = "manual-render-subtitles.ass"; Path = (Join-Path $PSScriptRoot "fixtures\manual-render-subtitles.ass") }
    )
    if ($Payload.implementation.gitHead -notmatch '^[0-9a-f]{40}$' -or
        @($Payload.implementation.files).Count -ne $expectedImplementationFiles.Count) {
        throw "Manual-render payload does not identify the exact implementation file set."
    }
    foreach ($expectedFile in $expectedImplementationFiles) {
        $records = @($Payload.implementation.files | Where-Object id -eq $expectedFile.Id)
        if ($records.Count -ne 1 -or $records[0].fileName -ne $expectedFile.FileName -or
            [int64]$records[0].byteSize -ne [int64](Get-Item -LiteralPath $expectedFile.Path).Length -or
            $records[0].sha256 -ne (Get-FileHash -LiteralPath $expectedFile.Path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Manual-render implementation evidence does not match: $($expectedFile.Id)"
        }
    }
    $upstreamFiles = @{
        mediaIngest = Join-Path $PSScriptRoot "results\media-ingest.json"
        proxyFrameMap = Join-Path $PSScriptRoot "results\proxy-frame-map.json"
        encoderMatrix = Join-Path $PSScriptRoot "results\encoder-matrix.json"
        ffmpegCapabilities = Join-Path $PSScriptRoot "results\ffmpeg-capabilities.json"
    }
    foreach ($property in $upstreamFiles.Keys) {
        if ($Payload.upstream.$property.sha256 -ne (Get-FileHash -LiteralPath $upstreamFiles[$property] -Algorithm SHA256).Hash.ToLowerInvariant() -or
            $Payload.upstream.$property.benchmarkRunSha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Manual-render upstream evidence does not match: $property"
        }
    }
    $manifestPath = Join-Path $PSScriptRoot "fixtures\manual-render-manifest.json"
    $subtitlePath = Join-Path $PSScriptRoot "fixtures\manual-render-subtitles.ass"
    if ($Payload.manifest.manifestId -ne "st-012-manual-render-v1" -or
        $Payload.manifest.descriptor.sha256 -ne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.manifest.subtitle.sha256 -ne (Get-FileHash -LiteralPath $subtitlePath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $Payload.manifest.validation.allRequiredOperationsDeclared -ne $true -or
        $Payload.manifest.validation.verticalCropExactNineBySixteen -ne $true -or
        $Payload.manifest.validation.sourceTextUsedBySubtitle -ne $false -or
        $Payload.manifest.rights.externalPublicationAllowed -ne $false) {
        throw "Manual-render handwritten manifest evidence is incomplete or changed."
    }
    if ($Payload.tooling.ffmpegSha256 -ne "6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb" -or
        $Payload.tooling.ffprobeSha256 -ne "4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2" -or
        $Payload.tooling.videoEncoder -ne "h264_nvenc" -or $Payload.tooling.audioEncoder -ne "aac" -or
        [string]::IsNullOrWhiteSpace([string]$Payload.tooling.pythonPackages.opencv) -or
        [string]::IsNullOrWhiteSpace([string]$Payload.tooling.pythonPackages.numpy)) {
        throw "Manual-render payload does not preserve the approved toolchain."
    }
    if ($Payload.sourceTiming.authority -ne "observed-source-presentation-timestamps" -or
        $Payload.sourceTiming.timeBase -ne "1/600" -or [int]$Payload.sourceTiming.packetCount -ne 62316 -or
        [double]$Payload.sourceTiming.maximumObservedFrameDurationSeconds -ne 0.635 -or
        $Payload.sourceTiming.packetPtsArtifact.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.sourceTiming.nominalFrameRateUsedForSourceMapping -ne $false) {
        throw "Manual-render payload did not use the observed source PTS authority."
    }

    $renders = @($Payload.renders)
    if ($renders.Count -ne 4) {
        throw "Manual-render payload must contain two runs for each of two compositions."
    }
    $expectedProfiles = @{
        "landscape-trim-concat-subtitle-audio" = @{ Profile = "landscape-1280x720"; Width = 1280; Height = 720; Operations = @("trim", "concat", "scale", "subtitle", "audio-gain") }
        "vertical-trim-center-crop" = @{ Profile = "vertical-1080x1920"; Width = 1080; Height = 1920; Operations = @("trim", "crop") }
    }
    foreach ($compositionId in $expectedProfiles.Keys) {
        $records = @($renders | Where-Object compositionId -eq $compositionId | Sort-Object iteration)
        $expected = $expectedProfiles[$compositionId]
        if ($records.Count -ne 2 -or @($records | ForEach-Object iteration) -join ',' -ne '1,2') {
            throw "Manual-render composition does not contain exactly two iterations: $compositionId"
        }
        foreach ($record in $records) {
            if ($record.profileId -ne $expected.Profile -or
                @($record.operations).Count -ne $expected.Operations.Count -or
                @($expected.Operations | Where-Object { $_ -notin @($record.operations) }).Count -ne 0 -or
                $record.output.sha256 -notmatch '^[0-9a-f]{64}$' -or [int64]$record.output.byteSize -le 0 -or
                $record.process.exitCode -ne 0 -or $record.fullDecode.succeeded -ne $true -or $record.fullDecode.exitCode -ne 0 -or
                $record.probe.video.codecName -ne "h264" -or $record.probe.video.width -ne $expected.Width -or
                $record.probe.video.height -ne $expected.Height -or $record.probe.video.pixelFormat -ne "yuv420p" -or
                $record.probe.video.frameRate -ne "30000/1001" -or $record.probe.video.decodedFrameCount -le 0 -or
                $record.probe.audio.codecName -ne "aac" -or $record.probe.audio.sampleRate -ne 48000 -or
                $record.probe.audio.channels -ne 2 -or $record.probe.audio.decodedFrameCount -le 0 -or
                $record.probe.audioVideoEndDifferenceSeconds -gt 0.1 -or
                $record.decodedVideoSha256 -notmatch '^[0-9a-f]{64}$' -or
                $record.decodedAudioSha256 -notmatch '^[0-9a-f]{64}$' -or
                $record.commandArgumentsIncludedInPayload -ne $false) {
                throw "Manual-render output failed its profile, decode, hash, or command privacy contract."
            }
        }
    }
    $reproducibility = @($Payload.reproducibility)
    if ($reproducibility.Count -ne 2 -or
        @($reproducibility | Where-Object {
            $_.runsCompared -ne 2 -or
            $_.sameMachineGpuDriverObservation -ne $true -or
            $_.crossMachineOrDriverReproducibilityMeasured -ne $false -or
            $_.byteIdentityRequiredForTaskCompletion -ne $false
        }).Count -ne 0) {
        throw "Manual-render reproducibility observations are incomplete."
    }
    $visual = @($Payload.visualAndReverseMapping)
    if ($visual.Count -ne 2 -or
        @($visual | Where-Object {
            $_.mappingAuthority -ne "observed-output-and-source-presentation-timestamps-plus-handwritten-piecewise-manifest" -or
            [int]$_.sourcePtsCount -ne 62316 -or [int]$_.outputPtsCount -le 0 -or [int]$_.sampleCount -ne 2 -or
            @($_.samples).Count -ne 2 -or $_.allSourceFrameSelectionErrorsWithinObservedMaximumDuration -ne $true -or
            $_.allOffsetControlsExceedMappedDifferences -ne $true -or
            $_.visualControlIsContentCorrespondenceNotQualityScore -ne $true -or
            $_.sourcePixelsIncludedInSummary -ne $false -or $_.productionThresholdsApproved -ne $false
        }).Count -ne 0) {
        throw "Manual-render reverse-map or visual-control evidence did not pass."
    }
    $subtitleControls = @($visual | Where-Object { $null -ne $_.subtitleControl })
    if ($subtitleControls.Count -ne 1 -or
        $subtitleControls[0].subtitleControl.activeDifferenceExceedsInactive -ne $true -or
        $subtitleControls[0].subtitleControl.subtitleTextContentIncludedInSummary -ne $false) {
        throw "Manual-render subtitle mechanism control did not pass."
    }
    if ($Payload.audioMechanism.status -ne "succeeded" -or
        $Payload.audioMechanism.linearGain -ne 0.5 -or
        $Payload.audioMechanism.gainMechanismWithinStage0Tolerance -ne $true -or
        $Payload.audioMechanism.outputIsNotSilent -ne $true -or
        $Payload.audioMechanism.toleranceIsProductionApproved -ne $false -or
        $Payload.audioMechanism.audioSamplesIncludedInSummary -ne $false) {
        throw "Manual-render audio mechanism evidence did not pass."
    }
    if ($Payload.assessment.requiredOperationsExercised -ne $true -or
        $Payload.assessment.landscapeAndVerticalOutputsProduced -ne $true -or
        $Payload.assessment.allOutputsPassedFullDecode -ne $true -or
        $Payload.assessment.allReverseMapSamplesWithinObservedFrameDuration -ne $true -or
        $Payload.assessment.allVisualControlsSeparate -ne $true -or
        $Payload.assessment.subtitleMechanismPassed -ne $true -or
        $Payload.assessment.subtitleSourceSpeechTraceabilityMeasured -ne $false -or
        $Payload.assessment.contentLevelOriginRefTraceabilityMeasured -ne $false -or
        $Payload.assessment.audioGainMechanismPassed -ne $true -or
        $Payload.assessment.perceptualQualityMeasured -ne $false -or
        $Payload.assessment.productionThresholdsApproved -ne $false -or
        $Payload.assessment.finalStage0DecisionPending -ne $true) {
        throw "Manual-render aggregate assessment contradicts the measured evidence."
    }
    if ($Payload.privacy.localEvidenceAcl.inheritanceProtected -ne $true -or
        [int]$Payload.privacy.localEvidenceAcl.unexpectedAccessRuleCount -ne 0 -or
        $Payload.privacy.sourceDerivedVideoArtifactsPersistedOnlyInIgnoredWorkDirectory -ne $true -or
        $Payload.privacy.sourcePixelsIncludedInPayload -ne $false -or
        $Payload.privacy.sourceAudioSamplesIncludedInPayload -ne $false -or
        $Payload.privacy.sourceTranscriptTextIncludedInPayload -ne $false -or
        $Payload.privacy.sourceMetadataTagsCopiedToOutputs -ne $false -or
        $Payload.privacy.absoluteHostPathsIncludedInPayload -ne $false -or
        $Payload.privacy.externalPublicationAllowed -ne $false -or
        $Payload.privacy.commandArgumentsIncludedInPayload -ne $false -or
        $Payload.privacy.workDirectoryWasEmptyAfterAclProtection -ne $true) {
        throw "Manual-render payload violated the local evidence or rights boundary."
    }
    $payloadJson = $Payload | ConvertTo-Json -Depth 30 -Compress
    if ($payloadJson -match '[A-Za-z]:\\' -or $payloadJson -match '"(?:sourcePath|outputPath|command|stderr)"\s*:') {
        throw "Manual-render payload contains an absolute path or forbidden process property."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Manual-render payload did not confirm fixture path consumption."
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

    if ($Stage -in @("asr-benchmark", "alignment-benchmark", "local-text-model", "manual-render")) {
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
        "alignment-benchmark" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-alignment.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-AlignmentPayload -Payload $payload
        }
        "local-text-model" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-text-model.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-LocalTextModelPayload -Payload $payload
        }
        "manual-render" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-manual-render.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ManualRenderPayload -Payload $payload
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
