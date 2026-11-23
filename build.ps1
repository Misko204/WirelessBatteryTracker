<#
.SYNOPSIS
  Builds Wireless device battery tracker and packages it into a Windows installer.

.DESCRIPTION
  1. Publishes a self-contained, single-file x64 build (no .NET install needed on the target PC) to .\publish
  2. Compiles installer\installer.iss with Inno Setup into .\dist\WirelessBatteryTracker-Setup-<version>.exe

  The version comes from <Version> in src\WirelessBatteryTracker\WirelessBatteryTracker.csproj.

  Requirements: .NET 10 SDK and Inno Setup 6
    winget install Microsoft.DotNet.SDK.10
    winget install JRSoftware.InnoSetup
#>
param(
    [string]$Version  # optional override, e.g. from a release tag
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\WirelessBatteryTracker\WirelessBatteryTracker.csproj'

if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
Write-Host "Building version $Version"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw ".NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10" }

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Publish from a short temporary path: deep folders can exceed Windows' 260-character path limit.
$work = Join-Path ([IO.Path]::GetTempPath()) 'wdbt-build'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
robocopy (Join-Path $root 'src\WirelessBatteryTracker') $work /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
& $dotnet publish (Join-Path $work 'WirelessBatteryTracker.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -o (Join-Path $work 'publish')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Copy-Item (Join-Path $work 'publish') $publish -Recurse
Remove-Item $work -Recurse -Force

& $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\installer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Join-Path $root "dist\WirelessBatteryTracker-Setup-$Version.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
