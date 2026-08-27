#Requires -Version 7.0

<#
.SYNOPSIS
Compares local Japanese ASR candidates on one normalized fixture audio stream.

.DESCRIPTION
Runs faster-whisper small on CPU and CUDA, and the pinned Foundry Local Whisper small GPU
variant when present. A local Japanese TTS control proves that each successful pipeline is
operating. Rights-unconfirmed transcript text and detailed timestamps are written only to ignored
local artifacts; the benchmark payload contains hashes, counts, timing, memory, and availability.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 10800
$foundryModelId = "openai-whisper-small-cuda-gpu:3"
$foundryModelDirectoryName = "openai-whisper-small-cuda-gpu-3"
$foundryModelVersionDirectory = "v3"
$foundryModelManifestSha256 = "003489877f5a82eccbaeb435e520fb1656780331f26ae7cb4f03bbb15f8f64f8"
$foundryModelFileCount = 17
$foundryModelTotalBytes = 316267930
$foundryCudaEpManifestSha256 = "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021"
$foundryCudaEpFileCount = 14
$foundryCudaEpTotalBytes = 2423764787
$foundryCudaEpVersionFileSha256 = "28236ce308e5232254f4f09ca8e6df0805b9a3608a79d9e4d09a4b459fb99640"
$fasterModelRevision = "536b0662742c02347bc0e980a01041f333bce120"
$fasterModelManifestSha256 = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"
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

function Get-LocalEvidenceAclEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for ASR evidence protection."
    }
    $systemSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-18")
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    $allowedSids = @($currentSid, $systemSid, $administratorsSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $verified = Get-Acl -LiteralPath $Path
    $allowedValues = @($allowedSids | ForEach-Object { $_.Value })
    $unexpected = @($verified.Access | Where-Object {
        $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedValues -or
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl -or
        ($_.InheritanceFlags -band $inheritance) -ne $inheritance
    })
    $verifiedOwnerSid = ([Security.Principal.NTAccount]$verified.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
    if (-not $verified.AreAccessRulesProtected -or $verifiedOwnerSid -ne $currentSid.Value -or
        $unexpected.Count -ne 0 -or @($verified.Access).Count -ne 3) {
        throw "The ASR evidence directory ACL did not match the approved local principals."
    }
    return [pscustomobject][ordered]@{
        inheritanceProtected = $true
        approvedPrincipalCount = 3
        accessRuleCount = @($verified.Access).Count
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
        # A newly created or legacy directory is expected to fail the strict read-only check once.
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for ASR evidence protection."
    }
    $systemSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-18")
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($currentSid)
    foreach ($sid in @($currentSid, $systemSid, $administratorsSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    return Get-LocalEvidenceAclEvidence -Path $Path
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

function Protect-LocalDiagnosticText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][hashtable]$PathReplacements,
        [ValidateRange(1024, 1048576)][int]$MaximumCharacters = 65536
    )

    $safe = $Text
    $orderedPaths = @($PathReplacements.Keys | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_)
    } | Sort-Object { ([string]$_).Length } -Descending)
    foreach ($path in $orderedPaths) {
        $replacement = [string]$PathReplacements[$path]
        foreach ($variant in @([string]$path, ([string]$path).Replace('\', '/')) | Sort-Object -Unique) {
            $safe = [regex]::Replace(
                $safe,
                [regex]::Escape($variant),
                [Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement },
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        }
    }
    if ($safe.Length -gt $MaximumCharacters) {
        $safe = $safe.Substring(0, $MaximumCharacters) + " [truncated]"
    }
    return $safe
}

function Write-LocalDiagnosticAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][hashtable]$PathReplacements
    )

    Write-LocalTextAtomically -Path $Path -Text (Protect-LocalDiagnosticText -Text $Text -PathReplacements $PathReplacements)
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

function Get-DescendantProcessIds {
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

function Stop-ProcessTreeAndWait {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($Process.Id)
    foreach ($processId in Get-DescendantProcessIds -RootProcessId $Process.Id) {
        [void]$ids.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    $remaining = 0
    foreach ($processId in $ids) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit(10000)) {
                $remaining++
            }
        }
        finally {
            $observed.Dispose()
        }
    }
    if ($remaining -gt 0) {
        throw "A timed-out ASR process left $remaining observed processes running."
    }
}

function Get-AggregateWorkingSetBytes {
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

function Get-SystemWideGpuMemoryUsedMiB {
    $nvidiaSmi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($null -eq $nvidiaSmi) {
        return $null
    }
    $lines = @(& $nvidiaSmi.Source --query-gpu=memory.used --format=csv,noheader,nounits 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
        return $null
    }
    [int64]$total = 0
    foreach ($line in $lines) {
        $value = 0L
        if (-not [int64]::TryParse($line.Trim(), [ref]$value)) {
            return $null
        }
        $total += $value
    }
    return $total
}

function Invoke-MeasuredProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [string]$WorkingDirectory,
        [AllowEmptyCollection()]
        [int[]]$MonitorProcessIds = @(),
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
    $gpuStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $baselineGpuMiB = Get-SystemWideGpuMemoryUsedMiB
    $peakGpuMiB = $baselineGpuMiB
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
        foreach ($processId in $MonitorProcessIds) { [void]$observedIds.Add($processId) }
        foreach ($rootId in @($process.Id) + @($MonitorProcessIds)) {
            foreach ($processId in Get-DescendantProcessIds -RootProcessId $rootId) {
                [void]$observedIds.Add($processId)
            }
        }
        $treeRefresh = [Diagnostics.Stopwatch]::StartNew()
        $baselineWorkingSet = Get-AggregateWorkingSetBytes -ProcessIds @($observedIds)
        $peakWorkingSet = $baselineWorkingSet
        while (-not $process.WaitForExit(250)) {
            if ($treeRefresh.ElapsedMilliseconds -ge 1000) {
                foreach ($rootId in @($process.Id) + @($MonitorProcessIds)) {
                    foreach ($processId in Get-DescendantProcessIds -RootProcessId $rootId) {
                        [void]$observedIds.Add($processId)
                    }
                }
                $treeRefresh.Restart()
            }
            $workingSet = Get-AggregateWorkingSetBytes -ProcessIds @($observedIds)
            if ($workingSet -gt $peakWorkingSet) {
                $peakWorkingSet = $workingSet
            }
            if ($gpuStopwatch.ElapsedMilliseconds -ge 1000) {
                $gpuMemory = Get-SystemWideGpuMemoryUsedMiB
                if ($null -ne $gpuMemory -and ($null -eq $peakGpuMiB -or $gpuMemory -gt $peakGpuMiB)) {
                    $peakGpuMiB = $gpuMemory
                }
                $gpuStopwatch.Restart()
            }
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Stop-ProcessTreeAndWait -Process $process
                throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
            }
        }
        $process.WaitForExit()
        $workingSet = Get-AggregateWorkingSetBytes -ProcessIds $MonitorProcessIds
        if ($workingSet -gt $peakWorkingSet) {
            $peakWorkingSet = $workingSet
        }
        $gpuMemory = Get-SystemWideGpuMemoryUsedMiB
        if ($null -ne $gpuMemory -and ($null -eq $peakGpuMiB -or $gpuMemory -gt $peakGpuMiB)) {
            $peakGpuMiB = $gpuMemory
        }
        $stopwatch.Stop()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
            elapsedMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            baselineAggregateWorkingSetBytes = $baselineWorkingSet
            peakAggregateWorkingSetBytes = $peakWorkingSet
            systemWideGpuMemoryUsedMiBBaseline = $baselineGpuMiB
            systemWideGpuMemoryUsedMiBPeak = $peakGpuMiB
            systemWideGpuMemoryUsedMiBPeakDelta = if ($null -eq $baselineGpuMiB -or $null -eq $peakGpuMiB) {
                $null
            }
            else {
                [math]::Max(0, [int64]$peakGpuMiB - [int64]$baselineGpuMiB)
            }
            gpuMemoryScope = "system-wide observation; WDDM did not expose per-process used_gpu_memory"
            workingSetScope = "root process, dynamically discovered descendants, and explicit monitored process IDs"
            observedProcessIdCount = $observedIds.Count
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            Stop-ProcessTreeAndWait -Process $process
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
    throw "Process output did not contain a JSON object."
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
    $matches = @(Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName | Where-Object {
        (Get-FileSha256 -Path $_.FullName) -eq $ExpectedSha256
    })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one approved $ExecutableName, found $($matches.Count)."
    }
    return $matches[0].FullName
}

function Get-WavDescriptor {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$FfprobePath
    )

    $probe = Invoke-MeasuredProcess -FileName $FfprobePath -TimeoutSeconds 120 -Arguments @(
        "-v", "error", "-show_entries",
        "stream=codec_name,sample_rate,channels,time_base,start_time,duration:format=duration,size",
        "-of", "json", $Path
    )
    if ($probe.exitCode -ne 0) {
        throw "ffprobe could not inspect the normalized ASR audio."
    }
    $value = $probe.stdout | ConvertFrom-Json -ErrorAction Stop
    if (@($value.streams).Count -ne 1 -or $value.streams[0].codec_name -ne "pcm_s16le" -or
        [int]$value.streams[0].sample_rate -ne 16000 -or [int]$value.streams[0].channels -ne 1) {
        throw "Normalized ASR audio is not mono 16 kHz PCM S16LE."
    }
    return [pscustomobject][ordered]@{
        fileName = [IO.Path]::GetFileName($Path)
        byteSize = [int64](Get-Item -LiteralPath $Path).Length
        sha256 = Get-FileSha256 -Path $Path
        codecName = $value.streams[0].codec_name
        sampleRate = [int]$value.streams[0].sample_rate
        channels = [int]$value.streams[0].channels
        timeBase = $value.streams[0].time_base
        startTimeSeconds = [double]$value.streams[0].start_time
        durationSeconds = [math]::Round([double]$value.format.duration, 6)
    }
}

function New-CandidateRecord {
    param(
        [Parameter(Mandatory)][object]$Summary,
        [Parameter(Mandatory)][object]$Measurement,
        [Parameter(Mandatory)][object]$DiagnosticDescriptor
    )

    return [pscustomobject][ordered]@{
        candidateId = $Summary.candidateId
        status = $Summary.status
        engine = $Summary.engine
        model = $Summary.model
        configuration = $Summary.configuration
        control = $Summary.control
        directWholeFileDiagnostic = $Summary.directWholeFileDiagnostic
        fixture = $Summary.fixture
        timing = $Summary.timing
        processMeasurement = [ordered]@{
            exitCode = $Measurement.exitCode
            elapsedMilliseconds = $Measurement.elapsedMilliseconds
            baselineAggregateWorkingSetBytes = $Measurement.baselineAggregateWorkingSetBytes
            peakAggregateWorkingSetBytes = $Measurement.peakAggregateWorkingSetBytes
            systemWideGpuMemoryUsedMiBBaseline = $Measurement.systemWideGpuMemoryUsedMiBBaseline
            systemWideGpuMemoryUsedMiBPeak = $Measurement.systemWideGpuMemoryUsedMiBPeak
            systemWideGpuMemoryUsedMiBPeakDelta = $Measurement.systemWideGpuMemoryUsedMiBPeakDelta
            gpuMemoryScope = $Measurement.gpuMemoryScope
            workingSetScope = $Measurement.workingSetScope
            observedProcessIdCount = $Measurement.observedProcessIdCount
        }
        artifact = $Summary.artifact
        error = $Summary.error
        diagnostic = $DiagnosticDescriptor
        transcriptTextIncludedInPayload = $false
    }
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workRoot = Join-Path $repositoryRoot ("benchmarks\work\asr-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$script:asrStageCompleted = $false
trap {
    $stageError = $_
    if (-not $script:asrStageCompleted -and (Test-Path -LiteralPath $workRoot -PathType Container)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $stageError
}
$workAcl = Protect-LocalEvidenceDirectory -Path $workRoot
$diagnosticPathReplacements = @{
    $resolvedSample = "<SOURCE>"
    $workRoot = "<ASR_WORK>"
    $repositoryRoot = "<WORKSPACE>"
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $diagnosticPathReplacements[$env:USERPROFILE] = "<USERPROFILE>"
}

$controlConfigPath = Join-Path $repositoryRoot "benchmarks\fixtures\asr-control.json"
$controlConfig = Get-Content -LiteralPath $controlConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
$fixtureManifest = Get-Content (Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json") -Raw -Encoding utf8 | ConvertFrom-Json
$mediaIngest = Get-Content (Join-Path $repositoryRoot "benchmarks\results\media-ingest.json") -Raw -Encoding utf8 | ConvertFrom-Json
$hardware = Get-Content (Join-Path $repositoryRoot "benchmarks\results\hardware-inventory.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegCapabilities = Get-Content (Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
if (@($fixtureManifest.sourceAssets).Count -ne 1 -or (Get-FileSha256 -Path $resolvedSample) -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "The ASR source does not match the fixture manifest."
}
$sourceAudioStreams = @($mediaIngest.streams | Where-Object { $_.codecType -eq "audio" })
if ($sourceAudioStreams.Count -ne 1) {
    throw "ST-006 requires exactly one source audio stream in the ST-003 evidence."
}
$sourceAudioStream = $sourceAudioStreams[0]
if ($sourceAudioStream.codecName -ne "aac" -or [int]$sourceAudioStream.sampleRate -ne 48000 -or
    [int]$sourceAudioStream.channels -ne 2) {
    throw "The source audio stream differs from the ST-003 fixture evidence."
}

$implementationSpecs = @(
    @{ Id = "benchmark-harness"; Path = (Join-Path $repositoryRoot "benchmarks\run-benchmarks.ps1") },
    @{ Id = "asr-wrapper"; Path = (Join-Path $repositoryRoot "benchmarks\stages\measure-asr.ps1") },
    @{ Id = "asr-driver"; Path = (Join-Path $repositoryRoot "benchmarks\stages\asr_benchmark.py") },
    @{ Id = "benchmark-schema"; Path = (Join-Path $repositoryRoot "benchmarks\results\schema.json") },
    @{ Id = "python-environment-setup"; Path = (Join-Path $repositoryRoot "benchmarks\setup-python-env.ps1") },
    @{ Id = "asr-control-config"; Path = $controlConfigPath }
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
$gitHeadResult = Invoke-MeasuredProcess -FileName (Get-Command git.exe -ErrorAction Stop).Source -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments @("rev-parse", "HEAD")
if ($gitHeadResult.exitCode -ne 0 -or $gitHeadResult.stdout.Trim() -notmatch '^[0-9a-f]{40}$') {
    throw "Unable to record the Git HEAD for the ASR benchmark."
}
$gitStatusResult = Invoke-MeasuredProcess -FileName (Get-Command git.exe -ErrorAction Stop).Source -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments (@("status", "--porcelain", "--") + @($implementationSpecs.Path))
if ($gitStatusResult.exitCode -ne 0) {
    throw "Unable to record the implementation working-tree state."
}

$ffmpegPath = Find-ApprovedExecutable -PackageId $ffmpegCapabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $ffmpegCapabilities.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $ffmpegCapabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $ffmpegCapabilities.executables.ffprobe.sha256
$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing."
}

$controlRawPath = Join-Path $workRoot "asr-control-raw.wav"
$controlAudioPath = Join-Path $workRoot "asr-control-16k-mono.wav"
Add-Type -AssemblyName System.Speech
$synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $voice = $synthesizer.GetInstalledVoices() | Where-Object {
        $_.Enabled -and $_.VoiceInfo.Name -eq $controlConfig.voice.name -and $_.VoiceInfo.Culture.Name -eq $controlConfig.voice.culture
    } | Select-Object -First 1
    if ($null -eq $voice) {
        throw "The pinned Japanese TTS control voice is not installed."
    }
    $synthesizer.SelectVoice($controlConfig.voice.name)
    $synthesizer.SetOutputToWaveFile($controlRawPath)
    $synthesizer.Speak($controlConfig.referenceText)
}
finally {
    $synthesizer.Dispose()
}
$controlNormalize = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 300 -Arguments @(
    "-v", "error", "-i", $controlRawPath, "-map_metadata", "-1", "-ac", "1", "-ar", "16000",
    "-c:a", "pcm_s16le", "-fflags", "+bitexact", "-flags:a", "+bitexact", "-y", $controlAudioPath
)
if ($controlNormalize.exitCode -ne 0) {
    throw "Japanese TTS control normalization failed."
}
Remove-Item -LiteralPath $controlRawPath -Force

$fixtureAudioPath = Join-Path $workRoot "fixture-audio-16k-mono.wav"
$fixtureAudioBuild = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 1800 -Arguments @(
    "-v", "error", "-i", $resolvedSample, "-map", "0:a:0", "-vn", "-map_metadata", "-1",
    "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-fflags", "+bitexact", "-flags:a", "+bitexact",
    "-y", $fixtureAudioPath
)
if ($fixtureAudioBuild.exitCode -ne 0) {
    throw "Fixture audio normalization failed."
}
$controlAudio = Get-WavDescriptor -Path $controlAudioPath -FfprobePath $ffprobePath
$fixtureAudio = Get-WavDescriptor -Path $fixtureAudioPath -FfprobePath $ffprobePath

$pythonPackageResult = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    "-c",
    "import json,ctranslate2,faster_whisper,huggingface_hub,tokenizers;print(json.dumps({'faster-whisper':faster_whisper.__version__,'ctranslate2':ctranslate2.__version__,'huggingface_hub':huggingface_hub.__version__,'tokenizers':tokenizers.__version__,'cudaDeviceCount':ctranslate2.get_cuda_device_count()}))"
)
if ($pythonPackageResult.exitCode -ne 0) {
    throw "Unable to inspect ASR Python runtime versions."
}
$pythonPackages = ConvertFrom-LastJsonLine -Text $pythonPackageResult.stdout
foreach ($expected in @(
    @{ Name = "faster-whisper"; Actual = $pythonPackages.'faster-whisper'; Version = "1.2.1" },
    @{ Name = "ctranslate2"; Actual = $pythonPackages.ctranslate2; Version = "4.8.1" },
    @{ Name = "huggingface_hub"; Actual = $pythonPackages.huggingface_hub; Version = "1.28.0" },
    @{ Name = "tokenizers"; Actual = $pythonPackages.tokenizers; Version = "0.23.1" }
)) {
    if ($expected.Actual -ne $expected.Version) {
        throw "Unexpected ASR Python runtime version: $($expected.Name) $($expected.Actual)"
    }
}

$asrScriptPath = Join-Path $PSScriptRoot "asr_benchmark.py"
$fasterModelPath = Join-Path $repositoryRoot "benchmarks\models\faster-whisper-small-$fasterModelRevision"
$prepareModel = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 3600 -WorkingDirectory $repositoryRoot -Arguments @(
    $asrScriptPath, "prepare-faster-model", "--model-path", $fasterModelPath
)
if ($prepareModel.exitCode -ne 0) {
    throw "Pinned faster-whisper model acquisition or verification failed."
}
$fasterModel = (ConvertFrom-LastJsonLine -Text $prepareModel.stdout).model
if ($fasterModel.revision -ne $fasterModelRevision -or $fasterModel.manifestSha256 -ne $fasterModelManifestSha256) {
    throw "Prepared faster-whisper model does not match the pinned revision and manifest."
}

$cpuArtifactPath = Join-Path $workRoot "faster-whisper-cpu-transcript.json"
$cpuMeasurement = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -Arguments @(
    $asrScriptPath, "run-faster", "--candidate-id", "faster-whisper-small-cpu-int8",
    "--model-path", $fasterModelPath, "--control-config", $controlConfigPath,
    "--control-audio", $controlAudioPath, "--fixture-audio", $fixtureAudioPath,
    "--device", "cpu", "--compute-type", "int8", "--output-artifact", $cpuArtifactPath
)
$cpuDiagnosticPath = Join-Path $workRoot "faster-whisper-cpu.stderr.txt"
Write-LocalDiagnosticAtomically -Path $cpuDiagnosticPath -Text $cpuMeasurement.stderr -PathReplacements $diagnosticPathReplacements
$cpuSummary = ConvertFrom-LastJsonLine -Text $cpuMeasurement.stdout
$cpuCandidate = New-CandidateRecord -Summary $cpuSummary -Measurement $cpuMeasurement -DiagnosticDescriptor (Get-FileDescriptor -Path $cpuDiagnosticPath)
$cpuCandidate | Add-Member -NotePropertyName comparableTiming -NotePropertyValue ([pscustomobject][ordered]@{
    modelLoadMilliseconds = [math]::Round([double]$cpuSummary.timing.modelLoadSeconds * 1000, 3)
    controlTranscriptionMilliseconds = [math]::Round([double]$cpuSummary.timing.controlTranscriptionSeconds * 1000, 3)
    fixtureCoverageMilliseconds = [math]::Round([double]$cpuSummary.timing.fixtureTranscriptionSeconds * 1000, 3)
    fixtureCoveragePath = "single-full-normalized-wav"
    diagnosticWholeFileInvocationExcluded = $true
    singleObservation = $true
})

$fasterCudaLibraryDirectory = Join-Path $env:USERPROFILE ".foundry\ep\cuda-ep"
$fasterCudaRuntime = if (Test-Path -LiteralPath $fasterCudaLibraryDirectory -PathType Container) {
    Get-DirectoryManifest -Root $fasterCudaLibraryDirectory -Recursive
}
else { $null }
$fasterCudaEnvironment = @{}
if ($null -ne $fasterCudaRuntime -and
    $fasterCudaRuntime.fileCount -eq $foundryCudaEpFileCount -and
    $fasterCudaRuntime.totalBytes -eq $foundryCudaEpTotalBytes -and
    $fasterCudaRuntime.manifestSha256 -eq $foundryCudaEpManifestSha256) {
    $fasterCudaEnvironment["PATH"] = "$fasterCudaLibraryDirectory;$env:PATH"
}
$cudaArtifactPath = Join-Path $workRoot "faster-whisper-cuda-transcript.json"
$cudaMeasurement = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -Arguments @(
    $asrScriptPath, "run-faster", "--candidate-id", "faster-whisper-small-cuda-float16",
    "--model-path", $fasterModelPath, "--control-config", $controlConfigPath,
    "--control-audio", $controlAudioPath, "--fixture-audio", $fixtureAudioPath,
    "--device", "cuda", "--compute-type", "float16", "--output-artifact", $cudaArtifactPath
) -EnvironmentVariables $fasterCudaEnvironment
$cudaDiagnosticPath = Join-Path $workRoot "faster-whisper-cuda.stderr.txt"
Write-LocalDiagnosticAtomically -Path $cudaDiagnosticPath -Text $cudaMeasurement.stderr -PathReplacements $diagnosticPathReplacements
$cudaSummary = ConvertFrom-LastJsonLine -Text $cudaMeasurement.stdout
$cudaCandidate = New-CandidateRecord -Summary $cudaSummary -Measurement $cudaMeasurement -DiagnosticDescriptor (Get-FileDescriptor -Path $cudaDiagnosticPath)
$cudaCandidate | Add-Member -NotePropertyName cudaRuntime -NotePropertyValue ([pscustomobject][ordered]@{
    source = if ($fasterCudaEnvironment.Count -eq 1) { "Foundry Local CUDA execution-provider cache" } else { "not-available" }
    pathInjectedOnlyForChildProcess = $fasterCudaEnvironment.Count -eq 1
    manifestSha256 = if ($null -eq $fasterCudaRuntime) { $null } else { $fasterCudaRuntime.manifestSha256 }
    releaseRedistributionApproval = $false
})
$cudaComparableTiming = $null
if ($cudaSummary.status -eq "succeeded") {
    $cudaComparableTiming = [pscustomobject][ordered]@{
        modelLoadMilliseconds = [math]::Round([double]$cudaSummary.timing.modelLoadSeconds * 1000, 3)
        controlTranscriptionMilliseconds = [math]::Round([double]$cudaSummary.timing.controlTranscriptionSeconds * 1000, 3)
        fixtureCoverageMilliseconds = [math]::Round([double]$cudaSummary.timing.fixtureTranscriptionSeconds * 1000, 3)
        fixtureCoveragePath = "single-full-normalized-wav"
        diagnosticWholeFileInvocationExcluded = $true
        singleObservation = $true
    }
}
$cudaCandidate | Add-Member -NotePropertyName comparableTiming -NotePropertyValue $cudaComparableTiming

$foundryPackage = Get-AppxPackage -Name "Microsoft.FoundryLocal" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $foundryPackage) {
    $foundryCandidate = [pscustomobject][ordered]@{
        candidateId = "foundry-local-whisper-small-cuda-30s-windows"
        status = "failed"
        engine = "Foundry Local CLI"
        model = [ordered]@{ modelId = $foundryModelId; availabilityVerified = $false }
        configuration = [ordered]@{ language = "ja"; outputFormat = "json" }
        control = $null
        fixture = $null
        timing = $null
        processMeasurement = [ordered]@{ notRun = $true }
        artifact = $null
        error = [ordered]@{
            code = "FOUNDRY_LOCAL_NOT_AVAILABLE"
            message = "Foundry Local is not installed; the optional candidate was not run."
        }
        diagnostic = $null
        transcriptTextIncludedInPayload = $false
        comparableTiming = $null
    }
    $foundryToolingRecord = [pscustomobject][ordered]@{
        available = $false
        expectedCliVersion = "0.10.3"
        preview = $true
        reason = "Microsoft.FoundryLocal Appx package was not found."
    }
    $foundryModelRecord = [pscustomobject][ordered]@{
        available = $false
        expectedModelId = $foundryModelId
        expectedManifestSha256 = $foundryModelManifestSha256
    }
}
else {
$foundryCliBinary = Get-ChildItem -LiteralPath $foundryPackage.InstallLocation -File -Filter "foundry.exe" | Select-Object -First 1
$foundryDaemonBinary = Get-ChildItem -LiteralPath $foundryPackage.InstallLocation -File -Filter "foundrylocald.exe" | Select-Object -First 1
if ($null -eq $foundryCliBinary -or $null -eq $foundryDaemonBinary) {
    throw "Foundry Local package binaries could not be located."
}
$foundryPath = $foundryCliBinary.FullName
$foundryVersionResult = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("--version")
$foundryVersion = $foundryVersionResult.stdout.Trim()
if ($foundryVersion -ne "0.10.3") {
    throw "Expected Foundry Local CLI 0.10.3 but found $foundryVersion."
}
$foundryServerStatusBefore = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("server", "status")
$foundryServerWasReady = $foundryServerStatusBefore.exitCode -eq 0 -and $foundryServerStatusBefore.stdout -like "*Ready*"
$cacheLocationResult = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("cache", "location")
if ($cacheLocationResult.exitCode -ne 0) {
    throw "Foundry Local cache location could not be determined."
}
$foundryModelsRoot = $null
foreach ($line in @($cacheLocationResult.stdout -split "`r?`n")) {
    if ($line -match '(?<path>[A-Za-z]:\\.+)$') {
        $candidate = $Matches.path.Trim()
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $foundryModelsRoot = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($foundryModelsRoot) -or (Split-Path -Leaf $foundryModelsRoot) -ne "models") {
    throw "Foundry Local cache location did not identify an existing models directory."
}
$foundryModelPath = Join-Path $foundryModelsRoot "Microsoft\$foundryModelDirectoryName\$foundryModelVersionDirectory"
if (-not (Test-Path -LiteralPath $foundryModelPath -PathType Container)) {
    $download = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 3600 -Arguments @("model", "download", $foundryModelId)
    if ($download.exitCode -ne 0) {
        throw "Foundry Local could not download the pinned Whisper model."
    }
}
$foundryModel = Get-DirectoryManifest -Root $foundryModelPath
if ($foundryModel.fileCount -ne $foundryModelFileCount -or $foundryModel.totalBytes -ne $foundryModelTotalBytes -or
    $foundryModel.manifestSha256 -ne $foundryModelManifestSha256) {
    throw "Foundry Local Whisper model files do not match the pinned v3 manifest."
}
$foundryHome = Split-Path -Parent (Split-Path -Parent $foundryModelsRoot)
$foundryCudaEpPath = Join-Path $foundryHome "ep\cuda-ep"
$foundryCudaEp = Get-DirectoryManifest -Root $foundryCudaEpPath -Recursive
if ($foundryCudaEp.fileCount -ne $foundryCudaEpFileCount -or
    $foundryCudaEp.totalBytes -ne $foundryCudaEpTotalBytes -or
    $foundryCudaEp.manifestSha256 -ne $foundryCudaEpManifestSha256) {
    throw "Foundry Local CUDA execution-provider files do not match the pinned manifest."
}
$foundryCudaVersionPath = Join-Path $foundryCudaEpPath "version.json"
if ((Get-FileSha256 -Path $foundryCudaVersionPath) -ne $foundryCudaEpVersionFileSha256) {
    throw "Foundry Local CUDA execution-provider version metadata failed verification."
}
$foundryCudaVersion = Get-Content -LiteralPath $foundryCudaVersionPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($foundryCudaVersion.components.onnxruntime_providers_cuda.version -ne "1.26.20260520.2.756157b" -or
    $foundryCudaVersion.components.onnxruntime_genai_cuda.version -ne "0.14.1" -or
    $foundryCudaVersion.components.cuda_binaries.version -ne "12.8.3") {
    throw "Foundry Local CUDA execution-provider component versions differ from the pinned values."
}
$modelInfo = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("model", "info", "whisper-small", "--output", "json")
$modelInfoValue = ConvertFrom-LastJsonLine -Text $modelInfo.stdout
if ($modelInfo.exitCode -ne 0 -or $modelInfoValue.model.id -ne $foundryModelId -or
    $modelInfoValue.model.alias -ne "whisper-small" -or $modelInfoValue.model.type -ne "Speech" -or
    $modelInfoValue.model.device -ne "Gpu" -or $modelInfoValue.model.publisher -ne "Microsoft" -or
    $modelInfoValue.model.license -ne "apache-2.0" -or $modelInfoValue.model.cached -ne $true -or
    @($modelInfoValue.model.variants | Where-Object {
        $_.id -eq $foundryModelId -and $_.device -eq "Gpu" -and
        $_.executionProvider -eq "CUDAExecutionProvider" -and $_.cached -eq $true
    }).Count -ne 1) {
    throw "Foundry Local catalog info did not identify the pinned model and declared license."
}
$loadedBeforeResult = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("model", "list", "--loaded", "--variants", "--output", "json")
if ($loadedBeforeResult.exitCode -ne 0) {
    throw "Foundry Local loaded-model state could not be inspected before the benchmark."
}
$loadedBefore = ConvertFrom-LastJsonLine -Text $loadedBeforeResult.stdout
$foundryModelWasLoadedBefore = @($loadedBefore.variants | Where-Object { $_.variantId -eq $foundryModelId }).Count -eq 1
$foundryModelUnloadAttempted = $false
$foundryModelUnloadSucceeded = $false
$foundryServerStopAttempted = $false
$foundryServerStopSucceeded = $false
$foundryModelLoadedAfter = $null
$foundryServerReadyAfter = $null
try {
    if (-not $foundryServerWasReady) {
        $serverStart = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -Arguments @("server", "start")
        if ($serverStart.exitCode -ne 0) {
            throw "Foundry Local server start failed."
        }
    }
    $foundryDaemons = @(Get-Process -Name "foundrylocald" -ErrorAction Stop)
    if ($foundryDaemons.Count -ne 1) {
        throw "Expected exactly one Foundry Local daemon process."
    }
    $foundryDaemonId = $foundryDaemons[0].Id
    $foundryDaemons[0].Dispose()

    $foundryControlMeasurement = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 1800 -MonitorProcessIds @($foundryDaemonId) -Arguments @(
    "transcribe", "--model", $foundryModelId, "--language", "ja", "--file", $controlAudioPath, "--output", "json"
    )
$foundryControlDiagnosticPath = Join-Path $workRoot "foundry-control.stderr.txt"
Write-LocalDiagnosticAtomically -Path $foundryControlDiagnosticPath -Text $foundryControlMeasurement.stderr -PathReplacements $diagnosticPathReplacements
$foundryDirectFixtureMeasurement = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds $processTimeoutSeconds -MonitorProcessIds @($foundryDaemonId) -Arguments @(
    "transcribe", "--model", $foundryModelId, "--language", "ja", "--file", $fixtureAudioPath, "--output", "json"
)
$foundryDirectDiagnosticPath = Join-Path $workRoot "foundry-direct-fixture.stderr.txt"
Write-LocalDiagnosticAtomically -Path $foundryDirectDiagnosticPath -Text $foundryDirectFixtureMeasurement.stderr -PathReplacements $diagnosticPathReplacements

$foundryChunkDirectory = Join-Path $workRoot "foundry-chunks"
$foundryChunkResultDirectory = Join-Path $workRoot "foundry-chunk-results"
$foundryChunkDiagnosticDirectory = Join-Path $workRoot "foundry-chunk-diagnostics"
[IO.Directory]::CreateDirectory($foundryChunkDirectory) | Out-Null
[IO.Directory]::CreateDirectory($foundryChunkResultDirectory) | Out-Null
[IO.Directory]::CreateDirectory($foundryChunkDiagnosticDirectory) | Out-Null
$chunkPattern = Join-Path $foundryChunkDirectory "chunk-%04d.wav"
$chunkBuild = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 600 -Arguments @(
    "-v", "error", "-i", $fixtureAudioPath, "-map", "0:a:0", "-map_metadata", "-1",
    "-f", "segment", "-segment_time", "30", "-reset_timestamps", "1", "-c:a", "pcm_s16le",
    "-fflags", "+bitexact", "-flags:a", "+bitexact", "-y", $chunkPattern
)
if ($chunkBuild.exitCode -ne 0) {
    throw "Foundry Local fixture chunk generation failed."
}
$foundryChunkManifestPath = Join-Path $workRoot "foundry-chunk-manifest.json"
$describeChunks = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
    $asrScriptPath, "describe-wav-chunks", "--source-audio", $fixtureAudioPath,
    "--chunk-directory", $foundryChunkDirectory,
    "--chunk-duration-seconds", "30", "--output-manifest", $foundryChunkManifestPath
)
if ($describeChunks.exitCode -ne 0) {
    throw "Foundry Local fixture chunk manifest generation failed."
}
$foundryChunkManifest = Get-Content -LiteralPath $foundryChunkManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$chunkMeasurements = [Collections.Generic.List[object]]::new()
$allFoundryChunksSucceeded = $true
foreach ($chunk in @($foundryChunkManifest.chunks)) {
    $chunkAudioPath = Join-Path $foundryChunkDirectory $chunk.fileName
    $chunkMeasurement = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 1800 -MonitorProcessIds @($foundryDaemonId) -Arguments @(
        "transcribe", "--model", $foundryModelId, "--language", "ja", "--file", $chunkAudioPath, "--output", "json"
    )
    $chunkMeasurements.Add($chunkMeasurement)
    $chunkDiagnosticPath = Join-Path $foundryChunkDiagnosticDirectory ("chunk-{0:D4}.stderr.txt" -f [int]$chunk.index)
    Write-LocalDiagnosticAtomically -Path $chunkDiagnosticPath -Text $chunkMeasurement.stderr -PathReplacements $diagnosticPathReplacements
    if ($chunkMeasurement.exitCode -ne 0) {
        $allFoundryChunksSucceeded = $false
        continue
    }
    $chunkValue = ConvertFrom-LastJsonLine -Text $chunkMeasurement.stdout
    $chunkResultPath = Join-Path $foundryChunkResultDirectory ("chunk-{0:D4}.json" -f [int]$chunk.index)
    Write-LocalTextAtomically -Path $chunkResultPath -Text ($chunkValue | ConvertTo-Json -Compress -Depth 8)
}

$foundryCandidate = $null
if ($foundryControlMeasurement.exitCode -eq 0 -and $foundryDirectFixtureMeasurement.exitCode -eq 0 -and $allFoundryChunksSucceeded) {
    $foundryControlValue = ConvertFrom-LastJsonLine -Text $foundryControlMeasurement.stdout
    $foundryDirectFixtureValue = ConvertFrom-LastJsonLine -Text $foundryDirectFixtureMeasurement.stdout
    $foundryControlResultPath = Join-Path $workRoot "foundry-control-result.json"
    $foundryDirectFixtureResultPath = Join-Path $workRoot "foundry-direct-fixture-result.json"
    Write-LocalTextAtomically -Path $foundryControlResultPath -Text ($foundryControlValue | ConvertTo-Json -Compress -Depth 8)
    Write-LocalTextAtomically -Path $foundryDirectFixtureResultPath -Text ($foundryDirectFixtureValue | ConvertTo-Json -Compress -Depth 8)
    $foundryArtifactPath = Join-Path $workRoot "foundry-local-gpu-transcript.json"
    $foundrySummaryProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
        $asrScriptPath, "summarize-foundry-chunks", "--candidate-id", "foundry-local-whisper-small-cuda-30s-windows",
        "--model-id", $foundryModelId, "--model-manifest-sha256", $foundryModelManifestSha256,
        "--control-config", $controlConfigPath, "--control-audio", $controlAudioPath,
        "--fixture-audio", $fixtureAudioPath, "--fixture-duration-seconds", "$($fixtureAudio.durationSeconds)",
        "--control-result", $foundryControlResultPath, "--direct-fixture-result", $foundryDirectFixtureResultPath,
        "--chunk-manifest", $foundryChunkManifestPath, "--chunk-result-directory", $foundryChunkResultDirectory,
        "--output-artifact", $foundryArtifactPath
    )
    if ($foundrySummaryProcess.exitCode -ne 0) {
        throw "Foundry Local transcript summarization failed."
    }
    $foundrySummary = ConvertFrom-LastJsonLine -Text $foundrySummaryProcess.stdout
    $foundryMeasurements = @($foundryControlMeasurement) + @($chunkMeasurements)
    $foundryGpuPeaks = @($foundryMeasurements | ForEach-Object { $_.systemWideGpuMemoryUsedMiBPeak } | Where-Object { $null -ne $_ })
    $foundryGpuBaseline = $foundryControlMeasurement.systemWideGpuMemoryUsedMiBBaseline
    $foundryGpuPeak = if ($foundryGpuPeaks.Count -eq 0) { $null } else { [int64](($foundryGpuPeaks | Measure-Object -Maximum).Maximum) }
    $foundryChunkElapsedMilliseconds = [math]::Round((@($chunkMeasurements | ForEach-Object { $_.elapsedMilliseconds }) | Measure-Object -Sum).Sum, 3)
    $foundryMeasurement = [pscustomobject][ordered]@{
        exitCode = 0
        elapsedMilliseconds = [math]::Round($foundryControlMeasurement.elapsedMilliseconds + $foundryChunkElapsedMilliseconds, 3)
        baselineAggregateWorkingSetBytes = $foundryControlMeasurement.baselineAggregateWorkingSetBytes
        peakAggregateWorkingSetBytes = [int64]((@($foundryMeasurements | ForEach-Object { $_.peakAggregateWorkingSetBytes }) | Measure-Object -Maximum).Maximum)
        systemWideGpuMemoryUsedMiBBaseline = $foundryGpuBaseline
        systemWideGpuMemoryUsedMiBPeak = $foundryGpuPeak
        systemWideGpuMemoryUsedMiBPeakDelta = if ($null -eq $foundryGpuBaseline -or $null -eq $foundryGpuPeak) { $null } else { [math]::Max(0, $foundryGpuPeak - $foundryGpuBaseline) }
        gpuMemoryScope = $foundryControlMeasurement.gpuMemoryScope
        workingSetScope = $foundryControlMeasurement.workingSetScope
        observedProcessIdCount = [int](($foundryMeasurements | ForEach-Object { $_.observedProcessIdCount } | Measure-Object -Maximum).Maximum)
    }
    $foundrySummary | Add-Member -NotePropertyName timing -NotePropertyValue ([pscustomobject][ordered]@{
        controlTranscriptionMilliseconds = $foundryControlMeasurement.elapsedMilliseconds
        directWholeFileDiagnosticMilliseconds = $foundryDirectFixtureMeasurement.elapsedMilliseconds
        chunkGenerationMilliseconds = $chunkBuild.elapsedMilliseconds
        chunkTranscriptionMilliseconds = $foundryChunkElapsedMilliseconds
        chunkCount = [int]$foundryChunkManifest.chunkCount
        directEngineReportedDurationSeconds = $foundryDirectFixtureValue.durationSeconds
    })
    $foundryDiagnostics = [pscustomobject][ordered]@{
        control = Get-FileDescriptor -Path $foundryControlDiagnosticPath
        directFixture = Get-FileDescriptor -Path $foundryDirectDiagnosticPath
        chunks = Get-DirectoryManifest -Root $foundryChunkDiagnosticDirectory
    }
    $foundryCandidate = New-CandidateRecord -Summary $foundrySummary -Measurement $foundryMeasurement -DiagnosticDescriptor $foundryDiagnostics
    $foundryCandidate | Add-Member -NotePropertyName diagnosticWholeFileMeasurement -NotePropertyValue ([pscustomobject][ordered]@{
        excludedFromComparableTimingAndPrimaryResourceMeasurement = $true
        exitCode = $foundryDirectFixtureMeasurement.exitCode
        elapsedMilliseconds = $foundryDirectFixtureMeasurement.elapsedMilliseconds
        baselineAggregateWorkingSetBytes = $foundryDirectFixtureMeasurement.baselineAggregateWorkingSetBytes
        peakAggregateWorkingSetBytes = $foundryDirectFixtureMeasurement.peakAggregateWorkingSetBytes
        systemWideGpuMemoryUsedMiBBaseline = $foundryDirectFixtureMeasurement.systemWideGpuMemoryUsedMiBBaseline
        systemWideGpuMemoryUsedMiBPeak = $foundryDirectFixtureMeasurement.systemWideGpuMemoryUsedMiBPeak
        systemWideGpuMemoryUsedMiBPeakDelta = $foundryDirectFixtureMeasurement.systemWideGpuMemoryUsedMiBPeakDelta
        gpuMemoryScope = $foundryDirectFixtureMeasurement.gpuMemoryScope
        workingSetScope = $foundryDirectFixtureMeasurement.workingSetScope
        observedProcessIdCount = $foundryDirectFixtureMeasurement.observedProcessIdCount
    })
    $foundryCandidate | Add-Member -NotePropertyName comparableTiming -NotePropertyValue ([pscustomobject][ordered]@{
        modelLoadMilliseconds = $null
        controlTranscriptionMilliseconds = $foundryControlMeasurement.elapsedMilliseconds
        fixtureCoverageMilliseconds = [math]::Round($chunkBuild.elapsedMilliseconds + $foundryChunkElapsedMilliseconds, 3)
        adapterPreparationMilliseconds = $chunkBuild.elapsedMilliseconds
        engineInvocationMilliseconds = $foundryChunkElapsedMilliseconds
        fixtureCoveragePath = "complete-ordered-30-second-window-adapter"
        diagnosticWholeFileInvocationExcluded = $true
        singleObservation = $true
    })
}
else {
    $foundryCandidate = [pscustomobject][ordered]@{
        candidateId = "foundry-local-whisper-small-cuda-30s-windows"
        status = "failed"
        engine = "Foundry Local CLI"
        model = [ordered]@{ modelId = $foundryModelId; manifestSha256 = $foundryModelManifestSha256 }
        configuration = [ordered]@{ language = "ja"; outputFormat = "json" }
        control = $null
        fixture = $null
        timing = $null
        processMeasurement = [ordered]@{
            controlExitCode = $foundryControlMeasurement.exitCode
            directFixtureExitCode = $foundryDirectFixtureMeasurement.exitCode
            chunkCount = [int]$foundryChunkManifest.chunkCount
            failedChunkCount = @($chunkMeasurements | Where-Object { $_.exitCode -ne 0 }).Count
        }
        artifact = $null
        error = [ordered]@{
            code = "FOUNDRY_LOCAL_TRANSCRIBE_FAILED"
            message = "Foundry Local failed during control or fixture transcription; see local hashed diagnostics."
        }
        diagnostic = [ordered]@{
            control = Get-FileDescriptor -Path $foundryControlDiagnosticPath
            directFixture = Get-FileDescriptor -Path $foundryDirectDiagnosticPath
            chunks = Get-DirectoryManifest -Root $foundryChunkDiagnosticDirectory
        }
        transcriptTextIncludedInPayload = $false
        comparableTiming = $null
    }
}
}
finally {
    if (-not $foundryModelWasLoadedBefore) {
        $foundryModelUnloadAttempted = $true
        $unload = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -Arguments @("model", "unload", $foundryModelId)
        $foundryModelUnloadSucceeded = $unload.exitCode -eq 0
        if (-not $foundryModelUnloadSucceeded) {
            throw "Foundry Local model cleanup failed."
        }
    }
    $loadedAfterResult = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("model", "list", "--loaded", "--variants", "--output", "json")
    if ($loadedAfterResult.exitCode -ne 0) {
        throw "Foundry Local loaded-model state could not be inspected after cleanup."
    }
    $loadedAfter = ConvertFrom-LastJsonLine -Text $loadedAfterResult.stdout
    $foundryModelLoadedAfter = @($loadedAfter.variants | Where-Object { $_.variantId -eq $foundryModelId }).Count -eq 1
    if ($foundryModelLoadedAfter -ne $foundryModelWasLoadedBefore) {
        throw "Foundry Local model state was not restored to its pre-benchmark state."
    }
    if (-not $foundryServerWasReady) {
        $foundryServerStopAttempted = $true
        $serverStop = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -Arguments @("server", "stop")
        $foundryServerStopSucceeded = $serverStop.exitCode -eq 0
        if (-not $foundryServerStopSucceeded) {
            throw "Foundry Local server cleanup failed."
        }
    }
    $foundryServerStatusAfter = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -Arguments @("server", "status")
    $foundryServerReadyAfter = $foundryServerStatusAfter.exitCode -eq 0 -and $foundryServerStatusAfter.stdout -like "*Ready*"
    if ($foundryServerReadyAfter -ne $foundryServerWasReady) {
        throw "Foundry Local server state was not restored to its pre-benchmark state."
    }
}
$foundryToolingRecord = [pscustomobject][ordered]@{
    available = $true
    cliVersion = $foundryVersion
    packageVersion = $foundryPackage.Version.ToString()
    architecture = $foundryPackage.Architecture.ToString()
    signatureKind = $foundryPackage.SignatureKind.ToString()
    cliExecutable = Get-FileDescriptor -Path $foundryCliBinary.FullName
    daemonExecutable = Get-FileDescriptor -Path $foundryDaemonBinary.FullName
    modelInfoOutputSha256 = Get-StringSha256 -Text $modelInfo.stdout
    catalog = [ordered]@{
        alias = $modelInfoValue.model.alias
        modelId = $modelInfoValue.model.id
        type = $modelInfoValue.model.type
        device = $modelInfoValue.model.device
        publisher = $modelInfoValue.model.publisher
        licenseDeclaredByCatalog = $modelInfoValue.model.license
        executionProvider = ($modelInfoValue.model.variants | Where-Object { $_.id -eq $foundryModelId } | Select-Object -First 1).executionProvider
        cached = [bool]$modelInfoValue.model.cached
    }
    stateRestoration = [ordered]@{
        serverWasReadyBefore = $foundryServerWasReady
        modelWasLoadedBefore = $foundryModelWasLoadedBefore
        modelUnloadAttempted = $foundryModelUnloadAttempted
        modelUnloadSucceeded = $foundryModelUnloadSucceeded
        modelLoadedAfter = $foundryModelLoadedAfter
        serverStopAttempted = $foundryServerStopAttempted
        serverStopSucceeded = $foundryServerStopSucceeded
        serverReadyAfter = $foundryServerReadyAfter
        preAndPostStateMatch = $true
    }
    preview = $true
}
$foundryModelRecord = [pscustomobject][ordered]@{
    available = $true
    modelId = $foundryModelId
    modelDirectoryVersion = $foundryModelVersionDirectory
    licenseDeclaredByCatalog = "apache-2.0"
    fileCount = $foundryModel.fileCount
    totalBytes = $foundryModel.totalBytes
    manifestRule = $foundryModel.manifestRule
    manifestSha256 = $foundryModel.manifestSha256
    files = $foundryModel.files
    executionProvider = "CUDAExecutionProvider"
    executionProviderManifest = $foundryCudaEp
    executionProviderVersionFile = [ordered]@{
        byteSize = [int64](Get-Item -LiteralPath $foundryCudaVersionPath).Length
        sha256 = Get-FileSha256 -Path $foundryCudaVersionPath
        components = $foundryCudaVersion.components
    }
}
}

$candidates = @($cpuCandidate, $cudaCandidate, $foundryCandidate)
$successfulCandidates = @($candidates | Where-Object { $_.status -eq "succeeded" })
$fixtureTextHashes = @($successfulCandidates | ForEach-Object {
    [pscustomobject][ordered]@{ candidateId = $_.candidateId; textSha256 = $_.fixture.textSha256 }
})
$workAcl = Get-LocalEvidenceAclEvidence -Path $workRoot
$transcriptReferencePath = Join-Path $repositoryRoot "benchmarks\annotations\transcript-reference.jsonl"

$payload = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        hardwareBenchmarkRunId = $hardware.benchmarkRunId
        hardwareBenchmarkRunSha256 = $hardware.benchmarkRunSha256
        operatingSystemVersion = $hardware.operatingSystem.version
        processor = $hardware.processor.name
        logicalProcessors = $hardware.processor.logicalProcessors
        graphics = @($hardware.graphics | ForEach-Object { $_.name })
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    implementation = [ordered]@{
        gitHead = $gitHeadResult.stdout.Trim()
        implementationFilesCleanAtRun = [string]::IsNullOrWhiteSpace($gitStatusResult.stdout)
        files = $implementationFiles
    }
    tooling = [ordered]@{
        ffmpegPackageId = $ffmpegCapabilities.package.id
        ffmpegSha256 = $ffmpegCapabilities.executables.ffmpeg.sha256
        ffprobeSha256 = $ffmpegCapabilities.executables.ffprobe.sha256
        pythonVersion = (& $pythonPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
        pythonPackages = $pythonPackages
        foundryLocal = $foundryToolingRecord
    }
    analysisInput = [ordered]@{
        sourceAssetId = $fixtureManifest.sourceAssets[0].id
        sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
        mediaIngestBenchmarkRunId = $mediaIngest.benchmarkRunId
        mediaIngestBenchmarkRunSha256 = $mediaIngest.benchmarkRunSha256
        sourceAudio = [ordered]@{
            streamIndex = [int]$sourceAudioStream.index
            codecName = $sourceAudioStream.codecName
            sourceTimeBase = $sourceAudioStream.timeBase
            sourceStartSeconds = [double]$sourceAudioStream.startSeconds
            sourceDurationSeconds = [double]$sourceAudioStream.durationSeconds
            sourceSampleRate = [int]$sourceAudioStream.sampleRate
            sourceChannels = [int]$sourceAudioStream.channels
            sourceChannelLayout = $sourceAudioStream.channelLayout
            observedNegativePacketStartSeconds = $mediaIngest.timingAssessment.negativeStartSecondsAudio
            normalizedDecodedTimelineStartSeconds = 0.0
            timelineAuthority = "ASR times are relative to the decoded normalized WAV. ST-003 is the authority for source packet timing; ST-007 evaluates alignment and source mapping."
        }
        normalizedFixtureAudio = $fixtureAudio
        normalization = [ordered]@{
            channels = 1
            sampleRate = 16000
            sampleFormat = "signed 16-bit PCM little-endian"
            metadataRemoved = $true
        }
    }
    models = [ordered]@{
        fasterWhisperSmall = $fasterModel
        foundryLocalWhisperSmall = $foundryModelRecord
    }
    control = [ordered]@{
        controlId = $controlConfig.controlId
        configSha256 = Get-FileSha256 -Path $controlConfigPath
        referenceTextSha256 = Get-StringSha256 -Text $controlConfig.referenceText.Normalize([Text.NormalizationForm]::FormC)
        voice = [ordered]@{
            name = $voice.VoiceInfo.Name
            culture = $voice.VoiceInfo.Culture.Name
            gender = $voice.VoiceInfo.Gender.ToString()
        }
        audio = $controlAudio
        accuracyThresholdApproved = $false
        purpose = $controlConfig.purpose
    }
    candidates = $candidates
    comparison = [ordered]@{
        sameNormalizedFixtureAudioForAllCandidates = $true
        normalizedFixtureAudioSha256 = $fixtureAudio.sha256
        successfulCandidateCount = $successfulCandidates.Count
        fixtureTranscriptHashes = $fixtureTextHashes
        comparableTimings = @($candidates | ForEach-Object {
            [pscustomobject][ordered]@{
                candidateId = $_.candidateId
                status = $_.status
                timing = $_.comparableTiming
            }
        })
        fixtureReferenceTranscriptAvailable = Test-Path -LiteralPath $transcriptReferencePath -PathType Leaf
        fixtureCerMeasured = $false
        fixtureWerMeasured = $false
        fixtureProperNounErrorRateMeasured = $false
        controlCerIsSyntheticMechanismEvidenceOnly = $true
        fixturePerformanceTrialCountPerCandidate = 1
        performanceDistributionMeasured = $false
        timingComparisonStatus = "single-observation-with-documented-adapter-boundaries"
        segmentTimestampComparisonAvailable = @($successfulCandidates | Where-Object { $_.fixture.segmentTimestampsAvailable -eq $true }).Count -ge 2
        wordTimestampComparisonAvailable = @($successfulCandidates | Where-Object { $_.fixture.wordTimestampsAvailable -eq $true }).Count -ge 2
        productionThresholdsApproved = $false
    }
    annotations = [ordered]@{
        transcriptReferenceAvailable = Test-Path -LiteralPath $transcriptReferencePath -PathType Leaf
        precisionOrAccuracyClaimedForFixture = $false
        missingReferenceRelativePath = "benchmarks/annotations/transcript-reference.jsonl"
    }
    privacy = [ordered]@{
        localEvidenceAcl = $workAcl
        sourceAudioPersistedOnlyInIgnoredWorkDirectory = $true
        sourceTranscriptTextIncludedInPayload = $false
        sourceTranscriptTextIncludedInCanonicalSummary = $false
        localTranscriptArtifactsContainRightsUnconfirmedText = $true
        absoluteHostPathsIncludedInPayload = $false
        repositoryRelativeEvidenceReferencesIncludedInPayload = $true
    }
    samplePathConsumed = $true
}
$payloadJson = $payload | ConvertTo-Json -Depth 14
if ($payloadJson -match '"(?:text|content|transcript|referenceText|recognizedText)"\s*:') {
    throw "ASR payload projection contains a forbidden transcript content property."
}
$localPayloadPath = Join-Path $workRoot "stage-payload.json"
Write-LocalTextAtomically -Path $localPayloadPath -Text $payloadJson
$completionMarker = Join-Path $workRoot "completed.json"
Write-LocalTextAtomically -Path $completionMarker -Text (([ordered]@{
    schemaVersion = "1.0.0"
    completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
    candidateCount = $candidates.Count
}) | ConvertTo-Json -Compress)
$script:asrStageCompleted = $true
$payloadJson
