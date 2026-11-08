# Clean Release build + distribution package:
#   dist\vPilotRelay-<version>\   (vPilotRelay-Setup-<version>.exe, vPilotRelay.dll, vPilotRelay.ini, README.md, LICENSE)
#   dist\vPilotRelay-<version>.zip
#
# The version number lives in Directory.Build.props.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-VpilotDir "D:\vPilot"]

param(
    [string]$VpilotDir = "$env:LOCALAPPDATA\vPilot"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

foreach ($dll in 'RossCarlson.Vatsim.Vpilot.Plugins.dll', 'Newtonsoft.Json.dll', 'Microsoft.FlightSimulator.SimConnect.dll') {
    if (-not (Test-Path (Join-Path $VpilotDir $dll))) {
        throw "$dll not found in $VpilotDir. Is vPilot installed? If it is elsewhere, pass -VpilotDir."
    }
}

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$installer = Join-Path $root 'Installer'
Remove-Item -Recurse -Force (Join-Path $root 'bin'), (Join-Path $root 'obj'), (Join-Path $installer 'bin'), (Join-Path $installer 'obj') -ErrorAction SilentlyContinue

# 1) plugin
dotnet build (Join-Path $root 'vPilotRelay.csproj') -c Release "-p:VpilotDir=$VpilotDir" -nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

# 2) setup wizard (embeds the plugin DLL built above)
dotnet build (Join-Path $installer 'vPilotRelaySetup.csproj') -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "Setup wizard build failed." }

$dist = Join-Path $root 'dist'
$pkg = Join-Path $dist "vPilotRelay-$version"
Remove-Item -Recurse -Force $pkg, "$pkg.zip" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pkg | Out-Null

Copy-Item (Join-Path $installer 'bin\Release\net48\vPilotRelaySetup.exe') (Join-Path $pkg "vPilotRelay-Setup-$version.exe")
Copy-Item (Join-Path $root 'bin\Release\net48\vPilotRelay.dll') $pkg
Copy-Item (Join-Path $root 'vPilotRelay.ini') $pkg
Copy-Item (Join-Path $root 'README.md') $pkg
Copy-Item (Join-Path $root 'LICENSE') $pkg

Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath "$pkg.zip"

Write-Host ""
Write-Host "Package ready:" -ForegroundColor Green
Get-ChildItem $pkg | ForEach-Object { Write-Host "  $($_.Name)  ($($_.Length) bytes)" }
Write-Host "  $pkg.zip"
