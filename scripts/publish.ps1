# Publishes the tray app and the service into one folder (framework-dependent: needs the .NET 10 Desktop Runtime),
# and builds the installer when Inno Setup is available.
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root "artifacts\publish\$Runtime"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'NetLedger.Tray', 'NetLedger.Service') {
    dotnet publish (Join-Path $root "src\$project") -c $Configuration -r $Runtime --self-contained false -o $out -p:DebugType=none -nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "Published NetLedger $version to $out"

if ($NoInstaller) { return }
$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found; skipping the installer. Install it with: winget install JRSoftware.InnoSetup'
    return
}

& $iscc "/DAppVersion=$version" "/DPublishDir=$out" (Join-Path $root 'installer\NetLedger.iss')
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
