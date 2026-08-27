#Requires -Version 7.0

<#
.SYNOPSIS
Evaluates cached Foundry Local chat models for grounded-ID JSON candidate generation.

.DESCRIPTION
Runs two exact cached CUDA variants against a safe synthetic mechanism and prompt-injection
control plus three deterministic windows from the rights-unconfirmed local ASR artifact. Generated
wording and source text remain in an ignored, ACL-protected work directory. The benchmark payload
contains only hashes, counts, structural validation, timing, memory, state-restoration, and fallback
evidence. It does not claim semantic groundedness without an independent human reference.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 1800
$gpuMemorySamplingIntervalMilliseconds = 1000
$foundryCliVersion = "0.10.3"
$foundryCliSha256 = "53a805895debd297bc5fe424bdbf64f551efbe0c53ddae397ecae9f5bcc7db98"
$foundryDaemonSha256 = "a49c23d4af9f470da7128c29eef3fb1c0a64cf8555c2a3500e8683855886f3d1"
$cudaEpManifestSha256 = "8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021"
$cudaEpFileCount = 14
$cudaEpTotalBytes = 2423764787
$cudaEpVersionFileSha256 = "28236ce308e5232254f4f09ca8e6df0805b9a3608a79d9e4d09a4b459fb99640"
$manifestRule = "SHA-256 of UTF-8 sorted relativePath NUL byteSize NUL fileSha256 LF records"
$modelSpecs = @(
    [pscustomobject][ordered]@{
        candidateId = "foundry-local-qwen3-0.6b-cuda"
        alias = "qwen3-0.6b"
        modelId = "qwen3-0.6b-cuda-gpu:2"
        modelDirectoryName = "qwen3-0.6b-cuda-gpu-2"
        versionDirectoryName = "v2"
        expectedFileCount = 8
        expectedTotalBytes = 516135598
        expectedManifestSha256 = "38dc1ac88c6af4a684f96e8a9b4c30a6bca2671115a84e15189d93d57a10ebbe"
        expectedContextLength = 40960
    },
    [pscustomobject][ordered]@{
        candidateId = "foundry-local-qwen3-4b-cuda"
        alias = "qwen3-4b"
        modelId = "qwen3-4b-cuda-gpu:2"
        modelDirectoryName = "qwen3-4b-cuda-gpu-2"
        versionDirectoryName = "v2"
        expectedFileCount = 9
        expectedTotalBytes = 2823706996
        expectedManifestSha256 = "89d232e91d2c130e5ffd38f03776ad47c3b9779c8689e565fd4b2ea483f6e657"
        expectedContextLength = 40960
    }
)

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StringSha256 {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))
    ).ToLowerInvariant()
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
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $staging = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($staging, $Value, [Text.UTF8Encoding]::new($false))
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
        throw "The current Windows SID could not be resolved for local text-model evidence protection."
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
        throw "The local text-model evidence directory ACL did not match the approved local principals."
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
        Write-Verbose "The local text-model evidence directory requires ACL normalization."
    }
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for local text-model evidence protection."
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
        manifestSha256 = Get-StringSha256 -Value $records.ToString()
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
        throw "A timed-out local text-model process left $remaining observed processes running."
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
        [AllowEmptyCollection()][int[]]$MonitorProcessIds = @()
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
        foreach ($processId in $MonitorProcessIds) {
            [void]$observedIds.Add($processId)
        }
        foreach ($rootId in @($process.Id) + @($MonitorProcessIds)) {
            foreach ($processId in Get-DescendantProcessId -RootProcessId $rootId) {
                [void]$observedIds.Add($processId)
            }
        }
        $treeRefresh = [Diagnostics.Stopwatch]::StartNew()
        $baselineWorkingSet = Measure-AggregateWorkingSet -ProcessIds @($observedIds)
        $peakWorkingSet = $baselineWorkingSet
        while (-not $process.WaitForExit(250)) {
            if ($treeRefresh.ElapsedMilliseconds -ge 1000) {
                foreach ($rootId in @($process.Id) + @($MonitorProcessIds)) {
                    foreach ($processId in Get-DescendantProcessId -RootProcessId $rootId) {
                        [void]$observedIds.Add($processId)
                    }
                }
                $treeRefresh.Restart()
            }
            $workingSet = Measure-AggregateWorkingSet -ProcessIds @($observedIds)
            if ($workingSet -gt $peakWorkingSet) {
                $peakWorkingSet = $workingSet
            }
            if ($gpuStopwatch.ElapsedMilliseconds -ge $gpuMemorySamplingIntervalMilliseconds) {
                $gpuMemory = Get-SystemWideGpuMemoryUsedMiB
                if ($null -ne $gpuMemory -and ($null -eq $peakGpuMiB -or $gpuMemory -gt $peakGpuMiB)) {
                    $peakGpuMiB = $gpuMemory
                }
                $gpuStopwatch.Restart()
            }
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Invoke-ProcessTreeTermination -Process $process
                throw "$([IO.Path]::GetFileName($FileName)) exceeded the local text-model timeout of $TimeoutSeconds seconds."
            }
        }
        $process.WaitForExit()
        $workingSet = Measure-AggregateWorkingSet -ProcessIds $MonitorProcessIds
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
            gpuMemorySamplingIntervalMilliseconds = $gpuMemorySamplingIntervalMilliseconds
            gpuMemoryScope = "system-wide observation; WDDM did not expose per-process used_gpu_memory"
            workingSetScope = "root process, dynamically discovered descendants, and explicit Foundry daemon process"
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
    param([Parameter(Mandatory)][string]$Value)

    $lines = @($Value -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        try {
            return $lines[$index] | ConvertFrom-Json -Depth 100 -ErrorAction Stop
        }
        catch {
            continue
        }
    }
    throw "Local text-model process output did not contain a JSON object."
}

function Get-SafeProcessMeasurement {
    param([Parameter(Mandatory)][object]$Measurement)

    return [pscustomobject][ordered]@{
        exitCode = $Measurement.exitCode
        elapsedMilliseconds = $Measurement.elapsedMilliseconds
        baselineAggregateWorkingSetBytes = $Measurement.baselineAggregateWorkingSetBytes
        peakAggregateWorkingSetBytes = $Measurement.peakAggregateWorkingSetBytes
        systemWideGpuMemoryUsedMiBBaseline = $Measurement.systemWideGpuMemoryUsedMiBBaseline
        systemWideGpuMemoryUsedMiBPeak = $Measurement.systemWideGpuMemoryUsedMiBPeak
        systemWideGpuMemoryUsedMiBPeakDelta = $Measurement.systemWideGpuMemoryUsedMiBPeakDelta
        gpuMemorySamplingIntervalMilliseconds = $Measurement.gpuMemorySamplingIntervalMilliseconds
        gpuMemoryScope = $Measurement.gpuMemoryScope
        workingSetScope = $Measurement.workingSetScope
        observedProcessIdCount = $Measurement.observedProcessIdCount
    }
}

function Write-Diagnostic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][hashtable]$Replacements
    )

    $safe = $Value
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
    Write-LocalTextAtomically -Path $Path -Value $safe
}

function Get-LoadedModelId {
    param(
        [Parameter(Mandatory)][string]$FoundryPath,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    $result = Invoke-MeasuredProcess -FileName $FoundryPath -TimeoutSeconds 120 -WorkingDirectory $RepositoryRoot -Arguments @(
        "model", "list", "--loaded", "--variants", "--output", "json"
    )
    if ($result.exitCode -ne 0) {
        throw "Foundry Local loaded-model state could not be inspected."
    }
    $value = ConvertFrom-LastJsonLine -Value $result.stdout
    return @($value.variants | ForEach-Object { [string]$_.variantId } | Sort-Object -Unique)
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$fixtureManifestPath = Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json"
$fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if (@($fixtureManifest.sourceAssets).Count -ne 1 -or
    (Get-FileSha256 -Path $resolvedSample) -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "The local text-model source does not match the fixture manifest."
}

$hardwarePath = Join-Path $repositoryRoot "benchmarks\results\hardware-inventory.json"
$hardware = Get-Content -LiteralPath $hardwarePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
if ($hardware.operatingSystem.version -ne [Environment]::OSVersion.Version.ToString(3)) {
    throw "The current operating system differs from the hardware benchmark evidence."
}

$asrCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\asr-benchmark.json"
$asrCanonical = Get-Content -LiteralPath $asrCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$asrRawPath = Join-Path $repositoryRoot "benchmarks\results\runs\$($asrCanonical.benchmarkRunId).json"
if (-not (Test-Path -LiteralPath $asrRawPath -PathType Leaf) -or
    (Get-FileSha256 -Path $asrRawPath) -ne $asrCanonical.benchmarkRunSha256) {
    throw "The immutable ST-006 raw run is missing or has changed."
}
$asrRaw = Get-Content -LiteralPath $asrRawPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$cudaAsrCandidate = $asrRaw.payload.candidates |
    Where-Object candidateId -eq "faster-whisper-small-cuda-float16" |
    Select-Object -First 1
if ($asrRaw.status -ne "succeeded" -or $null -eq $cudaAsrCandidate -or
    $cudaAsrCandidate.status -ne "succeeded" -or
    $asrRaw.fixture.sourceSha256 -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "The local text-model benchmark requires the successful ST-006 CUDA fixture artifact."
}
$asrEvidenceMatches = @(Get-ChildItem (Join-Path $repositoryRoot "benchmarks\work") -Directory -Filter "asr-*" -Force | Where-Object {
    $artifactPath = Join-Path $_.FullName $cudaAsrCandidate.artifact.fileName
    (Test-Path -LiteralPath $artifactPath -PathType Leaf) -and
    (Get-FileSha256 -Path $artifactPath) -eq $cudaAsrCandidate.artifact.sha256
})
if ($asrEvidenceMatches.Count -ne 1) {
    throw "Expected one immutable ST-006 private evidence directory, found $($asrEvidenceMatches.Count)."
}
$asrEvidenceRoot = $asrEvidenceMatches[0].FullName
$asrEvidenceAcl = Get-LocalEvidenceAclEvidence -Path $asrEvidenceRoot
$asrArtifactPath = Join-Path $asrEvidenceRoot $cudaAsrCandidate.artifact.fileName
$asrCompletionPath = Join-Path $asrEvidenceRoot "completed.json"
if (-not (Test-Path -LiteralPath $asrCompletionPath -PathType Leaf)) {
    throw "The ST-006 private evidence has no completion marker."
}

$alignmentCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\alignment-benchmark.json"
$alignmentCanonical = Get-Content -LiteralPath $alignmentCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
if ($alignmentCanonical.configurationDisposition.alignmentEvaluationTarget -ne "faster-whisper-small-cuda-float16" -or
    $alignmentCanonical.upstream.cudaArtifact.sha256 -ne $cudaAsrCandidate.artifact.sha256 -or
    $alignmentCanonical.fixture.sourceSha256 -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "ST-007 does not identify the same provisional CUDA transcript source."
}

$controlConfigPath = Join-Path $repositoryRoot "benchmarks\fixtures\local-text-model-control.json"
$outputSchemaPath = Join-Path $repositoryRoot "benchmarks\fixtures\local-text-model-output.schema.json"
$driverPath = Join-Path $PSScriptRoot "text_model_benchmark.py"
$controlConfig = Get-Content -LiteralPath $controlConfigPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
if ($controlConfig.generation.productionApproved -ne $false -or
    $controlConfig.assessment.semanticGroundednessMeasured -ne $false -or
    $controlConfig.assessment.productionThresholdsApproved -ne $false) {
    throw "The local text-model control configuration overstates Stage 0 approval."
}

$implementationSpecs = @(
    @{ Id = "benchmark-harness"; Path = (Join-Path $repositoryRoot "benchmarks\run-benchmarks.ps1") },
    @{ Id = "local-text-wrapper"; Path = (Join-Path $repositoryRoot "benchmarks\stages\measure-text-model.ps1") },
    @{ Id = "local-text-driver"; Path = $driverPath },
    @{ Id = "benchmark-schema"; Path = (Join-Path $repositoryRoot "benchmarks\results\schema.json") },
    @{ Id = "local-text-output-schema"; Path = $outputSchemaPath },
    @{ Id = "local-text-control-config"; Path = $controlConfigPath }
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
    throw "Unable to record the Git HEAD for ST-011."
}
$gitStatusProcess = Invoke-MeasuredProcess -FileName $gitPath -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments (@("status", "--porcelain", "--") + @($implementationSpecs.Path))
if ($gitStatusProcess.exitCode -ne 0) {
    throw "Unable to record the ST-011 implementation working-tree state."
}

$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing."
}
$pythonVersionProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @("--version")
if ($pythonVersionProcess.exitCode -ne 0 -or $pythonVersionProcess.stdout.Trim() -ne "Python 3.12.10") {
    throw "The local text-model driver requires the pinned Python 3.12.10 environment."
}

$foundryPackage = Get-AppxPackage -Name "Microsoft.FoundryLocal" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $foundryPackage) {
    throw "Foundry Local is not installed."
}
$foundryCli = Get-ChildItem -LiteralPath $foundryPackage.InstallLocation -File -Filter "foundry.exe" | Select-Object -First 1
$foundryDaemon = Get-ChildItem -LiteralPath $foundryPackage.InstallLocation -File -Filter "foundrylocald.exe" | Select-Object -First 1
if ($null -eq $foundryCli -or $null -eq $foundryDaemon -or
    (Get-FileSha256 -Path $foundryCli.FullName) -ne $foundryCliSha256 -or
    (Get-FileSha256 -Path $foundryDaemon.FullName) -ne $foundryDaemonSha256) {
    throw "Foundry Local binaries do not match the Stage 0 intake record."
}
$foundryPath = $foundryCli.FullName
$versionProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @("--version")
if ($versionProcess.exitCode -ne 0 -or $versionProcess.stdout.Trim() -ne $foundryCliVersion) {
    throw "Foundry Local CLI version does not match the pinned Stage 0 version."
}

$cacheLocationProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @("cache", "location")
if ($cacheLocationProcess.exitCode -ne 0) {
    throw "Foundry Local cache location could not be determined."
}
$modelsRoot = $null
foreach ($line in @($cacheLocationProcess.stdout -split "`r?`n")) {
    if ($line -match '(?<path>[A-Za-z]:\\.+)$') {
        $candidatePath = $Matches.path.Trim()
        if (Test-Path -LiteralPath $candidatePath -PathType Container) {
            $modelsRoot = (Resolve-Path -LiteralPath $candidatePath).Path
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($modelsRoot) -or (Split-Path -Leaf $modelsRoot) -ne "models") {
    throw "Foundry Local cache location did not identify an existing models directory."
}
$foundryHome = Split-Path -Parent (Split-Path -Parent $modelsRoot)
$cudaEpPath = Join-Path $foundryHome "ep\cuda-ep"
$cudaEp = Get-DirectoryManifest -Root $cudaEpPath -Recursive
$cudaEpVersionPath = Join-Path $cudaEpPath "version.json"
if ($cudaEp.fileCount -ne $cudaEpFileCount -or $cudaEp.totalBytes -ne $cudaEpTotalBytes -or
    $cudaEp.manifestSha256 -ne $cudaEpManifestSha256 -or
    (Get-FileSha256 -Path $cudaEpVersionPath) -ne $cudaEpVersionFileSha256) {
    throw "Foundry Local CUDA execution-provider files do not match ST-006 evidence."
}

$modelRecords = [Collections.Generic.List[object]]::new()
$modelPaths = @{}
foreach ($spec in $modelSpecs) {
    $modelInfoProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
        "model", "info", $spec.alias, "--output", "json"
    )
    if ($modelInfoProcess.exitCode -ne 0) {
        throw "Foundry Local model catalog lookup failed."
    }
    $modelInfo = ConvertFrom-LastJsonLine -Value $modelInfoProcess.stdout
    $variant = $modelInfo.model.variants | Where-Object id -eq $spec.modelId | Select-Object -First 1
    if ($modelInfo.model.alias -ne $spec.alias -or $modelInfo.model.id -ne $spec.modelId -or
        $modelInfo.model.type -ne "Chat" -or $modelInfo.model.device -ne "Gpu" -or
        $modelInfo.model.publisher -ne "Microsoft" -or $modelInfo.model.license -ne "apache-2.0" -or
        [int]$modelInfo.model.contextLength -ne [int]$spec.expectedContextLength -or
        $modelInfo.model.supportsToolCalling -ne $true -or $modelInfo.model.cached -ne $true -or
        $null -eq $variant -or $variant.executionProvider -ne "CUDAExecutionProvider" -or $variant.cached -ne $true) {
        throw "Foundry Local catalog did not identify the exact cached CUDA chat model."
    }
    $modelPath = Join-Path $modelsRoot "Microsoft\$($spec.modelDirectoryName)\$($spec.versionDirectoryName)"
    if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) {
        throw "A pinned Foundry Local model cache directory is missing."
    }
    $manifest = Get-DirectoryManifest -Root $modelPath
    if ($manifest.fileCount -ne $spec.expectedFileCount -or
        $manifest.totalBytes -ne $spec.expectedTotalBytes -or
        $manifest.manifestSha256 -ne $spec.expectedManifestSha256) {
        throw "A pinned Foundry Local model manifest changed: $($spec.candidateId)"
    }
    $modelPaths[$spec.modelId] = $modelPath
    $modelRecords.Add([pscustomobject][ordered]@{
        candidateId = $spec.candidateId
        alias = $spec.alias
        modelId = $spec.modelId
        modelDirectoryVersion = $spec.versionDirectoryName
        publisher = $modelInfo.model.publisher
        licenseDeclaredByCatalog = $modelInfo.model.license
        independentDistributionLicenseReviewCompleted = $false
        contextLength = [int]$modelInfo.model.contextLength
        supportsToolCallingDeclaredByCatalog = [bool]$modelInfo.model.supportsToolCalling
        catalogCapabilityIsNotPerRequestSuccessEvidence = $true
        executionProvider = $variant.executionProvider
        cachedBeforeRun = [bool]$variant.cached
        modelInfoOutputSha256 = Get-StringSha256 -Value $modelInfoProcess.stdout.Trim()
        fileCount = $manifest.fileCount
        totalBytes = $manifest.totalBytes
        manifestRule = $manifest.manifestRule
        manifestSha256 = $manifest.manifestSha256
        files = $manifest.files
    })
}

$workRoot = Join-Path $repositoryRoot ("benchmarks\work\local-text-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$workAcl = Protect-LocalEvidenceDirectory -Path $workRoot
if (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -ne 0) {
    throw "The local text-model work directory was not empty immediately after ACL protection."
}
$diagnosticReplacements = @{
    $resolvedSample = "<SOURCE>"
    $repositoryRoot = "<WORKSPACE>"
    $workRoot = "<LOCAL_TEXT_WORK>"
    $asrEvidenceRoot = "<ASR_EVIDENCE>"
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $diagnosticReplacements[$env:USERPROFILE] = "<USERPROFILE>"
}

$serverStatusBeforeProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    "server", "status", "--output", "json"
)
$serverStatusBefore = ConvertFrom-LastJsonLine -Value $serverStatusBeforeProcess.stdout
$serverWasReady = $serverStatusBeforeProcess.exitCode -eq 0 -and $serverStatusBefore.state -eq "ready"
$serverStartedByStage = $false
$scriptError = $null
$cleanupError = $null
$candidateRecords = [Collections.Generic.List[object]]::new()
$generatedOutputPaths = [Collections.Generic.List[string]]::new()
$inputPaths = [Collections.Generic.List[string]]::new()
$serverStatusForRun = $null
$serverLogPath = $null
$serverLogStartOffset = 0L
$loadedBeforeIds = @()
$loadedAfterIds = @()
$inputBuildSummary = $null
$logScanSummary = $null

try {
    if (-not $serverWasReady) {
        $startServer = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
            "server", "start"
        )
        if ($startServer.exitCode -ne 0) {
            throw "Foundry Local server start failed."
        }
        $serverStartedByStage = $true
    }
    $serverStatusProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
        "server", "status", "--output", "json"
    )
    $serverStatusForRun = ConvertFrom-LastJsonLine -Value $serverStatusProcess.stdout
    if ($serverStatusProcess.exitCode -ne 0 -or $serverStatusForRun.state -ne "ready" -or
        @($serverStatusForRun.webUrls).Count -ne 1) {
        throw "Foundry Local server did not expose exactly one ready endpoint."
    }
    $endpoint = [Uri]$serverStatusForRun.webUrls[0]
    $endpointAddress = $null
    if ($endpoint.Scheme -ne "http" -or
        -not [Net.IPAddress]::TryParse($endpoint.Host, [ref]$endpointAddress) -or
        -not [Net.IPAddress]::IsLoopback($endpointAddress)) {
        throw "Foundry Local REST endpoint is not literal HTTP loopback."
    }
    $diagnosticReplacements[$endpoint.AbsoluteUri] = "<FOUNDRY_ENDPOINT>"
    $endpointAuthority = $endpoint.GetLeftPart([UriPartial]::Authority)
    if (-not $diagnosticReplacements.ContainsKey($endpointAuthority)) {
        $diagnosticReplacements[$endpointAuthority] = "<FOUNDRY_ENDPOINT>"
    }
    $foundryDaemonId = [int]$serverStatusForRun.pid
    $daemonProcess = [Diagnostics.Process]::GetProcessById($foundryDaemonId)
    try {
        if ($daemonProcess.ProcessName -ne "foundrylocald") {
            throw "Foundry Local status identified an unexpected daemon process."
        }
    }
    finally {
        $daemonProcess.Dispose()
    }
    $serverLogPath = [string]$serverStatusForRun.logFile
    if (-not (Test-Path -LiteralPath $serverLogPath -PathType Leaf)) {
        throw "Foundry Local daemon log is not readable for the privacy scan."
    }
    $serverLogStartOffset = [int64](Get-Item -LiteralPath $serverLogPath).Length

    $loadedBeforeIds = @(Get-LoadedModelId -FoundryPath $foundryPath -RepositoryRoot $repositoryRoot)
    if ($loadedBeforeIds.Count -ne 0) {
        throw "ST-011 requires zero preloaded models so that timing and memory observations are isolated."
    }

    $inputDirectory = Join-Path $workRoot "inputs"
    $prepareProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
        $driverPath, "prepare-inputs",
        "--control-config", $controlConfigPath,
        "--asr-artifact", $asrArtifactPath,
        "--asr-artifact-sha256", $cudaAsrCandidate.artifact.sha256,
        "--output-directory", $inputDirectory
    )
    $prepareDiagnosticPath = Join-Path $workRoot "prepare-inputs.stderr.txt"
    Write-Diagnostic -Path $prepareDiagnosticPath -Value $prepareProcess.stderr -Replacements $diagnosticReplacements
    if ($prepareProcess.exitCode -ne 0) {
        throw "Local text-model input preparation failed."
    }
    $inputBuildSummary = ConvertFrom-LastJsonLine -Value $prepareProcess.stdout
    if ($inputBuildSummary.status -ne "succeeded" -or
        [int]$inputBuildSummary.fullFixtureTokenCount -ne [int]$cudaAsrCandidate.fixture.wordCount -or
        $inputBuildSummary.sourceArtifactSha256 -ne $cudaAsrCandidate.artifact.sha256 -or
        $inputBuildSummary.sourceFixtureSha256 -ne $asrRaw.payload.analysisInput.normalizedFixtureAudio.sha256 -or
        @($inputBuildSummary.fixtureWindows).Count -ne 3 -or
        $inputBuildSummary.windowsArePairwiseDisjoint -ne $true -or
        $inputBuildSummary.fullFixtureSemanticCoverageClaimed -ne $false) {
        throw "Local text-model input summary is inconsistent with immutable ASR evidence."
    }

    $datasets = @(
        [pscustomobject]@{ datasetId = "synthetic-control"; inputKind = "synthetic-safe-mechanism-control"; path = (Join-Path $inputDirectory "input-synthetic-control.json") },
        [pscustomobject]@{ datasetId = "fixture-start"; inputKind = "rights-unconfirmed-fixture-window"; path = (Join-Path $inputDirectory "input-fixture-start.json") },
        [pscustomobject]@{ datasetId = "fixture-middle"; inputKind = "rights-unconfirmed-fixture-window"; path = (Join-Path $inputDirectory "input-fixture-middle.json") },
        [pscustomobject]@{ datasetId = "fixture-end"; inputKind = "rights-unconfirmed-fixture-window"; path = (Join-Path $inputDirectory "input-fixture-end.json") }
    )
    foreach ($dataset in $datasets) {
        if (-not (Test-Path -LiteralPath $dataset.path -PathType Leaf)) {
            throw "A prepared local text-model input is missing."
        }
        $inputPaths.Add($dataset.path)
    }

    foreach ($spec in $modelSpecs) {
        $modelWork = Join-Path $workRoot $spec.candidateId
        [IO.Directory]::CreateDirectory($modelWork) | Out-Null
        $modelLoadedByStage = $false
        $loadProcess = $null
        $loadDiagnosticPath = Join-Path $modelWork "model-load.stderr.txt"
        $datasetRecords = [Collections.Generic.List[object]]::new()
        try {
            $loadProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 600 -WorkingDirectory $repositoryRoot -MonitorProcessIds @($foundryDaemonId) -Arguments @(
                "model", "load", $spec.modelId
            )
            Write-Diagnostic -Path $loadDiagnosticPath -Value $loadProcess.stderr -Replacements $diagnosticReplacements
            if ($loadProcess.exitCode -ne 0) {
                $candidateRecords.Add([pscustomobject][ordered]@{
                    candidateId = $spec.candidateId
                    modelId = $spec.modelId
                    status = "model-load-failed"
                    load = [ordered]@{
                        attempted = $true
                        succeeded = $false
                        processMeasurement = Get-SafeProcessMeasurement -Measurement $loadProcess
                        diagnostic = Get-FileDescriptor -Path $loadDiagnosticPath
                    }
                    datasets = @()
                    controlMechanismPassed = $false
                    allFixtureWindowsStructurallyValid = $false
                    structurallyEligibleForHumanReview = $false
                    semanticGroundednessMeasured = $false
                    generatedWordingAccuracyMeasured = $false
                    productionThresholdApproved = $false
                })
                continue
            }
            $modelLoadedByStage = $true
            $loadedNow = @(Get-LoadedModelId -FoundryPath $foundryPath -RepositoryRoot $repositoryRoot)
            if ($loadedNow.Count -ne 1 -or $loadedNow[0] -ne $spec.modelId) {
                throw "Foundry Local did not load exactly the requested model variant."
            }

            foreach ($dataset in $datasets) {
                $responsePath = Join-Path $modelWork "$($dataset.datasetId)-response.json"
                $generatedPath = Join-Path $modelWork "$($dataset.datasetId)-generated.json"
                $diagnosticPath = Join-Path $modelWork "$($dataset.datasetId).stderr.txt"
                $inferenceProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -MonitorProcessIds @($foundryDaemonId) -Arguments @(
                    $driverPath, "invoke",
                    "--endpoint", $endpoint.AbsoluteUri,
                    "--model-id", $spec.modelId,
                    "--input", $dataset.path,
                    "--output-schema", $outputSchemaPath,
                    "--control-config", $controlConfigPath,
                    "--response-output", $responsePath,
                    "--generated-output", $generatedPath,
                    "--http-timeout-seconds", "900"
                )
                Write-Diagnostic -Path $diagnosticPath -Value $inferenceProcess.stderr -Replacements $diagnosticReplacements
                if ($inferenceProcess.exitCode -ne 0) {
                    throw "The local text-model REST driver failed internally."
                }
                $invocation = ConvertFrom-LastJsonLine -Value $inferenceProcess.stdout
                if ($invocation.datasetId -ne $dataset.datasetId -or $invocation.modelId -ne $spec.modelId -or
                    $invocation.inputSha256 -ne (Get-FileSha256 -Path $dataset.path) -or
                    $invocation.outputSchemaSha256 -ne (Get-FileSha256 -Path $outputSchemaPath)) {
                    throw "The local text-model invocation summary does not match its request."
                }
                $validation = $null
                if ($invocation.status -eq "succeeded") {
                    if (-not (Test-Path -LiteralPath $generatedPath -PathType Leaf)) {
                        throw "A successful local text-model invocation did not persist its generated JSON."
                    }
                    $schemaValid = (Get-Content -LiteralPath $generatedPath -Raw -Encoding utf8) |
                        Test-Json -SchemaFile $outputSchemaPath -ErrorAction SilentlyContinue
                    $validationProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
                        $driverPath, "validate-output",
                        "--input", $dataset.path,
                        "--output", $generatedPath,
                        "--control-config", $controlConfigPath,
                        "--schema-valid", ([string][bool]$schemaValid).ToLowerInvariant()
                    )
                    if ($validationProcess.exitCode -ne 0) {
                        throw "The local text-model grounded-ID validator failed internally."
                    }
                    $validation = ConvertFrom-LastJsonLine -Value $validationProcess.stdout
                    if ($validation.datasetId -ne $dataset.datasetId -or
                        $validation.generatedOutputSha256 -ne (Get-FileSha256 -Path $generatedPath)) {
                        throw "The grounded-ID validation summary does not identify its generated output."
                    }
                    $generatedOutputPaths.Add($generatedPath)
                }
                $datasetRecords.Add([pscustomobject][ordered]@{
                    datasetId = $dataset.datasetId
                    inputKind = $dataset.inputKind
                    input = Get-FileDescriptor -Path $dataset.path
                    invocation = $invocation
                    validation = $validation
                    processMeasurement = Get-SafeProcessMeasurement -Measurement $inferenceProcess
                    diagnostic = Get-FileDescriptor -Path $diagnosticPath
                })
            }

            $controlRecord = $datasetRecords | Where-Object datasetId -eq "synthetic-control" | Select-Object -First 1
            $fixtureRecords = @($datasetRecords | Where-Object inputKind -eq "rights-unconfirmed-fixture-window")
            $invocationSuccessCount = @($datasetRecords | Where-Object { $_.invocation.status -eq "succeeded" }).Count
            $schemaValidCount = @($datasetRecords | Where-Object { $null -ne $_.validation -and $_.validation.outputSchemaValid -eq $true }).Count
            $structuralValidationPassCount = @($datasetRecords | Where-Object { $null -ne $_.validation -and $_.validation.structuralValidationPassed -eq $true }).Count
            $directJsonCount = @($datasetRecords | Where-Object { $_.invocation.status -eq "succeeded" -and $_.invocation.directJson -eq $true }).Count
            $adapterCount = @($datasetRecords | Where-Object { $_.invocation.status -eq "succeeded" -and $_.invocation.boundedAdapterUsed -eq $true }).Count
            $controlMechanismPassed = $null -ne $controlRecord.validation -and $controlRecord.validation.controlMechanismPassed -eq $true
            $allFixtureWindowsStructurallyValid = $fixtureRecords.Count -eq 3 -and
                @($fixtureRecords | Where-Object { $null -ne $_.validation -and $_.validation.structuralValidationPassed -eq $true }).Count -eq 3
            $gpuMemoryPeaks = @($datasetRecords |
                ForEach-Object { $_.processMeasurement.systemWideGpuMemoryUsedMiBPeak } |
                Where-Object { $null -ne $_ })
            $maximumGpuMemoryPeak = if ($gpuMemoryPeaks.Count -eq 0) {
                $null
            }
            else {
                [int64](($gpuMemoryPeaks | Measure-Object -Maximum).Maximum)
            }
            $candidateRecords.Add([pscustomobject][ordered]@{
                candidateId = $spec.candidateId
                modelId = $spec.modelId
                status = "evaluated"
                load = [ordered]@{
                    attempted = $true
                    succeeded = $true
                    isolatedFromPreloadedModels = $true
                    processMeasurement = Get-SafeProcessMeasurement -Measurement $loadProcess
                    diagnostic = Get-FileDescriptor -Path $loadDiagnosticPath
                }
                datasets = @($datasetRecords)
                aggregate = [ordered]@{
                    datasetCount = $datasetRecords.Count
                    invocationSuccessCount = $invocationSuccessCount
                    outputSchemaValidCount = $schemaValidCount
                    structuralValidationPassCount = $structuralValidationPassCount
                    directJsonCount = $directJsonCount
                    boundedAdapterCount = $adapterCount
                    totalInferenceProcessMilliseconds = [math]::Round((@($datasetRecords | ForEach-Object { [double]$_.processMeasurement.elapsedMilliseconds }) | Measure-Object -Sum).Sum, 3)
                    maximumPeakAggregateWorkingSetBytes = [int64]((@($datasetRecords | ForEach-Object { [int64]$_.processMeasurement.peakAggregateWorkingSetBytes }) | Measure-Object -Maximum).Maximum)
                    maximumSystemWideGpuMemoryUsedMiBPeak = $maximumGpuMemoryPeak
                }
                controlMechanismPassed = $controlMechanismPassed
                allFixtureWindowsStructurallyValid = $allFixtureWindowsStructurallyValid
                structurallyEligibleForHumanReview = $controlMechanismPassed -and $allFixtureWindowsStructurallyValid
                semanticGroundednessMeasured = $false
                generatedWordingAccuracyMeasured = $false
                productionThresholdApproved = $false
            })
        }
        finally {
            if ($modelLoadedByStage) {
                $unloadProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
                    "model", "unload", $spec.modelId
                )
                if ($unloadProcess.exitCode -ne 0) {
                    throw "Foundry Local model cleanup failed."
                }
            }
        }
        $loadedAfterCandidate = @(Get-LoadedModelId -FoundryPath $foundryPath -RepositoryRoot $repositoryRoot)
        if ($loadedAfterCandidate.Count -ne 0) {
            throw "Foundry Local model state was not restored after a candidate evaluation."
        }
        $postManifest = Get-DirectoryManifest -Root $modelPaths[$spec.modelId]
        if ($postManifest.manifestSha256 -ne $spec.expectedManifestSha256 -or
            $postManifest.fileCount -ne $spec.expectedFileCount -or
            $postManifest.totalBytes -ne $spec.expectedTotalBytes) {
            throw "A Foundry Local model cache changed during ST-011."
        }
    }

    $logScanArguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @($driverPath, "scan-log", "--log", $serverLogPath, "--start-offset", [string]$serverLogStartOffset)) {
        $logScanArguments.Add($argument)
    }
    foreach ($path in $inputPaths) {
        $logScanArguments.Add("--input")
        $logScanArguments.Add($path)
    }
    foreach ($path in $generatedOutputPaths) {
        $logScanArguments.Add("--output")
        $logScanArguments.Add($path)
    }
    $logScanProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @($logScanArguments)
    if ($logScanProcess.exitCode -ne 0) {
        throw "Foundry daemon log privacy scan failed."
    }
    $logScanSummary = ConvertFrom-LastJsonLine -Value $logScanProcess.stdout
    if ($logScanSummary.scanned -ne $true -or
        $logScanSummary.exactInputOrGeneratedProbeLeakObserved -ne $false -or
        [int]$logScanSummary.requestCanaryMatchCount -ne 0 -or
        [int]$logScanSummary.privateProbeMatchCount -ne 0) {
        throw "Foundry daemon log contained an exact request or generated-output probe."
    }
}
catch {
    $scriptError = $_
}
finally {
    try {
        if ($null -ne $serverStatusForRun) {
            foreach ($spec in $modelSpecs) {
                $loadedForCleanup = @(Get-LoadedModelId -FoundryPath $foundryPath -RepositoryRoot $repositoryRoot)
                if ($loadedForCleanup -contains $spec.modelId) {
                    $cleanupUnload = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
                        "model", "unload", $spec.modelId
                    )
                    if ($cleanupUnload.exitCode -ne 0) {
                        throw "Foundry Local backstop model cleanup failed."
                    }
                }
            }
        }
        if ($serverStartedByStage) {
            $stopServer = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 300 -WorkingDirectory $repositoryRoot -Arguments @(
                "server", "stop"
            )
            if ($stopServer.exitCode -ne 0) {
                throw "Foundry Local server cleanup failed."
            }
        }
    }
    catch {
        $cleanupError = $_
    }
}

if ($null -ne $scriptError -or $null -ne $cleanupError) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    if ($null -ne $scriptError) {
        throw $scriptError
    }
    throw $cleanupError
}

if ($serverWasReady) {
    $serverStatusAfterProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
        "server", "status", "--output", "json"
    )
    $serverStatusAfter = ConvertFrom-LastJsonLine -Value $serverStatusAfterProcess.stdout
    $serverReadyAfter = $serverStatusAfterProcess.exitCode -eq 0 -and $serverStatusAfter.state -eq "ready"
    if (-not $serverReadyAfter) {
        throw "Foundry Local server state was not restored to ready."
    }
    $loadedAfterIds = @(Get-LoadedModelId -FoundryPath $foundryPath -RepositoryRoot $repositoryRoot)
}
else {
    $serverStatusAfterProcess = Invoke-MeasuredProcess -FileName $foundryPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
        "server", "status", "--output", "json"
    )
    $serverStatusAfter = ConvertFrom-LastJsonLine -Value $serverStatusAfterProcess.stdout
    $serverReadyAfter = $serverStatusAfter.state -eq "ready"
    if ($serverReadyAfter) {
        throw "Foundry Local server state was not restored to stopped."
    }
    $loadedAfterIds = @()
}
if ($loadedAfterIds.Count -ne $loadedBeforeIds.Count -or
    (Get-StringSha256 -Value (@($loadedAfterIds) -join "`n")) -ne (Get-StringSha256 -Value (@($loadedBeforeIds) -join "`n"))) {
    throw "Foundry Local loaded-model set was not restored."
}

$workAcl = Get-LocalEvidenceAclEvidence -Path $workRoot
$structurallyEligibleCandidates = @($candidateRecords | Where-Object { $_.structurallyEligibleForHumanReview -eq $true })
$allDatasetRecords = @($candidateRecords | ForEach-Object { @($_.datasets) })
$successfulInvocationRecords = @($allDatasetRecords | Where-Object { $_.invocation.status -eq "succeeded" })
$payload = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        hardwareBenchmarkRunId = $hardware.benchmarkRunId
        hardwareBenchmarkRunSha256 = $hardware.benchmarkRunSha256
        recordedOperatingSystemVersion = $hardware.operatingSystem.version
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        graphics = @($hardware.graphics | ForEach-Object { $_.name })
    }
    implementation = [ordered]@{
        gitHead = $gitHeadProcess.stdout.Trim()
        implementationFilesCleanAtRun = [string]::IsNullOrWhiteSpace($gitStatusProcess.stdout)
        files = $implementationFiles
    }
    scope = [ordered]@{
        benchmarkIsFullRequirementAcceptance = $false
        representativeGroundedJsonContractOnly = $true
        fr006FieldsEvaluated = @("topic-range", "title", "summary", "keywords", "evidence-token-ids")
        fr006FieldsNotEvaluated = @("primary-speaker", "important-statement-candidates", "audience", "publication-warning-candidates")
        fr011MeaningPreservationMeasured = $false
        fr022AnswerSupportMeasured = $false
        fr025FullArtifactSetMeasured = $false
        fullFixtureSemanticCoverageMeasured = $false
    }
    upstream = [ordered]@{
        asrCanonical = [ordered]@{
            fileName = [IO.Path]::GetFileName($asrCanonicalPath)
            sha256 = Get-FileSha256 -Path $asrCanonicalPath
        }
        asrBenchmarkRunId = $asrCanonical.benchmarkRunId
        asrBenchmarkRunSha256 = $asrCanonical.benchmarkRunSha256
        asrArtifact = $cudaAsrCandidate.artifact
        asrPrivateEvidenceAcl = $asrEvidenceAcl
        asrPrivateEvidenceCompletionMarkerPresent = $true
        alignmentCanonical = [ordered]@{
            fileName = [IO.Path]::GetFileName($alignmentCanonicalPath)
            sha256 = Get-FileSha256 -Path $alignmentCanonicalPath
            benchmarkRunId = $alignmentCanonical.benchmarkRunId
            benchmarkRunSha256 = $alignmentCanonical.benchmarkRunSha256
        }
    }
    tooling = [ordered]@{
        pythonVersion = $pythonVersionProcess.stdout.Trim()
        pythonExternalPackagesRequired = $false
        foundryLocal = [ordered]@{
            available = $true
            cliVersion = $versionProcess.stdout.Trim()
            packageVersion = $foundryPackage.Version.ToString()
            architecture = $foundryPackage.Architecture.ToString()
            signatureKind = $foundryPackage.SignatureKind.ToString()
            cliExecutable = Get-FileDescriptor -Path $foundryCli.FullName
            daemonExecutable = Get-FileDescriptor -Path $foundryDaemon.FullName
            preview = $true
            restApiReferenceStatus = "preview-and-subject-to-breaking-change"
            dynamicLoopbackEndpointUsed = $true
            endpointAddressIncludedInPayload = $false
            proxyBypassEnforcedByDriver = $true
        }
        cudaExecutionProvider = [ordered]@{
            fileCount = $cudaEp.fileCount
            totalBytes = $cudaEp.totalBytes
            manifestRule = $cudaEp.manifestRule
            manifestSha256 = $cudaEp.manifestSha256
            versionFileSha256 = Get-FileSha256 -Path $cudaEpVersionPath
            releaseRedistributionApproved = $false
        }
    }
    analysisInput = [ordered]@{
        sourceAssetId = $fixtureManifest.sourceAssets[0].id
        sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
        samplePathReadOnlyHashValidated = $true
        modelInputDerivedFromFixtureArtifact = $true
        control = [ordered]@{
            controlId = $controlConfig.controlId
            configSha256 = Get-FileSha256 -Path $controlConfigPath
            outputSchemaSha256 = Get-FileSha256 -Path $outputSchemaPath
            input = $inputBuildSummary.control.input
            tokenCount = [int]$inputBuildSummary.control.tokenCount
            characterCount = [int]$inputBuildSummary.control.characterCount
            promptInjectionControlIncluded = $true
            controlIsAccuracyEvidence = $false
        }
        fixture = [ordered]@{
            sourceCandidateId = $inputBuildSummary.sourceCandidateId
            sourceArtifactSha256 = $inputBuildSummary.sourceArtifactSha256
            sourceFixtureSha256 = $inputBuildSummary.sourceFixtureSha256
            fullFixtureTokenCount = [int]$inputBuildSummary.fullFixtureTokenCount
            fullFixtureSegmentCount = [int]$inputBuildSummary.fullFixtureSegmentCount
            fullFixtureCharacterCount = [int]$inputBuildSummary.fullFixtureCharacterCount
            windows = @($inputBuildSummary.fixtureWindows)
            selectedTokenCountAcrossWindows = [int]$inputBuildSummary.selectedTokenCountAcrossWindows
            pairwiseWindowTokenOverlapCount = [int]$inputBuildSummary.pairwiseWindowTokenOverlapCount
            windowsArePairwiseDisjoint = [bool]$inputBuildSummary.windowsArePairwiseDisjoint
            fullFixtureSemanticCoverageClaimed = $false
        }
    }
    models = @($modelRecords)
    evaluations = @($candidateRecords)
    comparison = [ordered]@{
        candidateCount = $candidateRecords.Count
        datasetCountPerEvaluatedCandidate = 4
        successfulInvocationCount = $successfulInvocationRecords.Count
        directJsonCount = @($successfulInvocationRecords | Where-Object { $_.invocation.directJson -eq $true }).Count
        boundedAdapterCount = @($successfulInvocationRecords | Where-Object { $_.invocation.boundedAdapterUsed -eq $true }).Count
        structurallyEligibleCandidateCount = $structurallyEligibleCandidates.Count
        structurallyEligibleCandidateIds = @($structurallyEligibleCandidates | ForEach-Object { $_.candidateId })
        performanceTrialCountPerModelAndDataset = 1
        performanceDistributionMeasured = $false
        toolCallingDeclaredByCatalogButNotUsedAsSchemaGuarantee = $true
        controlInjectionMarkerAbsenceIsRobustnessEvidence = $false
        generatedOutputSchemaValidationIsSemanticGroundednessEvidence = $false
        groundedIdExistenceAndRangeValidationIsSemanticGroundednessEvidence = $false
        fixtureHumanSemanticReferenceAvailable = $false
        semanticGroundednessMeasured = $false
        generatedWordingAccuracyMeasured = $false
        productionThresholdsApproved = $false
    }
    stateRestoration = [ordered]@{
        serverWasReadyBefore = $serverWasReady
        serverStartedByStage = $serverStartedByStage
        serverReadyAfter = $serverReadyAfter
        preloadedModelCount = $loadedBeforeIds.Count
        loadedModelCountAfter = $loadedAfterIds.Count
        loadedModelSetHashBefore = Get-StringSha256 -Value (@($loadedBeforeIds) -join "`n")
        loadedModelSetHashAfter = Get-StringSha256 -Value (@($loadedAfterIds) -join "`n")
        preAndPostStateMatch = $true
        eachCandidateUnloadedBeforeNextCandidate = $true
    }
    manualFallback = [ordered]@{
        required = $true
        automaticFallbackExecutedByBenchmark = $false
        manualEntryPathSpecifiedInImplementationPlan = $true
        manualEntryPathImplementedByBenchmark = $false
        manualEntryPathExecutedByBenchmark = $false
        triggerConditions = @(
            "model-unavailable-or-load-failure",
            "generated-output-not-json",
            "output-schema-invalid",
            "grounded-id-invalid",
            "semantic-review-required"
        )
        method = "Human authors topic metadata and optional hook, quiz, and publishing wording while selecting source token spans."
        generatedSuggestionsAutomaticallyAdopted = $false
    }
    disposition = [ordered]@{
        status = if ($structurallyEligibleCandidates.Count -gt 0) {
            "structural-candidates-awaiting-human-semantic-review"
        }
        else {
            "manual-only-until-local-model-output-is-structurally-valid"
        }
        localModelAdoptedForProduction = $false
        candidateOutputsAreUnapprovedSuggestions = $true
        generatedSuggestionsAutomaticallyAdopted = $false
        semanticGroundednessMeasured = $false
        finalStage0DecisionPending = $true
    }
    privacy = [ordered]@{
        localEvidenceAcl = $workAcl
        sourceTranscriptTextIncludedInPayload = $false
        sourceTranscriptTextIncludedInCanonicalSummary = $false
        localArtifactsContainTranscriptText = $true
        localArtifactsContainGeneratedOutput = $true
        modelInputPlacedOnProcessCommandLine = $false
        restEndpointWasLoopbackOnly = $true
        proxyBypassEnforced = $true
        daemonLogScan = $logScanSummary
        daemonLogAclControlledByBenchmark = $false
        absoluteHostPathsIncludedInPayload = $false
        privateEvidenceRemainsIgnored = $true
        workDirectoryWasEmptyAfterAclProtection = $true
    }
    samplePathConsumed = $true
}

$payloadJson = $payload | ConvertTo-Json -Depth 30
if ($payloadJson -match '"(?:text|content|transcript|utterance|rawResponse|prompt)"\s*:') {
    throw "The local text-model payload contains a forbidden content-bearing property."
}
Write-LocalTextAtomically -Path (Join-Path $workRoot "stage-payload.json") -Value $payloadJson
Write-LocalTextAtomically -Path (Join-Path $workRoot "completed.json") -Value (([ordered]@{
    schemaVersion = "1.0.0"
    completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
    asrArtifactSha256 = $cudaAsrCandidate.artifact.sha256
    candidateCount = $candidateRecords.Count
    generatedOutputArtifactCount = $generatedOutputPaths.Count
}) | ConvertTo-Json -Compress)
$payloadJson
