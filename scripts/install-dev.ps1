# Installs the service from a publish folder without building the installer. Run from an elevated prompt.
#   .\scripts\publish.ps1 -NoInstaller
#   .\scripts\install-dev.ps1            # install or update
#   .\scripts\install-dev.ps1 -Remove    # stop, unregister and delete the copied files
#
# The files are copied to %ProgramFiles%\NetLedger-dev first. A SYSTEM service must never run from a folder a normal
# user can write to (such as this repository), or anything running as that user could replace the exe.
param([switch]$Remove, [string]$PublishDir = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\publish\win-x64'))

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated (administrator) PowerShell.'
}

$name = 'NetLedger'
$target = Join-Path $env:ProgramFiles 'NetLedger-dev'
$existing = Get-Service $name -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $name -ErrorAction SilentlyContinue
}

if ($Remove) {
    if ($existing) {
        sc.exe delete $name | Out-Null
        logman.exe stop NetLedger-KernelNetwork -ets 2>$null | Out-Null
    }

    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Service removed. Recorded data is kept in' (Join-Path $env:ProgramData 'NetLedger')
    return
}

$source = Resolve-Path $PublishDir
if (-not (Test-Path (Join-Path $source 'NetLedger.Service.exe'))) {
    throw "No published service in $source; run scripts\publish.ps1 -NoInstaller first."
}

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source '*') $target -Recurse -Force
$exe = Join-Path $target 'NetLedger.Service.exe'

# The image path keeps its own quotes; an unquoted path with spaces lets Windows try C:\Program.exe first.
$imagePath = "`"$exe`""
if ($existing) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name ImagePath -Value $imagePath
}
else {
    New-Service -Name $name -BinaryPathName $imagePath -DisplayName 'NetLedger' -StartupType Automatic `
        -Description 'Records network usage per hour, adapter and app for NetLedger.' | Out-Null
    sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
}

Start-Service $name
Get-Service $name | Format-Table Name, Status, StartType
