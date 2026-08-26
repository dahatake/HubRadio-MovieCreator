#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$SamplePath = (Join-Path $PSScriptRoot "..\sample\20260615_024326000_iOS.MOV")
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Invoke-VersionCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    $output = & $command.Source @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return (($output | Select-Object -First 1) -join "").Trim()
}

$resolvedSample = Resolve-Path $SamplePath
$sample = Get-Item $resolvedSample
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$computer = Get-CimInstance Win32_ComputerSystem
$gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object {
    [ordered]@{
        name = $_.Name
        wmiAdapterRamBytes = if ($null -eq $_.AdapterRAM) { $null } else { [int64]$_.AdapterRAM }
        driverVersion = $_.DriverVersion
    }
})

$nvidiaMemory = $null
$nvidiaDriver = $null
$nvidiaSmi = Get-Command "nvidia-smi" -ErrorAction SilentlyContinue
if ($null -ne $nvidiaSmi) {
    $nvidiaLine = (& $nvidiaSmi.Source --query-gpu=driver_version,memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1)
    if ($nvidiaLine -match '^\s*([^,]+),\s*(\d+)\s*$') {
        $nvidiaDriver = $Matches[1].Trim()
        $nvidiaMemory = [int64]$Matches[2]
    }
}

$npuDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match "NPU|Neural|AI Boost|Hexagon" } |
    ForEach-Object {
        [ordered]@{
            class = $_.Class
            friendlyName = $_.FriendlyName
            status = $_.Status
        }
    })

$webView2 = $null
foreach ($root in @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients",
    "HKCU:\Software\Microsoft\EdgeUpdate\Clients"
)) {
    if (-not (Test-Path $root)) {
        continue
    }

    foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
        $value = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
        if ($value.name -match "WebView2") {
            $webView2 = $value.pv
            break
        }
    }

    if ($null -ne $webView2) {
        break
    }
}

$dotnetSdks = @()
if ($null -ne (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    $dotnetSdks = @(& dotnet --list-sdks 2>$null | ForEach-Object { ($_ -split '\s+')[0] })
}

$result = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    collection = [ordered]@{
        script = "benchmarks/collect-hardware.ps1"
        shell = "pwsh.exe -NoLogo -NoProfile"
        powerShellVersion = $PSVersionTable.PSVersion.ToString()
        containsPersonalIdentifiers = $false
    }
    operatingSystem = [ordered]@{
        caption = $os.Caption
        version = $os.Version
        buildNumber = $os.BuildNumber
        architecture = $os.OSArchitecture
    }
    processor = [ordered]@{
        name = $cpu.Name
        physicalCores = [int]$cpu.NumberOfCores
        logicalProcessors = [int]$cpu.NumberOfLogicalProcessors
    }
    memory = [ordered]@{
        installedBytes = [int64]$computer.TotalPhysicalMemory
    }
    graphics = $gpus
    nvidia = [ordered]@{
        reportedMemoryMiB = $nvidiaMemory
        driverVersion = $nvidiaDriver
        authority = "nvidia-smi is authoritative for NVIDIA dedicated memory; WMI AdapterRAM is retained only as an observed value."
    }
    npuDevices = $npuDevices
    storage = [ordered]@{
        systemDrive = $env:SystemDrive
        freeBytesAtCollection = [int64](Get-PSDrive $env:SystemDrive.TrimEnd(':')).Free
    }
    runtimesAndTools = [ordered]@{
        dotnetSdkDefault = Invoke-VersionCommand "dotnet" @("--version")
        dotnetSdks = $dotnetSdks
        node = Invoke-VersionCommand "node" @("--version")
        npm = Invoke-VersionCommand "npm" @("--version")
        pythonDefault = Invoke-VersionCommand "python" @("--version")
        ffmpeg = Invoke-VersionCommand "ffmpeg" @("-version")
        ffprobe = Invoke-VersionCommand "ffprobe" @("-version")
        git = Invoke-VersionCommand "git" @("--version")
        winget = Invoke-VersionCommand "winget" @("--version")
        foundryCommandAvailable = $null -ne (Get-Command "foundry" -ErrorAction SilentlyContinue)
        azureCliCommandAvailable = $null -ne (Get-Command "az" -ErrorAction SilentlyContinue)
        webView2Runtime = $webView2
    }
    sampleAsset = [ordered]@{
        relativePath = "sample/20260615_024326000_iOS.MOV"
        byteSize = [int64]$sample.Length
        sha256 = (Get-FileHash $sample.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$result | ConvertTo-Json -Depth 8
