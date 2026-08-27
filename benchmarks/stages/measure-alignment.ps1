#Requires -Version 7.0

<#
.SYNOPSIS
Evaluates Japanese ASR timestamp structure and cross-device agreement.

.DESCRIPTION
Uses the immutable ST-006 CPU/CUDA private artifacts for full-fixture analysis and runs a short,
separated-slot Japanese TTS control through the same pinned faster-whisper model. Payload output
contains hashes, counts, aggregate differences, and capability flags only. It does not claim exact
word-boundary accuracy without an independently authored timing reference.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 1800
$fasterModelRevision = "536b0662742c02347bc0e980a01041f333bce120"
$fasterModelManifestSha256 = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"
$cudaRuntimeManifestSha256 = "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021"
$cudaRuntimeFileCount = 14
$cudaRuntimeTotalBytes = 2423764787
$manifestRule = "SHA-256 of UTF-8 sorted fileName NUL byteSize NUL fileSha256 LF records"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StringSha256 {
    param([Parameter(Mandatory)][string]$Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-FileDescriptor {
    param([Parameter(Mandatory)][string]$Path)

    $file = Get-Item -LiteralPath $Path
    return [pscustomobject][ordered]@{
        fileName = $file.Name
        byteSize = [int64]$file.Length
        sha256 = Get-FileSha256 -Path $file.FullName
    }
}

function Write-LocalTextAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text
    )

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $staging = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($staging, $Text, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $staging -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
    }
}

function Get-LocalEvidenceAclEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for alignment evidence protection."
    }
    $allowedSids = @(
        $currentSid,
        [Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    )
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = Get-Acl -LiteralPath $Path
    $allowedValues = @($allowedSids | ForEach-Object { $_.Value })
    $unexpected = @($acl.Access | Where-Object {
        $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedValues -or
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl -or
        ($_.InheritanceFlags -band $inheritance) -ne $inheritance
    })
    $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected -or $ownerSid -ne $currentSid.Value -or
        $unexpected.Count -ne 0 -or @($acl.Access).Count -ne 3) {
        throw "The alignment evidence directory ACL did not match the approved local principals."
    }
    return [pscustomobject][ordered]@{
        inheritanceProtected = $true
        approvedPrincipalCount = 3
        accessRuleCount = @($acl.Access).Count
        unexpectedAccessRuleCount = $unexpected.Count
        ownerIsCurrentPrincipal = $true
        approvedRulesGrantFullControl = $true
        principalIdentifiersIncludedInPayload = $false
    }
}

function Protect-LocalEvidenceDirectory {
    param([Parameter(Mandatory)][string]$Path)

    try {
        return Get-LocalEvidenceAclEvidence -Path $Path
    }
    catch {
        Write-Verbose "The alignment evidence directory requires ACL normalization."
    }
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for alignment evidence protection."
    }
    $allowedSids = @(
        $currentSid,
        [Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    )
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($currentSid)
    foreach ($sid in $allowedSids) {
        [void]$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        ))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    return Get-LocalEvidenceAclEvidence -Path $Path
}

function Get-DirectoryManifest {
    param(
        [Parameter(Mandatory)][string]$Root,
        [switch]$Recursive
    )

    $files = if ($Recursive) {
        @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName)
    }
    else {
        @(Get-ChildItem -LiteralPath $Root -File | Sort-Object Name)
    }
    $records = [Text.StringBuilder]::new()
    $descriptors = @($files | ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $sha256 = Get-FileSha256 -Path $_.FullName
        [void]$records.Append($relativePath).Append([char]0).Append($_.Length).Append([char]0).Append($sha256).Append("`n")
        [pscustomobject][ordered]@{
            relativePath = $relativePath
            byteSize = [int64]$_.Length
            sha256 = $sha256
        }
    })
    return [pscustomobject][ordered]@{
        fileCount = $descriptors.Count
        totalBytes = [int64](($files | Measure-Object Length -Sum).Sum)
        manifestRule = $manifestRule
        manifestSha256 = Get-StringSha256 -Text $records.ToString()
        files = $descriptors
    }
}

function Get-DescendantProcessId {
    param([Parameter(Mandatory)][int]$RootProcessId)

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
    $descendants = [Collections.Generic.HashSet[int]]::new()
    $queue = [Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootProcessId)
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        foreach ($child in $processes | Where-Object { [int]$_.ParentProcessId -eq $parent }) {
            $childId = [int]$child.ProcessId
            if ($descendants.Add($childId)) {
                $queue.Enqueue($childId)
            }
        }
    }
    return @($descendants)
}

function Invoke-ProcessTreeTermination {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($Process.Id)
    foreach ($processId in Get-DescendantProcessId -RootProcessId $Process.Id) {
        [void]$ids.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    foreach ($processId in $ids) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit(10000)) {
                throw "A timed-out alignment process remained alive."
            }
        }
        finally {
            $observed.Dispose()
        }
    }
}

function Measure-AggregateWorkingSet {
    param([Parameter(Mandatory)][AllowEmptyCollection()][int[]]$ProcessIds)

    [int64]$total = 0
    foreach ($processId in @($ProcessIds | Sort-Object -Unique)) {
        try {
            $process = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                $total += [int64]$process.WorkingSet64
            }
        }
        catch [InvalidOperationException] {
            continue
        }
        finally {
            $process.Dispose()
        }
    }
    return $total
}

function Invoke-MeasuredProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [string]$WorkingDirectory,
        [hashtable]$EnvironmentVariables = @{}
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
    foreach ($name in $EnvironmentVariables.Keys) {
        $startInfo.Environment[[string]$name] = [string]$EnvironmentVariables[$name]
    }
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    [int64]$baselineWorkingSet = 0
    [int64]$peakWorkingSet = 0
    try {
        if (-not $process.Start()) {
            throw "Failed to start $([IO.Path]::GetFileName($FileName))."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $observedIds = [Collections.Generic.HashSet[int]]::new()
        [void]$observedIds.Add($process.Id)
        $treeRefresh = [Diagnostics.Stopwatch]::StartNew()
        $baselineWorkingSet = Measure-AggregateWorkingSet -ProcessIds @($observedIds)
        $peakWorkingSet = $baselineWorkingSet
        while (-not $process.WaitForExit(250)) {
            if ($treeRefresh.ElapsedMilliseconds -ge 1000) {
                foreach ($processId in Get-DescendantProcessId -RootProcessId $process.Id) {
                    [void]$observedIds.Add($processId)
                }
                $treeRefresh.Restart()
            }
            $workingSet = Measure-AggregateWorkingSet -ProcessIds @($observedIds)
            if ($workingSet -gt $peakWorkingSet) {
                $peakWorkingSet = $workingSet
            }
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Invoke-ProcessTreeTermination -Process $process
                throw "$([IO.Path]::GetFileName($FileName)) exceeded the alignment timeout of $TimeoutSeconds seconds."
            }
        }
        $process.WaitForExit()
        $stopwatch.Stop()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
            elapsedMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            baselineAggregateWorkingSetBytes = $baselineWorkingSet
            peakAggregateWorkingSetBytes = $peakWorkingSet
            workingSetScope = "root process and dynamically discovered descendants"
            observedProcessIdCount = $observedIds.Count
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            Invoke-ProcessTreeTermination -Process $process
        }
        $process.Dispose()
    }
}

function ConvertFrom-LastJsonLine {
    param([Parameter(Mandatory)][string]$Text)

    $lines = @($Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        try {
            return $lines[$index] | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }
    }
    throw "Alignment process output did not contain a JSON object."
}

function Find-ApprovedExecutable {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$ExecutableName,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $packagesRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    $packageDirectory = Get-ChildItem $packagesRoot -Directory -Filter "$PackageId*" -ErrorAction Stop |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    $approvedPaths = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName) {
        if ((Get-FileSha256 -Path $file.FullName) -eq $ExpectedSha256) {
            $approvedPaths.Add($file.FullName)
        }
    }
    if ($approvedPaths.Count -ne 1) {
        throw "Expected exactly one approved $ExecutableName, found $($approvedPaths.Count)."
    }
    return $approvedPaths[0]
}

function Write-Diagnostic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][hashtable]$Replacements
    )

    $safe = $Text
    foreach ($source in @($Replacements.Keys | Sort-Object { ([string]$_).Length } -Descending)) {
        foreach ($variant in @([string]$source, ([string]$source).Replace('\', '/')) | Sort-Object -Unique) {
            $replacement = [string]$Replacements[$source]
            $safe = [regex]::Replace(
                $safe,
                [regex]::Escape($variant),
                [Text.RegularExpressions.MatchEvaluator]{ param($match) [void]$match; $replacement },
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        }
    }
    if ($safe.Length -gt 65536) {
        $safe = $safe.Substring(0, 65536) + " [truncated]"
    }
    Write-LocalTextAtomically -Path $Path -Text $safe
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workRoot = Join-Path $repositoryRoot ("benchmarks\work\alignment-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$script:alignmentStageCompleted = $false
trap {
    $stageError = $_
    if (-not $script:alignmentStageCompleted -and (Test-Path -LiteralPath $workRoot -PathType Container)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $stageError
}
$workAcl = Protect-LocalEvidenceDirectory -Path $workRoot
if (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -ne 0) {
    throw "The alignment work directory was not empty immediately after ACL protection."
}

$diagnosticReplacements = @{
    $resolvedSample = "<SOURCE>"
    $repositoryRoot = "<WORKSPACE>"
    $workRoot = "<ALIGNMENT_WORK>"
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $diagnosticReplacements[$env:USERPROFILE] = "<USERPROFILE>"
}

$fixtureManifestPath = Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json"
$fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if (@($fixtureManifest.sourceAssets).Count -ne 1 -or
    (Get-FileSha256 -Path $resolvedSample) -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "The alignment source does not match the fixture manifest."
}

$asrCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\asr-benchmark.json"
$asrCanonical = Get-Content -LiteralPath $asrCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$asrRawPath = Join-Path $repositoryRoot "benchmarks\results\runs\$($asrCanonical.benchmarkRunId).json"
if (-not (Test-Path -LiteralPath $asrRawPath -PathType Leaf) -or
    (Get-FileSha256 -Path $asrRawPath) -ne $asrCanonical.benchmarkRunSha256) {
    throw "The immutable ST-006 raw run is missing or has changed."
}
$asrRaw = Get-Content -LiteralPath $asrRawPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
if ($asrRaw.status -ne "succeeded" -or $asrRaw.fixture.sourceSha256 -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "The ST-006 run is not a successful result for this fixture."
}
$cpuCandidate = $asrRaw.payload.candidates | Where-Object candidateId -eq "faster-whisper-small-cpu-int8" | Select-Object -First 1
$cudaCandidate = $asrRaw.payload.candidates | Where-Object candidateId -eq "faster-whisper-small-cuda-float16" | Select-Object -First 1
if ($cpuCandidate.status -ne "succeeded" -or $cudaCandidate.status -ne "succeeded") {
    throw "ST-007 requires successful ST-006 CPU and CUDA timestamp artifacts."
}

$asrEvidenceMatches = @(Get-ChildItem (Join-Path $repositoryRoot "benchmarks\work") -Directory -Filter "asr-*" -Force | Where-Object {
    $cpuPath = Join-Path $_.FullName $cpuCandidate.artifact.fileName
    $cudaPath = Join-Path $_.FullName $cudaCandidate.artifact.fileName
    (Test-Path -LiteralPath $cpuPath -PathType Leaf) -and
    (Test-Path -LiteralPath $cudaPath -PathType Leaf) -and
    (Get-FileSha256 -Path $cpuPath) -eq $cpuCandidate.artifact.sha256 -and
    (Get-FileSha256 -Path $cudaPath) -eq $cudaCandidate.artifact.sha256
})
if ($asrEvidenceMatches.Count -ne 1) {
    throw "Expected one ST-006 private evidence directory, found $($asrEvidenceMatches.Count)."
}
$asrEvidenceRoot = $asrEvidenceMatches[0].FullName
$asrEvidenceAcl = Get-LocalEvidenceAclEvidence -Path $asrEvidenceRoot
$asrStagePayloadPath = Join-Path $asrEvidenceRoot "stage-payload.json"
$asrCompletionPath = Join-Path $asrEvidenceRoot "completed.json"
if (-not (Test-Path -LiteralPath $asrStagePayloadPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $asrCompletionPath -PathType Leaf)) {
    throw "The ST-006 local evidence is not marked complete."
}
$cpuArtifactPath = Join-Path $asrEvidenceRoot $cpuCandidate.artifact.fileName
$cudaArtifactPath = Join-Path $asrEvidenceRoot $cudaCandidate.artifact.fileName

$decodedAudioEvidencePath = Join-Path $repositoryRoot "benchmarks\results\audio-event-and-recording-quality.json"
$decodedAudioEvidence = Get-Content -LiteralPath $decodedAudioEvidencePath -Raw -Encoding utf8 | ConvertFrom-Json
if ($decodedAudioEvidence.fixture.sourceSha256 -ne $fixtureManifest.sourceAssets[0].sha256 -or
    [double]$decodedAudioEvidence.analysisInput.decodedAnalysis.sourceTimelineStartSeconds -ne 0 -or
    [int64]$decodedAudioEvidence.analysisInput.decodedAnalysis.firstDecodedFramePts -ne 0 -or
    [double]$decodedAudioEvidence.analysisInput.decodedAnalysis.decodedDurationSeconds -ne [double]$asrRaw.payload.analysisInput.normalizedFixtureAudio.durationSeconds) {
    throw "Decoded audio timeline evidence does not match the ST-006 analysis input."
}

$alignmentControlPath = Join-Path $repositoryRoot "benchmarks\fixtures\alignment-control.json"
$alignmentControl = Get-Content -LiteralPath $alignmentControlPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($alignmentControl.accuracyThresholdApproved -ne $false -or @($alignmentControl.phrases).Count -lt 1) {
    throw "Alignment control configuration is invalid or marked approved."
}
$phraseIds = @($alignmentControl.phrases | ForEach-Object { [string]$_.id })
if (@($phraseIds | Sort-Object -Unique).Count -ne $phraseIds.Count -or
    @($phraseIds | Where-Object { $_ -notmatch '^[a-z0-9-]+$' }).Count -ne 0) {
    throw "Alignment control phrase IDs must be unique safe identifiers."
}

$implementationSpecs = @(
    @{ Id = "benchmark-harness"; Path = (Join-Path $repositoryRoot "benchmarks\run-benchmarks.ps1") },
    @{ Id = "alignment-wrapper"; Path = (Join-Path $repositoryRoot "benchmarks\stages\measure-alignment.ps1") },
    @{ Id = "alignment-driver"; Path = (Join-Path $repositoryRoot "benchmarks\stages\alignment_benchmark.py") },
    @{ Id = "asr-driver"; Path = (Join-Path $repositoryRoot "benchmarks\stages\asr_benchmark.py") },
    @{ Id = "benchmark-schema"; Path = (Join-Path $repositoryRoot "benchmarks\results\schema.json") },
    @{ Id = "alignment-control-config"; Path = $alignmentControlPath }
)
$implementationFiles = @($implementationSpecs | ForEach-Object {
    $descriptor = Get-FileDescriptor -Path $_.Path
    [pscustomobject][ordered]@{
        id = $_.Id
        fileName = $descriptor.fileName
        byteSize = $descriptor.byteSize
        sha256 = $descriptor.sha256
    }
})
$gitPath = (Get-Command git.exe -ErrorAction Stop).Source
$gitHeadProcess = Invoke-MeasuredProcess -FileName $gitPath -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments @("rev-parse", "HEAD")
if ($gitHeadProcess.exitCode -ne 0 -or $gitHeadProcess.stdout.Trim() -notmatch '^[0-9a-f]{40}$') {
    throw "Unable to record the Git HEAD for ST-007."
}
$gitStatusProcess = Invoke-MeasuredProcess -FileName $gitPath -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments (@("status", "--porcelain", "--") + @($implementationSpecs.Path))
if ($gitStatusProcess.exitCode -ne 0) {
    throw "Unable to record the ST-007 implementation working-tree state."
}

$ffmpegCapabilities = Get-Content (Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegPath = Find-ApprovedExecutable -PackageId $ffmpegCapabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $ffmpegCapabilities.executables.ffmpeg.sha256
$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing."
}
$packageProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    "-c",
    "import json,ctranslate2,faster_whisper;print(json.dumps({'faster-whisper':faster_whisper.__version__,'ctranslate2':ctranslate2.__version__,'cudaDeviceCount':ctranslate2.get_cuda_device_count()}))"
)
if ($packageProcess.exitCode -ne 0) {
    throw "Unable to inspect the alignment Python runtime."
}
$pythonPackages = ConvertFrom-LastJsonLine -Text $packageProcess.stdout
if ($pythonPackages.'faster-whisper' -ne "1.2.1" -or $pythonPackages.ctranslate2 -ne "4.8.1" -or
    [int]$pythonPackages.cudaDeviceCount -lt 1) {
    throw "The alignment Python runtime does not match ST-006."
}

$modelPath = Join-Path $repositoryRoot "benchmarks\models\faster-whisper-small-$fasterModelRevision"
$cudaRuntimePath = Join-Path $env:USERPROFILE ".foundry\ep\cuda-ep"
$cudaRuntime = Get-DirectoryManifest -Root $cudaRuntimePath -Recursive
if ($cudaRuntime.fileCount -ne $cudaRuntimeFileCount -or $cudaRuntime.totalBytes -ne $cudaRuntimeTotalBytes -or
    $cudaRuntime.manifestSha256 -ne $cudaRuntimeManifestSha256) {
    throw "The CUDA runtime does not match the ST-006 manifest."
}
$cudaEnvironment = @{ PATH = "$cudaRuntimePath;$env:PATH" }

$rawClipDirectory = Join-Path $workRoot "tts-raw"
$normalizedClipDirectory = Join-Path $workRoot "tts-16k"
[IO.Directory]::CreateDirectory($rawClipDirectory) | Out-Null
[IO.Directory]::CreateDirectory($normalizedClipDirectory) | Out-Null
Add-Type -AssemblyName System.Speech
foreach ($phrase in @($alignmentControl.phrases)) {
    $rawPath = Join-Path $rawClipDirectory "$($phrase.id).wav"
    $normalizedPath = Join-Path $normalizedClipDirectory "$($phrase.id).wav"
    $synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $voice = $synthesizer.GetInstalledVoices() | Where-Object {
            $_.Enabled -and $_.VoiceInfo.Name -eq $alignmentControl.voice.name -and
            $_.VoiceInfo.Culture.Name -eq $alignmentControl.voice.culture
        } | Select-Object -First 1
        if ($null -eq $voice) {
            throw "The pinned Japanese TTS alignment voice is not installed."
        }
        $synthesizer.SelectVoice($alignmentControl.voice.name)
        $synthesizer.SetOutputToWaveFile($rawPath)
        $synthesizer.Speak([string]$phrase.spokenPhrase)
    }
    finally {
        $synthesizer.Dispose()
    }
    $normalization = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 120 -Arguments @(
        "-v", "error", "-i", $rawPath, "-map_metadata", "-1", "-ac", "1", "-ar", "16000",
        "-c:a", "pcm_s16le", "-fflags", "+bitexact", "-flags:a", "+bitexact", "-y", $normalizedPath
    )
    if ($normalization.exitCode -ne 0) {
        throw "A Japanese TTS alignment clip could not be normalized."
    }
}
Remove-Item -LiteralPath $rawClipDirectory -Recurse -Force

$alignmentScriptPath = Join-Path $PSScriptRoot "alignment_benchmark.py"
$controlAudioPath = Join-Path $workRoot "alignment-control-16k-mono.wav"
$controlManifestPath = Join-Path $workRoot "alignment-control-manifest.json"
$buildControl = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
    $alignmentScriptPath, "build-control", "--config", $alignmentControlPath,
    "--clip-directory", $normalizedClipDirectory, "--output-audio", $controlAudioPath,
    "--output-manifest", $controlManifestPath
)
if ($buildControl.exitCode -ne 0) {
    throw "Alignment slot control construction failed."
}
$controlConstructionResult = ConvertFrom-LastJsonLine -Text $buildControl.stdout
if ($controlConstructionResult.status -ne "succeeded" -or
    [int]$controlConstructionResult.slotCount -ne @($alignmentControl.phrases).Count) {
    throw "Alignment slot control summary does not match the configured phrase count."
}
$controlManifest = Get-Content -LiteralPath $controlManifestPath -Raw -Encoding utf8 | ConvertFrom-Json

$cpuControlArtifactPath = Join-Path $workRoot "alignment-control-cpu.json"
$cpuControlProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -Arguments @(
    $alignmentScriptPath, "run-control", "--candidate-id", "faster-whisper-small-cpu-int8",
    "--device", "cpu", "--compute-type", "int8", "--model-path", $modelPath,
    "--config", $alignmentControlPath, "--manifest", $controlManifestPath,
    "--control-audio", $controlAudioPath, "--clip-directory", $normalizedClipDirectory,
    "--output-artifact", $cpuControlArtifactPath
)
$cpuDiagnosticPath = Join-Path $workRoot "alignment-control-cpu.stderr.txt"
Write-Diagnostic -Path $cpuDiagnosticPath -Text $cpuControlProcess.stderr -Replacements $diagnosticReplacements
$cpuControlSummary = ConvertFrom-LastJsonLine -Text $cpuControlProcess.stdout
if ($cpuControlProcess.exitCode -ne 0 -or $cpuControlSummary.status -ne "succeeded") {
    $safeChecks = @($cpuControlSummary.termWindowChecks | ForEach-Object {
        [ordered]@{
            id = $_.id
            occurrenceCount = $_.occurrenceCount
            assignedSlotOccurrenceCount = $_.assignedSlotOccurrenceCount
            mappedWordCount = $_.mappedWordCount
            withinAssignedSlot = $_.withinAssignedSlot
        }
    }) | ConvertTo-Json -Compress
    throw "The CPU alignment slot control failed. Safe term-window checks: $safeChecks"
}

$cudaControlArtifactPath = Join-Path $workRoot "alignment-control-cuda.json"
$cudaControlProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -EnvironmentVariables $cudaEnvironment -Arguments @(
    $alignmentScriptPath, "run-control", "--candidate-id", "faster-whisper-small-cuda-float16",
    "--device", "cuda", "--compute-type", "float16", "--model-path", $modelPath,
    "--config", $alignmentControlPath, "--manifest", $controlManifestPath,
    "--control-audio", $controlAudioPath, "--clip-directory", $normalizedClipDirectory,
    "--output-artifact", $cudaControlArtifactPath
)
$cudaDiagnosticPath = Join-Path $workRoot "alignment-control-cuda.stderr.txt"
Write-Diagnostic -Path $cudaDiagnosticPath -Text $cudaControlProcess.stderr -Replacements $diagnosticReplacements
$cudaControlSummary = ConvertFrom-LastJsonLine -Text $cudaControlProcess.stdout
if ($cudaControlProcess.exitCode -ne 0 -or $cudaControlSummary.status -ne "succeeded") {
    $safeChecks = @($cudaControlSummary.termWindowChecks | ForEach-Object {
        [ordered]@{
            id = $_.id
            occurrenceCount = $_.occurrenceCount
            assignedSlotOccurrenceCount = $_.assignedSlotOccurrenceCount
            mappedWordCount = $_.mappedWordCount
            withinAssignedSlot = $_.withinAssignedSlot
        }
    }) | ConvertTo-Json -Compress
    throw "The CUDA alignment slot control failed. Safe term-window checks: $safeChecks"
}

$fixtureAnalysisArtifactPath = Join-Path $workRoot "fixture-alignment-analysis.json"
$fixtureAnalysisProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 600 -WorkingDirectory $repositoryRoot -Arguments @(
    $alignmentScriptPath, "analyze-fixture",
    "--cpu-artifact", $cpuArtifactPath, "--cpu-artifact-sha256", $cpuCandidate.artifact.sha256,
    "--cuda-artifact", $cudaArtifactPath, "--cuda-artifact-sha256", $cudaCandidate.artifact.sha256,
    "--input-audio-sha256", $asrRaw.payload.analysisInput.normalizedFixtureAudio.sha256,
    "--output-artifact", $fixtureAnalysisArtifactPath
)
$fixtureAnalysisDiagnosticPath = Join-Path $workRoot "fixture-alignment-analysis.stderr.txt"
Write-Diagnostic -Path $fixtureAnalysisDiagnosticPath -Text $fixtureAnalysisProcess.stderr -Replacements $diagnosticReplacements
if ($fixtureAnalysisProcess.exitCode -ne 0) {
    throw "Fixture alignment analysis failed."
}
$fixtureAnalysisSummary = ConvertFrom-LastJsonLine -Text $fixtureAnalysisProcess.stdout

$workAcl = Get-LocalEvidenceAclEvidence -Path $workRoot
$payload = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        hardwareBenchmarkRunId = $asrRaw.payload.host.hardwareBenchmarkRunId
        hardwareBenchmarkRunSha256 = $asrRaw.payload.host.hardwareBenchmarkRunSha256
        recordedOperatingSystemVersion = $asrRaw.payload.host.operatingSystemVersion
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    implementation = [ordered]@{
        gitHead = $gitHeadProcess.stdout.Trim()
        implementationFilesCleanAtRun = [string]::IsNullOrWhiteSpace($gitStatusProcess.stdout)
        files = $implementationFiles
    }
    upstream = [ordered]@{
        asrCanonical = [ordered]@{
            fileName = [IO.Path]::GetFileName($asrCanonicalPath)
            sha256 = Get-FileSha256 -Path $asrCanonicalPath
        }
        asrBenchmarkRunId = $asrRaw.runId
        asrBenchmarkRunSha256 = Get-FileSha256 -Path $asrRawPath
        normalizedFixtureAudioSha256 = $asrRaw.payload.analysisInput.normalizedFixtureAudio.sha256
        normalizedFixtureDurationSeconds = $asrRaw.payload.analysisInput.normalizedFixtureAudio.durationSeconds
        cpuArtifact = $cpuCandidate.artifact
        cudaArtifact = $cudaCandidate.artifact
        privateEvidenceAcl = $asrEvidenceAcl
        privateEvidenceCompletionMarkerPresent = $true
        privateStagePayload = Get-FileDescriptor -Path $asrStagePayloadPath
    }
    tooling = [ordered]@{
        pythonVersion = (& $pythonPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
        pythonPackages = $pythonPackages
        ffmpegPackageId = $ffmpegCapabilities.package.id
        ffmpegSha256 = $ffmpegCapabilities.executables.ffmpeg.sha256
        fasterWhisperModelRevision = $fasterModelRevision
        fasterWhisperModelManifestSha256 = $fasterModelManifestSha256
        cudaRuntimeManifestSha256 = $cudaRuntime.manifestSha256
        cudaRuntimePathInjectedOnlyForControlChild = $true
        cudaRuntimeReleaseRedistributionApproved = $false
    }
    separatedSlotControl = [ordered]@{
        controlId = $alignmentControl.controlId
        configSha256 = Get-FileSha256 -Path $alignmentControlPath
        transcriptionInputMode = "independent-slot-clips-mapped-through-manifest-offsets"
        constructedControlAudioWasTranscribed = $false
        manifest = Get-FileDescriptor -Path $controlManifestPath
        audio = $controlManifest.audio
        slotCount = $controlManifest.slotCount
        construction = $controlManifest.construction
        cpu = [ordered]@{
            summary = $cpuControlSummary
            processMeasurement = [ordered]@{
                elapsedMilliseconds = $cpuControlProcess.elapsedMilliseconds
                baselineAggregateWorkingSetBytes = $cpuControlProcess.baselineAggregateWorkingSetBytes
                peakAggregateWorkingSetBytes = $cpuControlProcess.peakAggregateWorkingSetBytes
                workingSetScope = $cpuControlProcess.workingSetScope
                observedProcessIdCount = $cpuControlProcess.observedProcessIdCount
            }
            diagnostic = Get-FileDescriptor -Path $cpuDiagnosticPath
        }
        cuda = [ordered]@{
            summary = $cudaControlSummary
            processMeasurement = [ordered]@{
                elapsedMilliseconds = $cudaControlProcess.elapsedMilliseconds
                baselineAggregateWorkingSetBytes = $cudaControlProcess.baselineAggregateWorkingSetBytes
                peakAggregateWorkingSetBytes = $cudaControlProcess.peakAggregateWorkingSetBytes
                workingSetScope = $cudaControlProcess.workingSetScope
                observedProcessIdCount = $cudaControlProcess.observedProcessIdCount
            }
            diagnostic = Get-FileDescriptor -Path $cudaDiagnosticPath
        }
        exactBoundaryAccuracyMeasured = $false
        accuracyThresholdApproved = $false
        interpretation = $controlManifest.interpretation
    }
    fixtureAnalysis = [ordered]@{
        candidates = $fixtureAnalysisSummary.candidates
        crossDeviceAgreement = $fixtureAnalysisSummary.agreement
        localArtifact = $fixtureAnalysisSummary.artifact
        processMeasurement = [ordered]@{
            elapsedMilliseconds = $fixtureAnalysisProcess.elapsedMilliseconds
            baselineAggregateWorkingSetBytes = $fixtureAnalysisProcess.baselineAggregateWorkingSetBytes
            peakAggregateWorkingSetBytes = $fixtureAnalysisProcess.peakAggregateWorkingSetBytes
            workingSetScope = $fixtureAnalysisProcess.workingSetScope
            observedProcessIdCount = $fixtureAnalysisProcess.observedProcessIdCount
        }
        diagnostic = Get-FileDescriptor -Path $fixtureAnalysisDiagnosticPath
        transcriptTextIncludedInPayload = $false
    }
    timelineMapping = [ordered]@{
        decodedAudioEvidenceBenchmarkRunId = $decodedAudioEvidence.benchmarkRunId
        decodedAudioEvidenceBenchmarkRunSha256 = $decodedAudioEvidence.benchmarkRunSha256
        normalizedAudioTimelineStartSeconds = 0.0
        firstDecodedSourceAudioFramePts = [int64]$decodedAudioEvidence.analysisInput.decodedAnalysis.firstDecodedFramePts
        firstDecodedSourceAudioFrameSeconds = [double]$decodedAudioEvidence.analysisInput.decodedAnalysis.firstDecodedFrameSeconds
        observedMinimumPacketPresentationSeconds = [double]$decodedAudioEvidence.analysisInput.sourceAudio.minimumPacketPresentationSeconds
        mappingRule = "ASR timestamps are relative to normalized decoded PCM starting at decoded source-audio frame PTS 0. The negative AAC packet presentation time is codec-delay evidence and is not added to decoded timestamps."
        exactSourceBoundaryErrorMeasured = $false
    }
    annotations = [ordered]@{
        transcriptReferenceAvailable = Test-Path -LiteralPath (Join-Path $repositoryRoot "benchmarks\annotations\transcript-reference.jsonl") -PathType Leaf
        exactWordBoundaryAccuracyMeasured = $false
        exactUtteranceBoundaryAccuracyMeasured = $false
        crossDeviceAgreementIsAccuracyEvidence = $false
    }
    disposition = [ordered]@{
        status = "provisional-candidate-with-required-manual-fallback"
        alignmentEvaluationTarget = "faster-whisper-small-cuda-float16"
        emittedWordTimestampsMayBeStoredAsEditableCandidates = $true
        productionWordTimingApproved = $false
        utteranceTimingAndManualAdjustmentFallbackRequired = $true
        clampWordTimesToParentSegments = $false
        crossDeviceOutputDivergence = [ordered]@{
            controlOutputHashesIdentical = $cpuControlSummary.recognizedTextSetSha256 -eq $cudaControlSummary.recognizedTextSetSha256
            cpuControlRecognizedCharacterCount = [int]$cpuControlSummary.recognizedCharacterCount
            cudaControlRecognizedCharacterCount = [int]$cudaControlSummary.recognizedCharacterCount
            fixtureOutputHashesIdentical = $fixtureAnalysisSummary.agreement.textHashesIdentical
            cpuFixtureSegmentCount = [int]$fixtureAnalysisSummary.candidates[0].segmentCount
            cudaFixtureSegmentCount = [int]$fixtureAnalysisSummary.candidates[1].segmentCount
            cpuFixtureWordCount = [int]$fixtureAnalysisSummary.candidates[0].wordCount
            cudaFixtureWordCount = [int]$fixtureAnalysisSummary.candidates[1].wordCount
            matchedExactTokenCount = [int]$fixtureAnalysisSummary.agreement.matchedExactTokenCount
            selectionUsesOutputAccuracy = $false
            timingAccuracyImplication = "unknown-without-independent-reference"
        }
        reason = "The target emits structurally valid word and utterance timestamps and passes a separated-slot mechanism control, but exact timing accuracy is unmeasured and one parent-containment overflow remains observable."
        finalStage0DecisionPending = $true
    }
    privacy = [ordered]@{
        localEvidenceAcl = $workAcl
        sourceTranscriptTextIncludedInPayload = $false
        sourceTranscriptTextIncludedInCanonicalSummary = $false
        localArtifactsContainTranscriptText = $true
        absoluteHostPathsIncludedInPayload = $false
        privateUpstreamEvidenceRemainsIgnored = $true
        workDirectoryWasEmptyAfterAclProtection = $true
    }
    samplePathConsumed = $true
}
$payloadJson = $payload | ConvertTo-Json -Depth 20
if ($payloadJson -match '"(?:text|content|transcript|referenceText|spokenPhrase)"\s*:') {
    throw "Alignment payload contains a forbidden transcript content property."
}
Write-LocalTextAtomically -Path (Join-Path $workRoot "stage-payload.json") -Text $payloadJson
Write-LocalTextAtomically -Path (Join-Path $workRoot "completed.json") -Text (([ordered]@{
    schemaVersion = "1.0.0"
    completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
    upstreamAsrRunSha256 = Get-FileSha256 -Path $asrRawPath
}) | ConvertTo-Json -Compress)
$script:alignmentStageCompleted = $true
$payloadJson
