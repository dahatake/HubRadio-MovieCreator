#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$PackageVersion = '2.4.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeRoot = Join-Path $env:TEMP (
    'HubRadio-ProgressRing-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot | Out-Null

try {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $probeRoot

    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.26100.0</TargetPlatformMinVersion>
    <RootNamespace>ProgressRingProbe</RootNamespace>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="$PackageVersion" />
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $probeRoot 'ProgressRingProbe.csproj') `
        -Value $project -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $probeRoot 'App.xaml') -Encoding utf8NoBOM -Value @'
<Application
    x:Class="ProgressRingProbe.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
'@
    Set-Content -LiteralPath (Join-Path $probeRoot 'App.xaml.cs') -Encoding utf8NoBOM -Value @'
using Microsoft.UI.Xaml;

namespace ProgressRingProbe;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
'@
    Set-Content -LiteralPath (Join-Path $probeRoot 'MainWindow.xaml') -Encoding utf8NoBOM -Value @'
<Window
    x:Class="ProgressRingProbe.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="ProgressRing compatibility probe">
    <Grid>
        <ProgressRing Width="32" Height="32" IsActive="True" />
    </Grid>
</Window>
'@
    Set-Content -LiteralPath (Join-Path $probeRoot 'MainWindow.xaml.cs') -Encoding utf8NoBOM -Value @'
using Microsoft.UI.Xaml;

namespace ProgressRingProbe;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
'@

    & dotnet.exe build (Join-Path $probeRoot 'ProgressRingProbe.csproj') `
        --configuration Release `
        -p:Platform=x64 `
        -p:TreatWarningsAsErrors=true `
        -warnaserror `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "ProgressRing probe build failed for Windows App SDK $PackageVersion."
    }

    $appPath = Join-Path $probeRoot (
        'bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\ProgressRingProbe.exe')
    $process = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru
    try {
        $exitedDuringStartup = $process.WaitForExit(10000)
        $process.Refresh()
        $compatible = -not $exitedDuringStartup -and
            $process.MainWindowHandle -ne [IntPtr]::Zero
        [pscustomobject]@{
            PackageVersion = $PackageVersion
            Compatible = $compatible
            ExitedDuringStartup = $exitedDuringStartup
            ExitCode = if ($exitedDuringStartup) { $process.ExitCode } else { $null }
            MainWindowCreated = $process.MainWindowHandle -ne [IntPtr]::Zero
        }
    }
    finally {
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }
        $process.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
